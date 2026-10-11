using BackupPlatform.Domain.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Application.Backups;

/// <summary>What one retention cycle did.</summary>
/// <param name="ArtifactsExamined">Expired, unpinned artifacts looked at.</param>
/// <param name="ReplicasMarkedForDeletion">Replicas that entered DeletePending because their artifact expired.</param>
/// <param name="DeletionsClaimed">Replicas whose physical delete this node took over.</param>
/// <param name="Deleted">Claimed replicas now recorded Deleted.</param>
/// <param name="Failed">Claimed replicas whose delete failed and was scheduled for a retry.</param>
/// <param name="LeaseLost">Claimed replicas whose outcome could not be recorded because another node took the delete over.</param>
public sealed record RetentionCycleResult(int ArtifactsExamined, int ReplicasMarkedForDeletion, int DeletionsClaimed, int Deleted, int Failed, int LeaseLost);

/// <summary>
/// The backend-owned half of retention: it expires backups, dispatches agent-owned removals and physically deletes only backend-owned replicas.
/// Every decision is made by <see cref="IBackupRetentionStore"/> in the database, so any number of nodes may run this at once; this class
/// talks to the storage. A replica is recorded Deleted only after the provider reported the object gone (and, where the provider can prove
/// absence, after that was checked), never before; any failure is recorded with a stable code and retried at a bounded cadence.
/// </summary>
public sealed class BackupRetentionService(
    IBackupRetentionStore store, IBackupStorageResolver storages, BackupRetentionOptions options, BackupNodeIdentity node, TimeProvider time,
    ILogger<BackupRetentionService> logger)
{
    /// <summary>Expires what is due, then deletes what is due. Batches repeat while they come back full, up to <see cref="BackupRetentionOptions.MaxBatchesPerCycle"/>.</summary>
    public async Task<RetentionCycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        int examined = 0, marked = 0, claimed = 0, deleted = 0, failed = 0, leaseLost = 0;

        for (var batch = 0; batch < options.MaxBatchesPerCycle; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var applied = await store.ApplyExpiredRetentionAsync(time.GetUtcNow(), options.BatchSize, cancellationToken);
            examined += applied.ArtifactsExamined;
            marked += applied.ReplicasMoved;
            if (applied.ArtifactsExamined < options.BatchSize) break;
        }

        for (var batch = 0; batch < options.MaxBatchesPerCycle; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claims = await store.ClaimDueDeletionsAsync(node.Value, time.GetUtcNow(), options.DeleteLeaseDuration, options.BatchSize, cancellationToken);
            claimed += claims.Count;
            foreach (var claim in claims)
            {
                // A replica left unfinished here stays Deleting: its lease expires and any node takes it over (it then finds the file absent or deletes it).
                cancellationToken.ThrowIfCancellationRequested();
                switch (await DeleteAsync(claim, cancellationToken))
                {
                    case Outcome.Deleted: deleted++; break;
                    case Outcome.Failed: failed++; break;
                    case Outcome.LeaseLost: leaseLost++; break;
                }
            }

            if (claims.Count < options.BatchSize) break;
        }

        return new RetentionCycleResult(examined, marked, claimed, deleted, failed, leaseLost);
    }

    /// <summary>Removes decided schedule occurrences older than the configured history. Never touches runs. Returns the rows deleted.</summary>
    public async Task<int> PruneScheduleOccurrencesAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - options.ScheduleOccurrenceHistoryRetention;
        var total = 0;
        for (var batch = 0; batch < options.MaxBatchesPerCycle; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var removed = await store.PruneScheduleOccurrencesAsync(cutoff, options.ScheduleOccurrencePruneBatchSize, cancellationToken);
            total += removed;
            if (removed < options.ScheduleOccurrencePruneBatchSize) break;
        }

        return total;
    }

    private enum Outcome { Deleted, Failed, LeaseLost, Unrecorded }

    private async Task<Outcome> DeleteAsync(ClaimedReplicaDeletion claim, CancellationToken cancellationToken)
    {
        var failureCode = await TryDeletePhysicallyAsync(claim, cancellationToken);
        try
        {
            if (failureCode is null)
            {
                if (await store.CompleteDeletionAsync(claim.ReplicaId, claim.LeaseToken, time.GetUtcNow(), cancellationToken)) return Outcome.Deleted;
                logger.LogWarning("Backup replica {ReplicaId} was deleted but its delete lease was taken over; the new owner records the outcome.", claim.ReplicaId);
                return Outcome.LeaseLost;
            }

            var now = time.GetUtcNow();
            logger.LogWarning("Backup replica {ReplicaId} delete attempt {Attempt} failed: {FailureCode}.", claim.ReplicaId, claim.Attempt, failureCode);
            if (await store.FailDeletionAsync(claim.ReplicaId, claim.LeaseToken, now, failureCode, now + options.DeleteRetryDelay(claim.Attempt), cancellationToken)) return Outcome.Failed;
            logger.LogWarning("Backup replica {ReplicaId} delete lease was taken over before the failure could be recorded.", claim.ReplicaId);
            return Outcome.LeaseLost;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The database is unreachable. The file is already gone (or the delete failed): either way the lease expires and the replica is claimed
            // again; a missing file then counts as success, which is what recovers "deleted but not recorded". Only the exception TYPE is logged.
            logger.LogError("Could not record the delete outcome of backup replica {ReplicaId} (attempt {Attempt}, outcome {Outcome}): {ErrorType}. It is retried when its lease expires.",
                claim.ReplicaId, claim.Attempt, failureCode ?? "deleted", exception.GetType().Name);
            return Outcome.Unrecorded;
        }
    }

    /// <summary>Null when the object is gone; otherwise the stable reason. Only cancellation escapes.</summary>
    private async Task<string?> TryDeletePhysicallyAsync(ClaimedReplicaDeletion claim, CancellationToken cancellationToken)
    {
        try
        {
            var provider = storages.Resolve(claim.TenantId, claim.StorageProvider, claim.StorageTarget);
            if (provider is null) return BackupDeleteFailureCodes.StorageNotConfigured;
            if (!provider.Capabilities.Supports(StorageCapabilityIds.Delete)) return BackupDeleteFailureCodes.DeleteNotSupported;
            if (!StorageObjectKey.TryCreate(claim.Key, out var key) || key is null) return BackupDeleteFailureCodes.InvalidLocation;

            // False means the object was already absent: the delete is idempotent, so that is success.
            await provider.DeleteAsync(key, cancellationToken);
            if (provider.Capabilities.Supports(StorageCapabilityIds.VerifyAbsence) && await provider.ExistsAsync(key, cancellationToken))
            {
                return BackupDeleteFailureCodes.DeleteNotVerified;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (StorageException exception)
        {
            return exception.Kind switch
            {
                StorageFailureKind.AccessDenied or StorageFailureKind.AuthenticationFailed => BackupDeleteFailureCodes.AccessDenied,
                StorageFailureKind.InvalidConfiguration or StorageFailureKind.NotFound or StorageFailureKind.NetworkUnavailable or StorageFailureKind.Timeout => BackupDeleteFailureCodes.StorageUnavailable,
                StorageFailureKind.Io or StorageFailureKind.InsufficientSpace => BackupDeleteFailureCodes.IoError,
                StorageFailureKind.NotSupported => BackupDeleteFailureCodes.DeleteNotSupported,
                _ => BackupDeleteFailureCodes.Unexpected
            };
        }
        catch (Exception)
        {
            return BackupDeleteFailureCodes.Unexpected;
        }
    }
}
