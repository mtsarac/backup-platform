using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

public sealed class PostgreSqlReplicaRepairStore(DbContextOptions<PlatformDbContext> options) : IReplicaRepairStore
{
    // Both claim paths serialize admission across all nodes before counting capacity.
    private const long ClaimLockKey = 0x4250_5252_434C_4D01;

    public Task<ReplicaRepairAdmissionResult> AdmitRepairAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid? storageConnectionId,
        string? storageProvider,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Guid? excludeOperationId = null) =>
        EvaluateRepairAsync(tenantId, artifactId, storageConnectionId, storageProvider, now, excludeOperationId, checkQuota: true, cancellationToken);

    private async Task<ReplicaRepairAdmissionResult> EvaluateRepairAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid? storageConnectionId,
        string? storageProvider,
        DateTimeOffset now,
        Guid? excludeOperationId,
        bool checkQuota,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);

        var artifact = await context.BackupArtifacts
            .Include(a => a.Replicas)
            .SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == artifactId, cancellationToken);

        if (artifact is null)
            return new(false, "artifact_not_found", "Artifact not found.");

        if (artifact.State == BackupArtifactState.Deleted)
            return new(false, "artifact_deleted", "Artifact has already been deleted.");

        if (artifact.RetentionExpiresAt is { } expires && expires <= now && artifact.RetentionPinnedAt is null)
            return new(false, ReplicaRepairFailureCodes.ArtifactExpired, "Artifact has expired and cannot be repaired.");

        var attempt = await context.BackupRunAttempts
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == artifact.AttemptId, cancellationToken);

        if (attempt is null)
            return new(false, "attempt_not_found", "Backup attempt not found.");

        var run = await context.BackupRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == attempt.RunId, cancellationToken);

        if (run is null)
            return new(false, "run_not_found", "Backup run not found.");

        var snapshotDestinations = run.Configuration.ResolveEffectiveDestinations();

        BackupDestination? targetDestination = null;
        if (storageConnectionId is { } connId && connId != Guid.Empty)
        {
            targetDestination = snapshotDestinations.FirstOrDefault(d => d.StorageConnectionId == connId);
        }
        else if (!string.IsNullOrWhiteSpace(storageProvider))
        {
            targetDestination = snapshotDestinations.FirstOrDefault(d => string.Equals(d.StorageProvider, storageProvider, StringComparison.OrdinalIgnoreCase));
        }

        if (targetDestination is null)
            return new(false, "unauthorized_destination", "Target destination is not part of the backup run snapshot.");

        StorageConnection? connection = null;
        if (targetDestination.StorageConnectionId is { } targetConnId && targetConnId != Guid.Empty)
        {
            connection = await context.StorageConnections
                .AsNoTracking()
                .SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Id == targetConnId, cancellationToken);

            if (connection is null)
                return new(false, "storage_connection_not_found", "Storage connection not found.");

            if (!connection.EffectiveEnabled)
                return new(false, "storage_connection_disabled", "Storage connection is disabled.");
        }

        var isAlreadyVerified = artifact.Replicas.Any(r =>
            ((targetDestination.StorageConnectionId != null && targetDestination.StorageConnectionId != Guid.Empty)
                ? r.StorageConnectionId == targetDestination.StorageConnectionId
                : string.Equals(r.StorageProvider, targetDestination.StorageProvider, StringComparison.OrdinalIgnoreCase))
            && r.VerificationState == "Verified"
            && r.Status != BackupReplicaStatus.Deleted);

        if (isAlreadyVerified)
            return new(false, ReplicaRepairFailureCodes.AlreadyVerified, "Target destination already has a live verified replica.");

        var effectiveConnectionId = targetDestination.StorageConnectionId ?? Guid.Empty;
        var activeRepairs = await context.ReplicaRepairOperations
            .Where(r => r.TenantId == tenantId && r.ArtifactId == artifactId && r.StorageConnectionId == effectiveConnectionId
                && (excludeOperationId == null || r.Id != excludeOperationId)
                && (r.Status == ReplicaRepairStatus.Pending || r.Status == ReplicaRepairStatus.InProgress))
            .ToListAsync(cancellationToken);

        foreach (var repair in activeRepairs)
        {
            if (repair.Status == ReplicaRepairStatus.InProgress && repair.LeaseExpiresAt <= now)
            {
                repair.MarkStaleFailed("lease_expired", "Repair lease expired before completion.", now);
            }
            else
            {
                await context.SaveChangesAsync(cancellationToken);
                return new(false, "repair_already_in_progress", "A repair is already in progress for this destination.");
            }
        }
        if (activeRepairs.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        var allCandidateSources = ReplicaRepairSourceEvaluator.SelectCandidateSources(artifact, now);
        var candidateSources = allCandidateSources.Where(source =>
            !((targetDestination.StorageConnectionId != null && targetDestination.StorageConnectionId != Guid.Empty)
                ? source.StorageConnectionId == targetDestination.StorageConnectionId
                : string.Equals(source.StorageProvider, targetDestination.StorageProvider, StringComparison.OrdinalIgnoreCase))).ToList();

        if (candidateSources.Count == 0)
            return new(false, ReplicaRepairFailureCodes.NoVerifiedSourceAvailable, "No healthy, verified source replica is available for repair.");

        var selectedSource = candidateSources[0];

        if (!checkQuota)
            return new(true, null, null, artifact, targetDestination, selectedSource, candidateSources);

        // A new repair is only pre-checked here; CreateOperationAsync reserves under the tenant lock. An existing operation is re-reserved
        // atomically, so its hold follows the current policy and artifact state.
        var own = excludeOperationId is { } ownId
            ? await context.ReplicaRepairOperations.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == ownId, cancellationToken)
            : null;
        BackupAdmissionDecision quotaDecision;
        if (own is { IsTerminal: false })
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            quotaDecision = await ReserveQuotaAsync(context, own, now, cancellationToken);
            if (quotaDecision.Allowed)
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        else
        {
            quotaDecision = await PostgreSqlBackupAdmissionPolicy.AdmitReplicaRepairAsync(
                context, tenantId, artifact, targetDestination.StorageProvider, effectiveConnectionId, connection, now, cancellationToken);
        }
        if (!quotaDecision.Allowed)
            return QuotaDenied(quotaDecision);

        return new(true, null, null, artifact, targetDestination, selectedSource, candidateSources);
    }

    public async Task<ReplicaRepairAdmissionResult> CreateOperationAsync(
        ReplicaRepairOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // Lock order: artifact row -> tenant row (taken by ReserveQuotaAsync).
        await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_artifacts
            WHERE "TenantId" = {operation.TenantId.Value} AND "Id" = {operation.ArtifactId} FOR NO KEY UPDATE
            """).ToListAsync(cancellationToken);
        // A terminal operation (imported history) holds nothing.
        if (!operation.IsTerminal)
        {
            var decision = await ReserveQuotaAsync(context, operation, operation.CreatedAt, cancellationToken);
            if (!decision.Allowed)
                return QuotaDenied(decision);
        }

        context.ReplicaRepairOperations.Add(operation);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, null, null);
    }

    /// <summary>
    /// Decides a repair's counted bytes against used + every other hold and sets the hold on the operation, under the tenant lock and inside the
    /// caller's transaction, so concurrent repairs and runs cannot both see the same free capacity. The caller saves the operation and commits.
    /// </summary>
    private static async Task<BackupAdmissionDecision> ReserveQuotaAsync(
        PlatformDbContext context, ReplicaRepairOperation operation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await TenantUsageEngine.LockTenantAsync(context, operation.TenantId, cancellationToken))
            return BackupAdmissionDecision.Denied(BackupAdmissionDenialCodes.SubscriptionRequired);

        var artifact = await context.BackupArtifacts.AsNoTracking().Include(a => a.Replicas)
            .SingleAsync(a => a.TenantId == operation.TenantId && a.Id == operation.ArtifactId, cancellationToken);
        var connection = operation.StorageConnectionId == Guid.Empty
            ? null
            : await context.StorageConnections.AsNoTracking()
                .SingleOrDefaultAsync(c => c.TenantId == operation.TenantId && c.Id == operation.StorageConnectionId, cancellationToken);
        return await PostgreSqlBackupAdmissionPolicy.AdmitReplicaRepairAsync(
            context, operation.TenantId, artifact, operation.StorageProvider, operation.StorageConnectionId, connection, now, cancellationToken, reserveOn: operation);
    }

    private static ReplicaRepairAdmissionResult QuotaDenied(BackupAdmissionDecision decision) =>
        new(false, ReplicaRepairFailureCodes.QuotaExceeded, $"Storage quota limit exceeded: {decision.DenialCode}.");
    public Task<bool> PinSourceAsync(TenantId tenantId, Guid operationId, Guid leaseToken, Guid sourceReplicaId, DateTimeOffset now, CancellationToken cancellationToken) =>
        PinSourceAsync(tenantId, operationId, leaseToken, sourceReplicaId, "default.bak", now, cancellationToken);

    public async Task<bool> PinSourceAsync(TenantId tenantId, Guid operationId, Guid leaseToken, Guid sourceReplicaId, string targetKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var operations = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM replica_repair_operations WHERE "TenantId" = {tenantId.Value} AND "Id" = {operationId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (operations.Count == 0) return false;
        var operation = await context.ReplicaRepairOperations.SingleAsync(entity => entity.TenantId == tenantId && entity.Id == operationId, cancellationToken);
        if (!operation.HasLease(leaseToken, now) || operation.CancellationRequestedAt is not null) return false;
        var sources = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_replicas
            WHERE "TenantId" = {tenantId.Value} AND "ArtifactId" = {operation.ArtifactId} AND "Id" = {sourceReplicaId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (sources.Count == 0) return false;
        var source = await context.BackupReplicas.AsNoTracking().SingleAsync(entity => entity.TenantId == tenantId && entity.Id == sourceReplicaId, cancellationToken);
        if (!ReplicaRepairSourceEvaluator.IsEligibleSource(source, now) || source.ExecutionLocation != BackupStorageExecutionLocation.Backend) return false;
        operation.SetTargetIntent(source.Id, targetKey);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CanCleanupTargetAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        string targetKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var operation = await context.ReplicaRepairOperations
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == operationId, cancellationToken);

        if (operation is null || !operation.HasLease(leaseToken, now)) return false;

        // Fencing: If a live Verified replica already exists on this destination, never delete!
        var hasVerified = await context.BackupReplicas
            .AnyAsync(r => r.TenantId == tenantId && r.ArtifactId == operation.ArtifactId &&
                r.StorageProvider == operation.StorageProvider &&
                r.StorageConnectionId == operation.StorageConnectionId &&
                r.VerificationState == "Verified" &&
                r.Status != BackupReplicaStatus.Deleted, cancellationToken);
        if (hasVerified) return false;

        // Fencing: Atomically extend lease by 2 minutes so no competing worker can claim during deletion
        operation.RenewLease(leaseToken, now, TimeSpan.FromMinutes(2));
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }


    public async Task<ReplicaRepairOperation?> ClaimOperationAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        string leaseOwner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({ClaimLockKey})", cancellationToken);
        var operation = await context.ReplicaRepairOperations.FromSqlInterpolated($"""
            SELECT *, xmin FROM replica_repair_operations
            WHERE "TenantId" = {tenantId.Value} AND "Id" = {operationId}
            FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken);

        if (operation is null || operation.Status != ReplicaRepairStatus.Pending) return null;
        if (operation.CancellationRequestedAt != null)
        {
            operation.CancelPending(now);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (operation.NextRetryAt > now) return null;

        // Expired leases still occupy capacity until recovery fences the old transfer.
        var active = context.ReplicaRepairOperations.Where(r => r.Status == ReplicaRepairStatus.InProgress);
        if (await active.CountAsync(cancellationToken) >= IReplicaRepairStore.MaxGlobalConcurrentRepairs ||
            await active.CountAsync(r => r.TenantId == tenantId, cancellationToken) >= IReplicaRepairStore.MaxTenantConcurrentRepairs)
            return null;

        operation.Claim(leaseToken, leaseOwner, now, leaseDuration);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return operation;
    }

    public async Task<ReplicaRepairLeaseRenewal> RenewLeaseAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var operation = await context.ReplicaRepairOperations
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == operationId, cancellationToken);

        if (operation is null || !operation.HasLease(leaseToken, now)) return ReplicaRepairLeaseRenewal.Lost;

        var cancellationRequested = operation.CancellationRequestedAt != null;
        operation.RenewLease(leaseToken, now, leaseDuration);
        await context.SaveChangesAsync(cancellationToken);
        return ReplicaRepairLeaseRenewal.Active(cancellationRequested);
    }

    public async Task<bool> UpdateProgressAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        long transferredBytes,
        long totalBytes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var operation = await context.ReplicaRepairOperations
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == operationId, cancellationToken);

        if (operation is null || !operation.HasLease(leaseToken, now)) return false;

        operation.UpdateProgress(leaseToken, transferredBytes, totalBytes, now);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<BackupReplica> CompleteOperationAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        BackupReplica newReplica,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var operation = await context.ReplicaRepairOperations
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == operationId, cancellationToken);

        if (operation is null || !operation.HasLease(leaseToken, now))
            throw new InvalidOperationException("Repair operation lease is lost, expired, or invalid.");

        var artifact = await context.BackupArtifacts
            .Include(a => a.Replicas)
            .SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == operation.ArtifactId, cancellationToken);

        if (artifact is null)
            throw new InvalidOperationException("Artifact not found during completion.");

        var existingVerified = artifact.Replicas.FirstOrDefault(r =>
            r.VerificationState == "Verified" &&
            r.Status != BackupReplicaStatus.Deleted &&
            r.StorageProvider == newReplica.StorageProvider &&
            r.StorageConnectionId == newReplica.StorageConnectionId);

        if (existingVerified is not null)
        {
            operation.Succeed(leaseToken, existingVerified.Id, now);
            await context.SaveChangesAsync(cancellationToken);
            return existingVerified;
        }

        artifact.AddRepairedReplica(newReplica);

        if (!context.BackupReplicas.Local.Contains(newReplica) && !await context.BackupReplicas.AnyAsync(r => r.Id == newReplica.Id, cancellationToken))
        {
            context.BackupReplicas.Add(newReplica);
        }
        operation.Succeed(leaseToken, newReplica.Id, now);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await context.Entry(operation).ReloadAsync(cancellationToken);
            if (!operation.HasLease(leaseToken, now))
                throw new InvalidOperationException("Repair operation lease is lost, expired, or invalid after reload.");
            operation.Succeed(leaseToken, newReplica.Id, now);
            await context.SaveChangesAsync(cancellationToken);
        }
        return newReplica;
    }

    public async Task<bool> FailOperationAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        string failureCode,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var operation = await context.ReplicaRepairOperations
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == operationId, cancellationToken);

        if (operation is null) return false;

        if (operation.HasLease(leaseToken, now))
        {
            operation.Fail(leaseToken, failureCode, reason, now);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (operation.Status == ReplicaRepairStatus.InProgress && operation.LeaseExpiresAt <= now && operation.LeaseToken == leaseToken)
        {
            operation.MarkStaleFailed(failureCode, reason, now);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        return false;
    }

    public async Task<bool> ScheduleRetryAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        string failureCode,
        string? reason,
        DateTimeOffset nextRetryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var operation = await context.ReplicaRepairOperations
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == operationId, cancellationToken);

        if (operation is null) return false;

        if (operation.HasLease(leaseToken, now))
        {
            operation.ScheduleRetry(leaseToken, failureCode, reason, nextRetryAt, now);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        return false;
    }

    public async Task RecordRepairOrphanAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid storageConnectionId,
        string storageProvider,
        string storageTarget,
        string key,
        long sizeBytes,
        string checksum,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var artifact = await context.BackupArtifacts
            .Include(a => a.Replicas)
            .SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == artifactId, cancellationToken);

        if (artifact is null) return;

        var orphan = new BackupReplica(
            Guid.NewGuid(),
            tenantId,
            artifactId,
            storageProvider,
            storageTarget,
            key,
            Math.Max(1, sizeBytes),
            checksum,
            "sha256",
            "Failed",
            "RepairCleanupFailed",
            now,
            verifiedAt: null,
            BackupStorageExecutionLocation.Backend,
            owningAgentId: null,
            storageConnectionId: storageConnectionId);

        artifact.AddOrphanReplica(orphan, now);
        if (!context.BackupReplicas.Local.Contains(orphan) && !await context.BackupReplicas.AnyAsync(r => r.Id == orphan.Id, cancellationToken))
        {
            context.BackupReplicas.Add(orphan);
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<ReplicaRepairOperation?> ClaimDueRepairAsync(
        string leaseOwner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken,
        TenantId? tenantId = null)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var tx = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({ClaimLockKey})", cancellationToken);

        // Expired leases still occupy capacity until recovery fences the old transfer.
        if (await context.ReplicaRepairOperations.CountAsync(r => r.Status == ReplicaRepairStatus.InProgress, cancellationToken) >=
            IReplicaRepairStore.MaxGlobalConcurrentRepairs)
            return null;

        var dueId = await context.Database.SqlQuery<Guid>($"""
            SELECT pending."Id" AS "Value" FROM replica_repair_operations pending
            WHERE pending."Status" = 'Pending' AND (pending."NextRetryAt" IS NULL OR pending."NextRetryAt" <= {now})
              AND ({tenantId == null} OR pending."TenantId" = {(tenantId == null ? Guid.Empty : tenantId.Value)})
              AND (SELECT COUNT(*) FROM replica_repair_operations active
                   WHERE active."Status" = 'InProgress' AND active."TenantId" = pending."TenantId") < {IReplicaRepairStore.MaxTenantConcurrentRepairs}
            ORDER BY COALESCE(pending."NextRetryAt", pending."CreatedAt") ASC, pending."Id"
            LIMIT 1
            FOR UPDATE OF pending SKIP LOCKED
            """).SingleOrDefaultAsync(cancellationToken);

        if (dueId == Guid.Empty) return null;

        var operation = await context.ReplicaRepairOperations
            .SingleOrDefaultAsync(r => r.Id == dueId, cancellationToken);

        if (operation is null || operation.Status != ReplicaRepairStatus.Pending) return null;

        if (operation.CancellationRequestedAt != null)
        {
            operation.CancelPending(now);
            await context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return null;
        }

        var leaseToken = Guid.NewGuid();
        operation.Claim(leaseToken, leaseOwner, now, leaseDuration);
        await context.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return operation;
    }

    public async Task<int> RecoverStaleRepairsAsync(
        DateTimeOffset now,
        TimeSpan initialRetryDelay,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var stale = await context.ReplicaRepairOperations
            .Where(r => r.Status == ReplicaRepairStatus.InProgress && r.LeaseExpiresAt <= now)
            .OrderBy(r => r.LeaseExpiresAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        var count = 0;
        foreach (var op in stale)
        {
            if (op.CancellationRequestedAt != null)
            {
                op.CancelStale(now);
                if (!string.IsNullOrEmpty(op.TargetKey))
                {
                    var artifact = await context.BackupArtifacts
                        .Include(a => a.Replicas)
                        .SingleOrDefaultAsync(a => a.TenantId == op.TenantId && a.Id == op.ArtifactId, cancellationToken);
                    if (artifact is not null && !artifact.Replicas.Any(r => r.StorageProvider == op.StorageProvider && r.StorageConnectionId == op.StorageConnectionId && r.VerificationState == "Verified" && r.Status != BackupReplicaStatus.Deleted))
                    {
                        var orphan = new BackupReplica(
                            Guid.NewGuid(),
                            op.TenantId,
                            op.ArtifactId,
                            op.StorageProvider,
                            op.StorageConnectionId == Guid.Empty ? op.StorageProvider : op.StorageConnectionId.ToString("D"),
                            op.TargetKey,
                            Math.Max(1, op.TotalBytes),
                            artifact.ArtifactChecksum,
                            artifact.ChecksumAlgorithm,
                            "Failed",
                            "RepairCrashRecovery",
                            now,
                            verifiedAt: null,
                            BackupStorageExecutionLocation.Backend,
                            owningAgentId: null,
                            storageConnectionId: op.StorageConnectionId);
                        artifact.AddOrphanReplica(orphan, now);
                        if (!context.BackupReplicas.Local.Contains(orphan) && !await context.BackupReplicas.AnyAsync(r => r.Id == orphan.Id, cancellationToken))
                        {
                            context.BackupReplicas.Add(orphan);
                        }
                    }
                }
                count++;
                continue;
            }

            if (op.AttemptCount < op.MaxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(3600, initialRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(0, op.AttemptCount - 1))));
                op.RecoverStale("lease_expired", "Repair lease expired before completion; recovery retry scheduled.", now + delay, now);
            }
            else
            {
                op.MarkStaleFailed("lease_expired", "Repair lease expired and maximum retry attempts exhausted.", now);
                if (!string.IsNullOrEmpty(op.TargetKey))
                {
                    var artifact = await context.BackupArtifacts
                        .Include(a => a.Replicas)
                        .SingleOrDefaultAsync(a => a.TenantId == op.TenantId && a.Id == op.ArtifactId, cancellationToken);
                    if (artifact is not null && !artifact.Replicas.Any(r => r.StorageProvider == op.StorageProvider && r.StorageConnectionId == op.StorageConnectionId && r.VerificationState == "Verified" && r.Status != BackupReplicaStatus.Deleted))
                    {
                        var orphan = new BackupReplica(
                            Guid.NewGuid(),
                            op.TenantId,
                            op.ArtifactId,
                            op.StorageProvider,
                            op.StorageConnectionId == Guid.Empty ? op.StorageProvider : op.StorageConnectionId.ToString("D"),
                            op.TargetKey,
                            Math.Max(1, op.TotalBytes),
                            artifact.ArtifactChecksum,
                            artifact.ChecksumAlgorithm,
                            "Failed",
                            "RepairCrashRecovery",
                            now,
                            verifiedAt: null,
                            BackupStorageExecutionLocation.Backend,
                            owningAgentId: null,
                            storageConnectionId: op.StorageConnectionId);
                        artifact.AddOrphanReplica(orphan, now);
                        if (!context.BackupReplicas.Local.Contains(orphan) && !await context.BackupReplicas.AnyAsync(r => r.Id == orphan.Id, cancellationToken))
                        {
                            context.BackupReplicas.Add(orphan);
                        }
                    }
                }
            }
            count++;
        }

        if (count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        return count;
    }

    public async Task<IReadOnlyList<ReplicaRepairOperation>> DiscoverEligibleRepairsAsync(
        DateTimeOffset now,
        int maxBatch,
        CancellationToken cancellationToken,
        TenantId? tenantId = null)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);

        const int ScanBatchSize = 50;
        DateTimeOffset? cursorCreatedAt = null;
        Guid? cursorId = null;
        var created = new List<ReplicaRepairOperation>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        while (created.Count < maxBatch)
        {
            var query = context.BackupArtifacts
                .Include(a => a.Replicas)
                .Where(a => (tenantId == null || a.TenantId == tenantId)
                    && a.AcceptedAt != null
                    && (a.RetentionExpiresAt == null || a.RetentionExpiresAt > now || a.RetentionPinnedAt != null));

            if (cursorCreatedAt.HasValue && cursorId.HasValue)
            {
                var cTime = cursorCreatedAt.Value;
                var cId = cursorId.Value;
                query = query.Where(a => a.CreatedAt < cTime || (a.CreatedAt == cTime && a.Id < cId));
            }

            var candidateArtifacts = await query
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .Take(ScanBatchSize)
                .ToListAsync(cancellationToken);

            if (candidateArtifacts.Count == 0) break;

            var last = candidateArtifacts[^1];
            cursorCreatedAt = last.CreatedAt;
            cursorId = last.Id;

            foreach (var artifact in candidateArtifacts)
            {
                if (created.Count >= maxBatch) break;
            var lockedArtifacts = await context.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM backup_artifacts
                WHERE "TenantId" = {artifact.TenantId.Value} AND "Id" = {artifact.Id} FOR NO KEY UPDATE
                """).ToListAsync(cancellationToken);
            if (lockedArtifacts.Count == 0) continue;

            var attempt = await context.BackupRunAttempts
                .AsNoTracking()
                .SingleOrDefaultAsync(at => at.TenantId == artifact.TenantId && at.Id == artifact.AttemptId, cancellationToken);
            if (attempt is null || !attempt.IsTerminal) continue;

            var run = await context.BackupRuns
                .AsNoTracking()
                .SingleOrDefaultAsync(r => r.TenantId == artifact.TenantId && r.Id == attempt.RunId, cancellationToken);
            if (run is null) continue;

            var destinations = run.Configuration.ResolveEffectiveDestinations();

            foreach (var dest in destinations)
            {
                if (created.Count >= maxBatch) break;

                var connId = dest.StorageConnectionId ?? Guid.Empty;
                var isVerified = artifact.Replicas.Any(r =>
                    ((dest.StorageConnectionId != null && dest.StorageConnectionId != Guid.Empty)
                        ? r.StorageConnectionId == dest.StorageConnectionId
                        : string.Equals(r.StorageProvider, dest.StorageProvider, StringComparison.OrdinalIgnoreCase))
                    && r.VerificationState == "Verified" && r.Status != BackupReplicaStatus.Deleted);

                if (isVerified) continue;

                var existingOp = await context.ReplicaRepairOperations
                    .Where(r => r.TenantId == artifact.TenantId && r.ArtifactId == artifact.Id && r.StorageConnectionId == connId)
                    .OrderByDescending(r => r.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                // Retryable failures remain Pending; terminal history is never revived by discovery.
                if (existingOp is not null) continue;

                // Quota is decided by the reservation below, in this transaction; a second connection must not touch the usage rows it holds.
                var admission = await EvaluateRepairAsync(
                    artifact.TenantId, artifact.Id, dest.StorageConnectionId, dest.StorageProvider, now, excludeOperationId: null, checkQuota: false, cancellationToken);
                if (!admission.Allowed || admission.SelectedSource is null) continue;
                var lockedSources = await context.Database.SqlQuery<Guid>($"""
                    SELECT "Id" AS "Value" FROM backup_replicas
                    WHERE "TenantId" = {artifact.TenantId.Value} AND "ArtifactId" = {artifact.Id} AND "Id" = {admission.SelectedSource.Id} FOR UPDATE
                    """).ToListAsync(cancellationToken);
                if (lockedSources.Count == 0) continue;
                var source = await context.BackupReplicas.AsNoTracking().SingleAsync(entity => entity.TenantId == artifact.TenantId && entity.Id == admission.SelectedSource.Id, cancellationToken);
                if (!ReplicaRepairSourceEvaluator.IsEligibleSource(source, now) || source.ExecutionLocation != BackupStorageExecutionLocation.Backend) continue;

                var newOp = new ReplicaRepairOperation(
                    Guid.NewGuid(),
                    artifact.TenantId,
                    artifact.Id,
                    connId,
                    dest.StorageProvider,
                    admission.SelectedSource.Id,
                    now,
                    requestedByUserId: null,
                    totalBytes: artifact.SizeBytes,
                    maxAttempts: 5,
                    nextRetryAt: now);

                // Saved per operation so the next candidate of the tenant sees this hold.
                if (!(await ReserveQuotaAsync(context, newOp, now, cancellationToken)).Allowed) continue;
                context.ReplicaRepairOperations.Add(newOp);
                await context.SaveChangesAsync(cancellationToken);
                created.Add(newOp);
            }
        }
    }

        await transaction.CommitAsync(cancellationToken);
        return created;
    }
    public async Task<bool> RequestCancellationAsync(
        TenantId tenantId,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var operation = await context.ReplicaRepairOperations
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == operationId, cancellationToken);

        if (operation is null || operation.IsTerminal) return false;

        if (operation.Status == ReplicaRepairStatus.Pending)
        {
            operation.CancelPending(now);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        operation.RequestCancellation(now);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CancelOperationAsync(
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var operation = await context.ReplicaRepairOperations
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == operationId, cancellationToken);

        if (operation is null) return false;

        if (operation.HasLease(leaseToken, now))
        {
            operation.Cancel(leaseToken, now);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (operation.Status == ReplicaRepairStatus.Pending)
        {
            operation.CancelPending(now);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        return false;
    }

    public async Task<ReplicaRepairOperationView?> GetOperationAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var op = await context.ReplicaRepairOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.ArtifactId == artifactId && r.Id == operationId, cancellationToken);

        return op is null ? null : ToView(op);
    }

    public async Task<IReadOnlyList<ReplicaRepairOperationView>> ListOperationsAsync(
        TenantId tenantId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var list = await context.ReplicaRepairOperations
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.ArtifactId == artifactId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(25)
            .ToListAsync(cancellationToken);

        return list.Select(ToView).ToList();
    }

    private static ReplicaRepairOperationView ToView(ReplicaRepairOperation r) =>
        new(
            r.Id,
            r.ArtifactId,
            r.StorageConnectionId,
            r.StorageProvider,
            r.SourceReplicaId,
            r.TargetReplicaId,
            r.TargetKey,
            r.Status.ToString(),
            r.FailureCode,
            r.FailureReason,
            r.TransferredBytes,
            r.TotalBytes,
            r.CreatedAt,
            r.StartedAt,
            r.CompletedAt,
            r.CancellationRequestedAt,
            r.RequestedByUserId,
            r.AttemptCount,
            r.MaxAttempts,
            r.NextRetryAt,
            r.LastAttemptAt,
            r.LastFailureCode,
            r.IsExhausted);
}
