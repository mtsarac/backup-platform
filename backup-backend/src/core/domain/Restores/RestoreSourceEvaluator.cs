using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Domain.Restores;

public sealed record RestoreSourceDecision(RestoreSourceRejection? Rejection, RestoreSourceAccess? Access)
{
    public bool Eligible => Rejection is null;
    public static RestoreSourceDecision Reject(RestoreSourceRejection reason) => new(reason, null);
    public static RestoreSourceDecision Accept(RestoreSourceAccess access) => new(null, access);
}

/// <summary>
/// Decides whether a replica may serve a restore on a given agent, from persisted facts only. A replica the target agent cannot reach is
/// never "assumed reachable": an agent-local replica of another agent is rejected with <see cref="RestoreSourceRejection.AgentTransferRequired"/>,
/// because moving the bytes between agents would be an explicit transfer this version does not have.
/// </summary>
public static class RestoreSourceEvaluator
{
    public static RestoreSourceDecision Evaluate(BackupArtifact artifact, BackupReplica replica, AgentId targetAgent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(replica);
        ArgumentNullException.ThrowIfNull(targetAgent);
        if (replica.ArtifactId != artifact.Id || replica.TenantId != artifact.TenantId) return RestoreSourceDecision.Reject(RestoreSourceRejection.ReplicaNotFound);
        if (artifact.BackupType != "Full") return RestoreSourceDecision.Reject(RestoreSourceRejection.BackupTypeUnsupported);
        // Available, or DeletePending inside its grace period (the grace period exists precisely so a premature deletion can still be recovered from).
        if (!replica.IsRestorable(now)) return RestoreSourceDecision.Reject(RestoreSourceRejection.ReplicaNotRestorable);
        if (replica.VerificationState != "Verified" || replica.VerifiedAt is null) return RestoreSourceDecision.Reject(RestoreSourceRejection.ReplicaNotVerified);
        if (replica.ChecksumAlgorithm != "sha256" || replica.Checksum.Length != 64) return RestoreSourceDecision.Reject(RestoreSourceRejection.ChecksumUnavailable);
        if (replica.ExecutionLocation == BackupStorageExecutionLocation.Agent)
        {
            return replica.OwningAgentId == targetAgent.Value
                ? RestoreSourceDecision.Accept(RestoreSourceAccess.AgentLocal)
                : RestoreSourceDecision.Reject(RestoreSourceRejection.AgentTransferRequired);
        }
        if (replica.ExecutionLocation != BackupStorageExecutionLocation.Backend)
            return RestoreSourceDecision.Reject(RestoreSourceRejection.NoAccessibleReplica);
        if (replica.StorageProvider == "central")
            return RestoreSourceDecision.Accept(RestoreSourceAccess.CentralStorage);
        return (replica.StorageProvider is "sftp" or "google-drive") && replica.StorageConnectionId != Guid.Empty
            && Guid.TryParse(replica.StorageTarget, out var connectionId) && connectionId == replica.StorageConnectionId
            ? RestoreSourceDecision.Accept(RestoreSourceAccess.BackendRelayed)
            : RestoreSourceDecision.Reject(RestoreSourceRejection.NoAccessibleReplica);
    }

    /// <summary>
    /// The best eligible replica for <paramref name="targetAgent"/>: Available before DeletePending (a replica in its grace period is the last
    /// resort), the target agent's own copy before a shared one (no network), then the newest. Null when none qualifies; the caller reports the
    /// most informative rejection with <see cref="Explain"/>.
    /// </summary>
    public static BackupReplica? SelectReplica(BackupArtifact artifact, AgentId targetAgent, DateTimeOffset now) =>
        artifact.Replicas
            .Select(replica => (Replica: replica, Decision: Evaluate(artifact, replica, targetAgent, now)))
            .Where(candidate => candidate.Decision.Eligible)
            .OrderBy(candidate => candidate.Replica.Status == BackupReplicaStatus.Available ? 0 : 1)
            .ThenBy(candidate => candidate.Decision.Access == RestoreSourceAccess.AgentLocal ? 0 : 1)
            .ThenByDescending(candidate => candidate.Replica.CreatedAt)
            .Select(candidate => candidate.Replica)
            .FirstOrDefault();

    /// <summary>Why no replica qualified: the rejection of the "closest" candidate (an agent-transfer problem outranks a plain unavailability).</summary>
    public static RestoreSourceRejection Explain(BackupArtifact artifact, AgentId targetAgent, DateTimeOffset now)
    {
        var rejections = artifact.Replicas.Select(replica => Evaluate(artifact, replica, targetAgent, now).Rejection).Where(r => r is not null).Select(r => r!.Value).ToArray();
        if (rejections.Length == 0) return RestoreSourceRejection.NoAccessibleReplica;
        if (rejections.Contains(RestoreSourceRejection.AgentTransferRequired)) return RestoreSourceRejection.AgentTransferRequired;
        return rejections.Min();
    }
}
