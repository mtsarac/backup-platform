using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

/// <summary>An artifact with the run and job it belongs to; the replicas are loaded.</summary>
public sealed record BackupArtifactRecord(BackupArtifact Artifact, Guid RunId, Guid JobId,
    string? JobName = null, string? DatabaseName = null, string? AgentName = null, Guid? AgentId = null, BackupConfiguration? Snapshot = null);

/// <summary>Created-at bounds are inclusive UTC instants; pagination has no history cutoff.</summary>
public sealed record BackupArtifactQuery(Guid? JobId = null, Guid? AgentId = null, string? State = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, int Page = 1, int PageSize = 25);

public sealed record BackupArtifactPage<T>(IReadOnlyList<T> Items, long TotalCount, int Page, int PageSize);

/// <summary>One replica a worker now owns the delete of. The location is the PERSISTED reference, never a caller-supplied one.</summary>
/// <param name="LeaseToken">Fences this delete: only the holder may complete or fail it.</param>
/// <param name="Attempt">How many delete attempts this replica has started, this one included (1 on the first).</param>
public sealed record ClaimedReplicaDeletion(
    Guid ReplicaId, TenantId TenantId, Guid ArtifactId, string StorageProvider, string StorageTarget, string Key, Guid LeaseToken, int Attempt);

public enum BackupArtifactDeleteKind { NotFound = 1, Requested, AlreadyInProgress, AlreadyDeleted }
public sealed record BackupArtifactDeleteResult(BackupArtifactDeleteKind Kind, BackupArtifactRecord? Artifact);

public enum BackupArtifactCancelKind { NotFound = 1, Restored, NothingToCancel }
public sealed record BackupArtifactCancelResult(BackupArtifactCancelKind Kind, BackupArtifactRecord? Artifact);

/// <summary>What the retention scan did in one batch.</summary>
/// <param name="ArtifactsExamined">Expired, unpinned artifacts the batch looked at; a full batch means there may be more.</param>
/// <param name="ReplicasMoved">Replicas that entered DeletePending.</param>
public sealed record BackupRetentionApplication(int ArtifactsExamined, int ReplicasMoved);

/// <summary>What the database knows about an attempt, for deciding whether a stored file may belong to live work.</summary>
public sealed record BackupAttemptActivity(Guid AttemptId, TenantId TenantId, Guid JobId, Guid RunId, BackupStatus AttemptStatus, BackupStatus RunStatus)
{
    /// <summary>
    /// Whether anything about the attempt's run is still in flight: queued, leased, running or waiting for a retry. Files of such a run are never
    /// cleaned up, including those of an attempt of it that already failed.
    /// </summary>
    public bool RunIsActive => RunStatus is BackupStatus.Pending or BackupStatus.Leased or BackupStatus.Running or BackupStatus.RetryPending;

    /// <summary>The attempt itself holds (or may still use) a lease.</summary>
    public bool AttemptIsLive => AttemptStatus is BackupStatus.Pending or BackupStatus.Leased or BackupStatus.Running;
}

/// <summary>
/// Durable retention state transitions and the queries the sweeps need. Every method is atomic and safe to call from any number of backend
/// nodes at once: eligibility is decided by the database inside the same statement or transaction that applies it, never by a read that
/// can go stale. Tenant-scoped operations never reveal that an id exists in another tenant.
/// </summary>
public interface IBackupRetentionStore
{
    /// <summary>
    /// Moves every Available replica of up to <paramref name="limit"/> expired, unpinned artifacts to DeletePending (soft delete) with the artifact's
    /// own grace period. Idempotent; concurrent nodes never act on the same artifact at once.
    /// </summary>
    Task<BackupRetentionApplication> ApplyExpiredRetentionAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claims due backend replicas and durably dispatches due agent replicas. Only backend replicas are returned for physical IO.
    /// Active agent leases are never taken by a backend worker; unclaimed dispatches are checked again at a bounded cadence.
    /// </summary>
    Task<IReadOnlyList<ClaimedReplicaDeletion>> ClaimDueDeletionsAsync(string owner, DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken);

    /// <summary>Marks the replica Deleted. False when the lease token no longer matches (somebody else took the delete over) or the replica is not Deleting.</summary>
    Task<bool> CompleteDeletionAsync(Guid replicaId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks the delete failed and schedules its retry. False when the lease token no longer matches.</summary>
    Task<bool> FailDeletionAsync(Guid replicaId, Guid leaseToken, DateTimeOffset now, string failureCode, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken);

    /// <summary>Manual delete: soft delete (DeletePending with the grace period). Never physical.</summary>
    Task<BackupArtifactDeleteResult> RequestDeleteAsync(TenantId tenantId, Guid artifactId, Guid? requestedByUserId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>DeletePending back to Available. Refused once a physical delete has begun. Race-safe against the delete workers and against the retention scan.</summary>
    Task<BackupArtifactCancelResult> CancelDeleteAsync(TenantId tenantId, Guid artifactId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<BackupArtifactRecord?> GetArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken);

    /// <summary>Newest first (by acceptance), optionally of one job. Includes artifacts whose replicas are all Deleted: history is never dropped.</summary>
    Task<IReadOnlyList<BackupArtifactRecord>> ListArtifactsAsync(TenantId tenantId, Guid? jobId, int limit, CancellationToken cancellationToken);

    Task<BackupArtifactPage<BackupArtifactRecord>> QueryArtifactsAsync(TenantId tenantId, BackupArtifactQuery query, CancellationToken cancellationToken);

    /// <summary>Of <paramref name="keys"/>, those some replica of ANY status references under <paramref name="storageProvider"/>, in any tenant.</summary>
    Task<IReadOnlySet<string>> FindReferencedKeysAsync(string storageProvider, IReadOnlyCollection<string> keys, CancellationToken cancellationToken);

    /// <summary>The attempts among <paramref name="attemptIds"/> that exist, with the state of their run. Unknown ids are simply absent.</summary>
    Task<IReadOnlyDictionary<Guid, BackupAttemptActivity>> GetAttemptActivityAsync(IReadOnlyCollection<Guid> attemptIds, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes up to <paramref name="limit"/> decided schedule occurrences whose decision is older than <paramref name="decidedBefore"/>, except the
    /// newest occurrence of every job (the scheduler's watermark) and any occurrence whose run has not finished. Returns the rows deleted.
    /// BackupRuns and their audit data are never touched.
    /// </summary>
    Task<int> PruneScheduleOccurrencesAsync(DateTimeOffset decidedBefore, int limit, CancellationToken cancellationToken);
}
