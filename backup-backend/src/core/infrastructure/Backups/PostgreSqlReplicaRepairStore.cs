using System.Collections.Concurrent;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
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

    private const int DiscoveryScanLimit = 50;
    // Shared across the worker's per-tick scopes; a restart begins a new pass.
    private static readonly ConcurrentDictionary<(string Database, Guid Tenant), DiscoveryProgress> DiscoveryCursors = new();

    private sealed class DiscoveryProgress
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset? CreatedAt { get; set; }
        public Guid Id { get; set; }
    }

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

        var snapshotDestinations = run.Configuration.Destinations;

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
        var hasActiveRepair = await context.ReplicaRepairOperations
            .AnyAsync(r => r.TenantId == tenantId && r.ArtifactId == artifactId && r.StorageConnectionId == effectiveConnectionId
                && (excludeOperationId == null || r.Id != excludeOperationId)
                && (r.Status == ReplicaRepairStatus.Pending || r.Status == ReplicaRepairStatus.InProgress), cancellationToken);

        // Only recovery can fence an expired transfer and account for its target before retrying.
        if (hasActiveRepair)
            return new(false, "repair_already_in_progress", "A repair is already in progress for this destination.");

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
        // atomically under its own row lock, matching the canonical lock order (operation -> tenant) to prevent deadlocks with cancellation.
        BackupAdmissionDecision quotaDecision;
        if (excludeOperationId is { } ownId)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var locked = await context.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM replica_repair_operations
                WHERE "TenantId" = {tenantId.Value} AND "Id" = {ownId}
                FOR UPDATE
                """).ToListAsync(cancellationToken);
            if (locked.Count > 0)
            {
                var own = await context.ReplicaRepairOperations.SingleAsync(r => r.TenantId == tenantId && r.Id == ownId, cancellationToken);
                if (!own.IsTerminal)
                {
                    quotaDecision = await ReserveQuotaAsync(context, own, now, cancellationToken);
                    if (quotaDecision.Allowed)
                    {
                        await context.SaveChangesAsync(cancellationToken);
                    }
                    await transaction.CommitAsync(cancellationToken);
                }
                else
                {
                    await transaction.CommitAsync(cancellationToken);
                    quotaDecision = BackupAdmissionDecision.Admitted;
                }
            }
            else
            {
                await transaction.CommitAsync(cancellationToken);
                quotaDecision = BackupAdmissionDecision.Admitted;
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
        if (operation.TargetKey != targetKey && !ManagedBackupLayout.IsPartialSibling(operation.TargetKey, targetKey)) return false;

        // Fencing: If a live Verified replica already exists on this destination, never delete!
        var hasVerified = await context.BackupReplicas
            .AnyAsync(r => r.TenantId == tenantId && r.ArtifactId == operation.ArtifactId &&
                r.StorageProvider == operation.StorageProvider &&
                r.StorageConnectionId == operation.StorageConnectionId &&
                r.VerificationState == "Verified" &&
                r.Status != BackupReplicaStatus.Deleted, cancellationToken);
        if (hasVerified) return false;

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

    public async Task<bool> FailTargetConflictAsync(
        TenantId tenantId, Guid operationId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var artifactId = await context.ReplicaRepairOperations.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Id == operationId)
            .Select(r => r.ArtifactId).SingleOrDefaultAsync(cancellationToken);
        if (artifactId == Guid.Empty) return false;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_artifacts
            WHERE "TenantId" = {tenantId.Value} AND "Id" = {artifactId} FOR NO KEY UPDATE
            """).ToListAsync(cancellationToken);
        await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM replica_repair_operations
            WHERE "TenantId" = {tenantId.Value} AND "Id" = {operationId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        var operation = await context.ReplicaRepairOperations.SingleAsync(r => r.TenantId == tenantId && r.Id == operationId, cancellationToken);
        if (!operation.HasLease(leaseToken, now) || string.IsNullOrEmpty(operation.TargetKey)) return false;
        var artifact = await context.BackupArtifacts.Include(a => a.Replicas)
            .SingleAsync(a => a.TenantId == tenantId && a.Id == artifactId, cancellationToken);
        var target = operation.StorageProvider is "sftp" or "google-drive" && operation.StorageConnectionId != Guid.Empty
            ? operation.StorageConnectionId.ToString("D") : operation.StorageProvider;
        if (!artifact.Replicas.Any(r => r.StorageConnectionId == operation.StorageConnectionId && r.Key == operation.TargetKey))
        {
            var conflict = new BackupReplica(Guid.NewGuid(), tenantId, artifact.Id, operation.StorageProvider, target,
                operation.TargetKey, Math.Max(1, operation.TotalBytes), artifact.ArtifactChecksum, artifact.ChecksumAlgorithm,
                "Failed", "RepairTargetConflict", now, null, BackupStorageExecutionLocation.Backend, null,
                storageConnectionId: operation.StorageConnectionId);
            artifact.AddQuarantinedReplica(conflict);
            context.BackupReplicas.Add(conflict);
        }
        operation.Fail(leaseToken, ReplicaRepairFailureCodes.TargetConflict, "Target object conflicts with expected content; ownership requires reconciliation.", now);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
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
        CancellationToken cancellationToken,
        bool quarantine = false)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var artifact = await context.BackupArtifacts
            .Include(a => a.Replicas)
            .SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == artifactId, cancellationToken);

        if (artifact is null) return;

        var existing = artifact.Replicas.FirstOrDefault(r => r.TenantId == tenantId && r.StorageConnectionId == storageConnectionId && r.Key == key)
            ?? await context.BackupReplicas.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.StorageConnectionId == storageConnectionId && r.Key == key, cancellationToken);
        if (existing is not null)
        {
            if (existing.VerificationState != "Verified")
            {
                if (quarantine)
                {
                    if (existing.Status == BackupReplicaStatus.Available)
                        existing.Quarantine();
                }
                else
                {
                    existing.ReopenOrphan(now);
                }
            }
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

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
            quarantine ? "RepairTargetConflict" : "RepairCleanupFailed",
            now,
            verifiedAt: null,
            BackupStorageExecutionLocation.Backend,
            owningAgentId: null,
            storageConnectionId: storageConnectionId);

        if (quarantine)
            artifact.AddQuarantinedReplica(orphan);
        else
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
        await using var scan = PlatformDbContext.CreateForPlatformOperations(options);
        var stale = await scan.ReplicaRepairOperations.AsNoTracking()
            .Where(r => r.Status == ReplicaRepairStatus.InProgress && r.LeaseExpiresAt <= now)
            .OrderBy(r => r.LeaseExpiresAt).Take(50)
            .Select(r => new { r.Id, r.TenantId, r.ArtifactId }).ToListAsync(cancellationToken);
        var count = 0;
        foreach (var candidate in stale)
        {
            await using var context = PlatformDbContext.CreateForPlatformOperations(options);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await context.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM backup_artifacts
                WHERE "TenantId" = {candidate.TenantId.Value} AND "Id" = {candidate.ArtifactId} FOR NO KEY UPDATE
                """).ToListAsync(cancellationToken);
            var locked = await context.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM replica_repair_operations
                WHERE "TenantId" = {candidate.TenantId.Value} AND "Id" = {candidate.Id} FOR UPDATE SKIP LOCKED
                """).ToListAsync(cancellationToken);
            if (locked.Count == 0) continue;
            var op = await context.ReplicaRepairOperations.SingleAsync(r => r.TenantId == candidate.TenantId && r.Id == candidate.Id, cancellationToken);
            if (op.Status != ReplicaRepairStatus.InProgress || op.LeaseExpiresAt > now) continue;
            // Preserve every old target before a retry persists its distinct key.
            if (!string.IsNullOrEmpty(op.TargetKey))
            {
                var artifact = await context.BackupArtifacts.Include(a => a.Replicas)
                    .SingleAsync(a => a.TenantId == op.TenantId && a.Id == op.ArtifactId, cancellationToken);
                if (!artifact.Replicas.Any(r => r.StorageConnectionId == op.StorageConnectionId && r.Key == op.TargetKey))
                {
                    var orphan = new BackupReplica(Guid.NewGuid(), op.TenantId, op.ArtifactId, op.StorageProvider,
                        op.StorageProvider is "sftp" or "google-drive" && op.StorageConnectionId != Guid.Empty
                            ? op.StorageConnectionId.ToString("D") : op.StorageProvider,
                        op.TargetKey, Math.Max(1, op.TotalBytes), artifact.ArtifactChecksum, artifact.ChecksumAlgorithm,
                        "Failed", "RepairCrashRecovery", now, null, BackupStorageExecutionLocation.Backend, null,
                        storageConnectionId: op.StorageConnectionId);
                    var ownedName = op.LeaseToken is { } token ? ManagedBackupLayout.RepairFileName(op.Id, token) : null;
                    if (ownedName is not null && (op.TargetKey == ownedName || op.TargetKey.EndsWith($"/{ownedName}", StringComparison.Ordinal)))
                        artifact.AddOrphanReplica(orphan, now);
                    else
                        artifact.AddQuarantinedReplica(orphan);
                    context.BackupReplicas.Add(orphan);
                }
            }
            if (op.CancellationRequestedAt != null)
                op.CancelStale(now);
            else if (op.AttemptCount < op.MaxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(3600, initialRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(0, op.AttemptCount - 1))));
                op.RecoverStale("lease_expired", "Repair lease expired before completion; recovery retry scheduled.", now + delay, now);
            }
            else
                op.MarkStaleFailed("lease_expired", "Repair lease expired and maximum retry attempts exhausted.", now);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            count++;
        }
        return count;
    }

    public async Task<IReadOnlyList<ReplicaRepairOperation>> DiscoverEligibleRepairsAsync(
        DateTimeOffset now,
        int maxBatch,
        CancellationToken cancellationToken,
        TenantId? tenantId = null)
    {
        if (maxBatch <= 0) return [];
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var connection = context.Database.GetDbConnection();
        var progress = DiscoveryCursors.GetOrAdd(
            ($"{connection.DataSource}/{connection.Database}", tenantId?.Value ?? Guid.Empty), _ => new DiscoveryProgress());
        await progress.Gate.WaitAsync(cancellationToken);
        try
        {
            var scopeId = tenantId?.Value ?? Guid.Empty;
            var hasCursor = progress.CreatedAt.HasValue;
            var cursorTime = progress.CreatedAt ?? now;
            var cursorId = progress.Id;
            // Filter before taking the page: healthy artifacts and terminal repair history consume no work or row locks.
            var candidates = await context.BackupArtifacts.FromSqlInterpolated($"""
                SELECT artifact.*, artifact.xmin
                FROM backup_artifacts artifact
                JOIN backup_run_attempts attempt ON attempt."TenantId" = artifact."TenantId" AND attempt."Id" = artifact."AttemptId"
                JOIN backup_runs run ON run."TenantId" = attempt."TenantId" AND run."Id" = attempt."RunId"
                WHERE ({scopeId} = '00000000-0000-0000-0000-000000000000'::uuid OR artifact."TenantId" = {scopeId})
                    AND artifact."AcceptedAt" IS NOT NULL
                    AND (artifact."RetentionExpiresAt" IS NULL OR artifact."RetentionExpiresAt" > {now} OR artifact."RetentionPinnedAt" IS NOT NULL)
                    AND attempt."Status" IN (4, 5, 6, 7)
                    AND (NOT {hasCursor} OR (artifact."CreatedAt", artifact."Id") < ({cursorTime}, {cursorId}))
                    AND EXISTS (
                        SELECT 1 FROM backup_replicas source
                        WHERE source."TenantId" = artifact."TenantId" AND source."ArtifactId" = artifact."Id"
                            AND source."VerificationState" = 'Verified' AND source."VerifiedAt" IS NOT NULL
                            AND source."Status" = 'Available' AND source."ExecutionLocation" = 'Backend'
                            AND (source."DeleteLeaseExpiresAt" IS NULL OR source."DeleteLeaseExpiresAt" <= {now})
                            AND source."DeleteNextAttemptAt" IS NULL)
                    AND EXISTS (
                        SELECT 1 FROM jsonb_array_elements(run."Configuration"->'Destinations') destination
                        WHERE (COALESCE((destination->>'StorageConnectionId')::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
                            = '00000000-0000-0000-0000-000000000000'::uuid OR EXISTS (
                                SELECT 1 FROM storage_connections target
                                WHERE target."TenantId" = artifact."TenantId" AND target."Id" = (destination->>'StorageConnectionId')::uuid
                                    AND target."Enabled" AND NOT target."AdministrativelyDisabled"))
                            AND NOT EXISTS (
                            SELECT 1 FROM backup_replicas replica
                            WHERE replica."TenantId" = artifact."TenantId" AND replica."ArtifactId" = artifact."Id"
                                AND replica."VerificationState" = 'Verified' AND replica."Status" <> 'Deleted'
                                AND CASE WHEN COALESCE((destination->>'StorageConnectionId')::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
                                    <> '00000000-0000-0000-0000-000000000000'::uuid
                                    THEN replica."StorageConnectionId" = (destination->>'StorageConnectionId')::uuid
                                    ELSE lower(replica."StorageProvider") = lower(destination->>'StorageProvider') END)
                            AND NOT EXISTS (
                                SELECT 1 FROM replica_repair_operations repair
                                WHERE repair."TenantId" = artifact."TenantId" AND repair."ArtifactId" = artifact."Id"
                                    AND repair."StorageConnectionId" = COALESCE((destination->>'StorageConnectionId')::uuid,
                                        '00000000-0000-0000-0000-000000000000'::uuid)))
                ORDER BY artifact."CreatedAt" DESC, artifact."Id" DESC
                LIMIT {DiscoveryScanLimit}
                """).AsNoTracking().ToListAsync(cancellationToken);

            var created = new List<ReplicaRepairOperation>();
            var examined = 0;
            foreach (var candidate in candidates)
            {
                if (created.Count >= maxBatch) break;
                // Advance even on denial or contention, so later candidates get a turn on the next scoped tick.
                progress.CreatedAt = candidate.CreatedAt;
                progress.Id = candidate.Id;
                examined++;
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                var locked = await context.Database.SqlQuery<Guid>($"""
                    SELECT "Id" AS "Value" FROM backup_artifacts
                    WHERE "TenantId" = {candidate.TenantId.Value} AND "Id" = {candidate.Id}
                    FOR NO KEY UPDATE SKIP LOCKED
                    """).ToListAsync(cancellationToken);
                if (locked.Count == 0) continue;
                var artifact = await context.BackupArtifacts.AsNoTracking().Include(a => a.Replicas)
                    .SingleAsync(a => a.TenantId == candidate.TenantId && a.Id == candidate.Id, cancellationToken);
                var attempt = await context.BackupRunAttempts.AsNoTracking()
                    .SingleAsync(a => a.TenantId == artifact.TenantId && a.Id == artifact.AttemptId, cancellationToken);
                if (!attempt.IsTerminal) continue;
                var run = await context.BackupRuns.AsNoTracking()
                    .SingleAsync(r => r.TenantId == artifact.TenantId && r.Id == attempt.RunId, cancellationToken);

                foreach (var destination in run.Configuration.Destinations)
                {
                    if (created.Count >= maxBatch) break;
                    var destinationId = destination.StorageConnectionId ?? Guid.Empty;
                    if (await context.ReplicaRepairOperations.AnyAsync(r => r.TenantId == artifact.TenantId
                        && r.ArtifactId == artifact.Id && r.StorageConnectionId == destinationId, cancellationToken)) continue;
                    var admission = await EvaluateRepairAsync(artifact.TenantId, artifact.Id, destination.StorageConnectionId,
                        destination.StorageProvider, now, excludeOperationId: null, checkQuota: false, cancellationToken);
                    if (!admission.Allowed || admission.SelectedSource is null) continue;
                    var lockedSources = await context.Database.SqlQuery<Guid>($"""
                        SELECT "Id" AS "Value" FROM backup_replicas
                        WHERE "TenantId" = {artifact.TenantId.Value} AND "ArtifactId" = {artifact.Id}
                            AND "Id" = {admission.SelectedSource.Id} FOR UPDATE SKIP LOCKED
                        """).ToListAsync(cancellationToken);
                    if (lockedSources.Count == 0) continue;
                    var source = await context.BackupReplicas.AsNoTracking()
                        .SingleAsync(r => r.TenantId == artifact.TenantId && r.Id == admission.SelectedSource.Id, cancellationToken);
                    if (!ReplicaRepairSourceEvaluator.IsEligibleSource(source, now)
                        || source.ExecutionLocation != BackupStorageExecutionLocation.Backend) continue;
                    var operation = new ReplicaRepairOperation(Guid.NewGuid(), artifact.TenantId, artifact.Id, destinationId,
                        destination.StorageProvider, source.Id, now, totalBytes: artifact.SizeBytes, maxAttempts: 5, nextRetryAt: now);
                    if (!(await ReserveQuotaAsync(context, operation, now, cancellationToken)).Allowed) continue;
                    context.ReplicaRepairOperations.Add(operation);
                    await context.SaveChangesAsync(cancellationToken);
                    created.Add(operation);
                }
                await transaction.CommitAsync(cancellationToken);
                context.ChangeTracker.Clear();
            }
            // Wrap only after exhausting this page; a full page resumes strictly after its last examined key.
            if (examined == candidates.Count && candidates.Count < DiscoveryScanLimit)
                progress.CreatedAt = null;
            return created;
        }
        finally
        {
            progress.Gate.Release();
        }
    }
    public async Task<bool> RequestCancellationAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var operation = await context.ReplicaRepairOperations.FromSqlInterpolated($"""
            SELECT *, xmin FROM replica_repair_operations
            WHERE "TenantId" = {tenantId.Value} AND "ArtifactId" = {artifactId} AND "Id" = {operationId}
            FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken);

        if (operation is null || operation.IsTerminal) return false;

        operation.RequestCancellation(now);
        if (operation.Status == ReplicaRepairStatus.Pending)
            operation.CancelPending(now);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
