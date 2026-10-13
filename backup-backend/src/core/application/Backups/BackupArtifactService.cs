using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Backups;

namespace BackupPlatform.Application.Backups;

public sealed record ArtifactDeleteResponse(BackupArtifactDeleteKind Kind, BackupArtifactView? Artifact);
public sealed record ArtifactCancelDeleteResponse(BackupArtifactCancelKind Kind, BackupArtifactView? Artifact);

/// <summary>
/// What an operator may do with backups and with the retention of a job. Every operation is scoped to one tenant (an artifact of another tenant is
/// reported as not found, never as forbidden) and none of them touches storage: deleting is a soft delete that starts the grace period, the
/// physical delete belongs to <see cref="BackupRetentionService"/>. All eligibility decisions are the store's, made in the database.
/// </summary>
public sealed class BackupArtifactService(IBackupRetentionStore store, IBackupScheduleStore jobs, BackupRetentionOptions options, TimeProvider time)
{
    private const int MaxListLimit = 200;

    public async Task<BackupArtifactView?> GetAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) =>
        ToView(await store.GetArtifactAsync(tenantId, artifactId, cancellationToken));

    /// <summary>Newest first, optionally of one job. Artifacts whose replicas are all deleted are included: history is never dropped.</summary>
    public async Task<IReadOnlyList<BackupArtifactView>> ListAsync(TenantId tenantId, Guid? jobId, int limit, CancellationToken cancellationToken)
    {
        var records = await store.ListArtifactsAsync(tenantId, jobId, Math.Clamp(limit, 1, MaxListLimit), cancellationToken);
        return [.. records.Select(Map)];
    }

    public async Task<BackupArtifactPage<BackupArtifactView>> QueryAsync(TenantId tenantId, BackupArtifactQuery query, CancellationToken cancellationToken)
    {
        var result = await store.QueryArtifactsAsync(tenantId, query, cancellationToken);
        return new([.. result.Items.Select(Map)], result.TotalCount, result.Page, result.PageSize);
    }

    /// <summary>Soft delete: the replicas become DeletePending and stay intact, visible and restorable until the artifact's own grace period is over.</summary>
    public async Task<ArtifactDeleteResponse> RequestDeleteAsync(TenantId tenantId, Guid artifactId, Guid? requestedByUserId, CancellationToken cancellationToken)
    {
        var result = await store.RequestDeleteAsync(tenantId, artifactId, requestedByUserId, time.GetUtcNow(), cancellationToken);
        return new ArtifactDeleteResponse(result.Kind, ToView(result.Artifact));
    }

    /// <summary>Undoes a soft delete while no physical delete has begun.</summary>
    public async Task<ArtifactCancelDeleteResponse> CancelDeleteAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken, Guid? requestedByUserId = null)
    {
        var result = await store.CancelDeleteAsync(tenantId, artifactId, time.GetUtcNow(), cancellationToken, requestedByUserId);
        return new ArtifactCancelDeleteResponse(result.Kind, ToView(result.Artifact));
    }

    /// <summary>
    /// Applies the request over the job's CURRENT policy (omitted members keep their value) through <c>BackupOrchestrator.ToDomain</c> and
    /// <see cref="IBackupScheduleStore.ModifyJobAsync"/>; null when the job does not exist; <see cref="ArgumentException"/> when the policy is invalid
    /// or below the platform minimums. The policy is resolved from the job as the store loads it under its lock, so a concurrent change cannot be
    /// merged over a stale read, and it only shapes backups accepted from now on: existing artifacts keep the expiration they were given.
    /// </summary>
    public async Task<BackupJobView?> ChangeJobRetentionAsync(TenantId tenantId, Guid jobId, BackupRetentionPolicyRequest request, CancellationToken cancellationToken, UserId? actor = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = time.GetUtcNow();
        var job = await jobs.ModifyJobAsync(tenantId, jobId, target => target.ChangeRetention(BackupOrchestrator.ToDomain(request, options, target.RetentionPolicy)), cancellationToken, actor, now);
        return job is null ? null : BackupOrchestrator.ToView(job, now);
    }

    private static BackupArtifactView? ToView(BackupArtifactRecord? record) => record is null ? null : Map(record);

    private static BackupArtifactView Map(BackupArtifactRecord record)
    {
        var artifact = record.Artifact;
        var state = artifact.State == BackupArtifactState.Available ? "Available"
            : artifact.State == BackupArtifactState.Deleted ? "Deleted"
            : artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.DeleteFailed && replica.DeleteFailureCode is not null) ? "DeleteFailed"
            : artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.Deleting) ? "Deleting"
            : "DeletePending";
        return BackupOrchestrator.ToView(artifact, record.RunId, record.JobId, record.Snapshot) with
        {
            State = state,
            JobName = record.JobName,
            DatabaseName = record.DatabaseName,
            AgentName = record.AgentName,
            AgentId = record.AgentId,
            CancelDeleteAllowed = artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.DeletePending && replica.VerificationState == "Verified")
        };
    }
}
