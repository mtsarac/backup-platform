using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

public sealed class PostgreSqlReplicaRepairStore(DbContextOptions<PlatformDbContext> options) : IReplicaRepairStore
{
    public async Task<ReplicaRepairAdmissionResult> AdmitRepairAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid? storageConnectionId,
        string? storageProvider,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Guid? excludeOperationId = null)
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

        // Quota admission check
        var entitlement = await PostgreSqlTenantEntitlementReader.ResolveAsync(context, tenantId, now, cancellationToken);
        var policy = await TenantUsageEngine.ResolvePolicyAsync(context, tenantId, cancellationToken);

        if (policy.EnforcementMode != QuotaEnforcementMode.None && entitlement.StorageLimit is { IsUnlimited: false })
        {
            long additionalBytes = 0;
            var providerType = targetDestination.StorageProvider;
            var ownership = connection?.Ownership ?? StorageOwnership.PlatformManaged;
            var quotaScope = connection?.QuotaScope ?? StorageQuotaScopes.DefaultFor(ownership);
            var targetFacts = new ReplicaStorageFacts(providerType, ownership, quotaScope, targetDestination.StorageConnectionId ?? Guid.Empty);

            if (policy.AccountingMode == QuotaAccountingMode.PhysicalReplicaBytes)
            {
                if (policy.IncludedStorageScopes.Matches(targetFacts))
                    additionalBytes = artifact.SizeBytes;
            }
            else if (policy.AccountingMode == QuotaAccountingMode.LogicalArtifactBytes)
            {
                var connectionFacts = await TenantUsageEngine.LoadConnectionFactsAsync(context, tenantId, cancellationToken);
                var hasReplicaInScope = artifact.Replicas.Any(r =>
                    r.Status != BackupReplicaStatus.Deleted &&
                    connectionFacts.TryGetValue(r.StorageConnectionId, out var facts) &&
                    policy.IncludedStorageScopes.Matches(facts));

                if (!hasReplicaInScope && policy.IncludedStorageScopes.Matches(targetFacts))
                    additionalBytes = artifact.SizeBytes;
            }

            if (additionalBytes > 0)
            {
                var (usage, _) = await TenantUsageEngine.EnsureCurrentAsync(context, tenantId, policy, now, cancellationToken);
                var decision = QuotaAdmissionDecision.Decide(entitlement, policy, usage.UsedBytes, usage.ReservedBytes, additionalBytes);
                if (!decision.Allowed)
                    return new(false, ReplicaRepairFailureCodes.QuotaExceeded, $"Storage quota limit exceeded: {decision.DenialCode}.");
            }
        }

        return new(true, null, null, artifact, targetDestination, selectedSource, candidateSources);
    }

    public async Task<ReplicaRepairOperation> CreateOperationAsync(
        ReplicaRepairOperation operation,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        context.ReplicaRepairOperations.Add(operation);
        await context.SaveChangesAsync(cancellationToken);
        return operation;
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
        var operation = await context.ReplicaRepairOperations
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == operationId, cancellationToken);

        if (operation is null) return null;

        if (operation.Status == ReplicaRepairStatus.Pending)
        {
            operation.Claim(leaseToken, leaseOwner, now, leaseDuration);
            await context.SaveChangesAsync(cancellationToken);
            return operation;
        }

        if (operation.Status == ReplicaRepairStatus.InProgress && operation.LeaseExpiresAt <= now)
        {
            operation.Claim(leaseToken, leaseOwner, now, leaseDuration);
            await context.SaveChangesAsync(cancellationToken);
            return operation;
        }

        return null;
    }

    public async Task<bool> RenewLeaseAsync(
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

        if (operation is null || !operation.HasLease(leaseToken, now)) return false;

        operation.RenewLease(leaseToken, now, leaseDuration);
        await context.SaveChangesAsync(cancellationToken);
        return true;
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

        var dueId = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM replica_repair_operations
            WHERE "Status" = 'Pending' AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= {now})
              AND ({tenantId == null} OR "TenantId" = {tenantId!.Value})
            ORDER BY COALESCE("NextRetryAt", "CreatedAt") ASC
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).SingleOrDefaultAsync(cancellationToken);

        if (dueId == Guid.Empty) return null;

        var operation = await context.ReplicaRepairOperations
            .SingleOrDefaultAsync(r => r.Id == dueId, cancellationToken);

        if (operation is null || operation.Status != ReplicaRepairStatus.Pending) return null;

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
            if (op.AttemptCount < op.MaxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(3600, initialRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(0, op.AttemptCount - 1))));
                op.RecoverStale("lease_expired", "Repair lease expired before completion; recovery retry scheduled.", now + delay, now);
            }
            else
            {
                op.MarkStaleFailed("lease_expired", "Repair lease expired and maximum retry attempts exhausted.", now);
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
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);

        var candidateArtifacts = await context.BackupArtifacts
            .Include(a => a.Replicas)
            .Where(a => a.AcceptedAt != null
                && (a.RetentionExpiresAt == null || a.RetentionExpiresAt > now || a.RetentionPinnedAt != null))
            .OrderByDescending(a => a.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        var created = new List<ReplicaRepairOperation>();

        foreach (var artifact in candidateArtifacts)
        {
            if (created.Count >= maxBatch) break;

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

                if (existingOp is not null)
                {
                    if (existingOp.Status is ReplicaRepairStatus.Pending or ReplicaRepairStatus.InProgress or ReplicaRepairStatus.Succeeded or ReplicaRepairStatus.Cancelled)
                        continue;
                    if (existingOp.IsExhausted)
                        continue;
                    if (existingOp.CompletedAt is { } completed && now - completed < TimeSpan.FromMinutes(2))
                        continue;
                }

                var admission = await AdmitRepairAsync(artifact.TenantId, artifact.Id, dest.StorageConnectionId, dest.StorageProvider, now, cancellationToken);
                if (!admission.Allowed || admission.SelectedSource is null) continue;

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

                context.ReplicaRepairOperations.Add(newOp);
                created.Add(newOp);
            }
        }

        if (created.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        return created;
    }

    public async Task<int> GetActiveRepairsCountAsync(TenantId? tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var query = context.ReplicaRepairOperations
            .Where(r => r.Status == ReplicaRepairStatus.InProgress);
        if (tenantId != null)
            query = query.Where(r => r.TenantId == tenantId);
        return await query.CountAsync(cancellationToken);
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
