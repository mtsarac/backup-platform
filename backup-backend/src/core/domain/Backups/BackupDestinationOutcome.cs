using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

/// <summary>The closed vocabulary of one destination's result within one attempt.</summary>
public static class BackupDestinationStates
{
    public const string Verified = "Verified";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string NotAttempted = "NotAttempted";
    public const string NotRun = "NotRun";
    public const string Succeeded = "Succeeded";

    internal static bool IsState(string? value) => value is Verified or Failed or Cancelled;
    internal static bool IsUpload(string? value) => value is Succeeded or Failed or NotAttempted;
    internal static bool IsVerification(string? value) => value is Verified or Failed or NotRun;
}

/// <summary>
/// What one attempt did with ONE snapshot destination of the one artifact: how the transfer ended, how the stored copy verified, why it failed,
/// and whether an object may have been left behind. It is immutable history, written once with the attempt's completion. A destination that ends
/// Verified is the same fact as its <see cref="BackupReplica"/> (<see cref="ReplicaId"/>); every other state is what a replica row cannot express
/// (a destination that never stored anything has no replica), and it never makes the artifact less available.
/// </summary>
public sealed class BackupDestinationOutcome : ITenantOwned
{
    private BackupDestinationOutcome() { TenantId = null!; StorageProvider = null!; State = null!; Upload = null!; Verification = null!; }

    public BackupDestinationOutcome(Guid id, TenantId tenantId, Guid runId, Guid attemptId, string storageProvider, Guid storageConnectionId, bool isPrimary,
        BackupStorageExecutionLocation executionLocation, string state, string upload, string verification, string? failureCode, string? key,
        bool objectMayRemain, DateTimeOffset startedAt, DateTimeOffset completedAt, Guid? replicaId)
    {
        BackupGuard.Id(id);
        BackupGuard.Id(runId);
        BackupGuard.Id(attemptId);
        BackupGuard.Id(storageConnectionId);
        ArgumentNullException.ThrowIfNull(tenantId);
        BackupGuard.Reference(storageProvider);
        if (!BackupDestinationStates.IsState(state) || !BackupDestinationStates.IsUpload(upload) || !BackupDestinationStates.IsVerification(verification) ||
            !Enum.IsDefined(executionLocation) || completedAt < startedAt)
            throw new ArgumentException("Invalid destination outcome.");
        if (failureCode is not null) BackupGuard.Reference(failureCode);
        if (key is not null) BackupGuard.RelativeKey(key);
        var verified = state == BackupDestinationStates.Verified;
        // A verified destination is exactly: stored, verified, nothing failed, and its replica exists. Anything else is a failure with a reason.
        if (verified != (upload == BackupDestinationStates.Succeeded && verification == BackupDestinationStates.Verified && failureCode is null && key is not null && replicaId is not null) ||
            verified && objectMayRemain ||
            !verified && (failureCode is null || replicaId is not null || verification == BackupDestinationStates.Verified) ||
            objectMayRemain && (key is null || upload != BackupDestinationStates.Succeeded) ||
            state == BackupDestinationStates.Cancelled && failureCode != BackupFailureCodes.Cancelled)
            throw new ArgumentException("Inconsistent destination outcome.");
        Id = id;
        TenantId = tenantId;
        RunId = runId;
        AttemptId = attemptId;
        StorageProvider = storageProvider;
        StorageConnectionId = storageConnectionId;
        IsPrimary = isPrimary;
        ExecutionLocation = executionLocation;
        State = state;
        Upload = upload;
        Verification = verification;
        FailureCode = failureCode;
        Key = key;
        ObjectMayRemain = objectMayRemain;
        StartedAt = BackupGuard.Timestamp(startedAt);
        CompletedAt = BackupGuard.Timestamp(completedAt);
        ReplicaId = replicaId;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid RunId { get; private set; }
    /// <summary>The transfer attempt: every retry of a run transfers (and records) its destinations again under its own attempt.</summary>
    public Guid AttemptId { get; private set; }
    public string StorageProvider { get; private set; }
    public Guid StorageConnectionId { get; private set; }
    public bool IsPrimary { get; private set; }
    public BackupStorageExecutionLocation ExecutionLocation { get; private set; }
    /// <summary>Verified, Failed or Cancelled.</summary>
    public string State { get; private set; }
    /// <summary>Succeeded, Failed or NotAttempted.</summary>
    public string Upload { get; private set; }
    /// <summary>Verified, Failed or NotRun.</summary>
    public string Verification { get; private set; }
    public string? FailureCode { get; private set; }
    /// <summary>The key written under this destination, when anything was.</summary>
    public string? Key { get; private set; }
    /// <summary>An object was written and could not be proven removed after the destination failed: physical bytes may exist that no replica accounts for.</summary>
    public bool ObjectMayRemain { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset CompletedAt { get; private set; }
    public Guid? ReplicaId { get; private set; }

    public bool IsVerified => State == BackupDestinationStates.Verified;

    internal bool SameResult(BackupDestinationOutcome other) => StorageProvider == other.StorageProvider && StorageConnectionId == other.StorageConnectionId &&
        IsPrimary == other.IsPrimary && ExecutionLocation == other.ExecutionLocation && State == other.State && Upload == other.Upload &&
        Verification == other.Verification && FailureCode == other.FailureCode && Key == other.Key && ObjectMayRemain == other.ObjectMayRemain &&
        StartedAt == other.StartedAt && CompletedAt == other.CompletedAt;
}

public static class BackupReplicationStatuses
{
    /// <summary>Every destination holds a verified copy.</summary>
    public const string Healthy = "Healthy";
    /// <summary>The required number of verified copies exists, but not every destination holds one.</summary>
    public const string Degraded = "Degraded";
    /// <summary>At least one verified copy exists, fewer than the policy requires.</summary>
    public const string InsufficientReplicas = "InsufficientReplicas";
    /// <summary>No verified copy exists.</summary>
    public const string Unavailable = "Unavailable";
}

/// <summary>One snapshot destination as the replication projection shows it. <see cref="State"/>: Verified, Failed, Cancelled, NotAttempted, Deleted (a verified copy that has since been deleted) or Pending.</summary>
public sealed record BackupDestinationHealth(
    string StorageProvider, Guid? StorageConnectionId, bool IsPrimary, string State, string? FailureCode, bool ObjectMayRemain, Guid? ReplicaId, string? ReplicaStatus);

/// <summary>
/// Replication compliance of one artifact (or of the attempt that tried to create it), independent of the engine's outcome and of the run's status:
/// <see cref="Status"/> says whether the verified copies meet the policy, <see cref="PolicySatisfied"/> is exactly <c>VerifiedReplicas &gt;= RequiredVerifiedReplicas</c>.
/// <see cref="Destinations"/> is empty when only the replicas are known (an artifact listing).
/// </summary>
public sealed record BackupReplicationHealth(
    int RequiredVerifiedReplicas, int VerifiedReplicas, int DestinationCount, string Status, bool PolicySatisfied, IReadOnlyList<BackupDestinationHealth> Destinations);

/// <summary>THE definition of replication health. A pure projection of stored facts, so it can never drift from them.</summary>
public static class BackupReplicationEvaluator
{
    public static string StatusOf(int required, int verified, int total) =>
        verified <= 0 ? BackupReplicationStatuses.Unavailable
        : verified < required ? BackupReplicationStatuses.InsufficientReplicas
        : verified >= total ? BackupReplicationStatuses.Healthy
        : BackupReplicationStatuses.Degraded;

    /// <summary>The compliance of one artifact from its replicas alone: verified destinations are the distinct storage connections that still hold a usable verified copy.</summary>
    public static BackupReplicationHealth ForArtifact(int required, int total, IEnumerable<BackupReplica> replicas)
    {
        var required1 = Math.Max(1, required);
        var total1 = Math.Max(total, required1);
        var verified = replicas.Where(IsUsableVerified).Select(replica => (replica.StorageProvider, replica.StorageConnectionId)).Distinct().Count();
        return new(required1, verified, total1, StatusOf(required1, verified, total1), verified >= required1, []);
    }

    /// <summary>
    /// The compliance of one terminal attempt against its run's snapshot: every snapshot destination appears exactly once, with the replica that
    /// holds it and the outcome that was recorded for it. Null while the attempt has not ended (nothing has been decided yet).
    /// </summary>
    public static BackupReplicationHealth? ForAttempt(BackupConfiguration snapshot, BackupRunAttempt attempt)
    {
        // Nothing was transferred (the engine failed first, the attempt was lost or cancelled before a copy existed): there is no replication to judge, only a run status.
        if (!attempt.IsTerminal || (attempt.Artifacts.Count == 0 && attempt.DestinationOutcomes.Count == 0)) return null;
        var replicas = attempt.Artifacts.SelectMany(artifact => artifact.Replicas).ToArray();
        var destinations = snapshot.Destinations.Select(destination =>
        {
            bool Matches(string provider, Guid? connection) =>
                provider == destination.StorageProvider && (destination.StorageConnectionId is null || connection == destination.StorageConnectionId);
            var verifiedReplica = replicas.FirstOrDefault(replica => replica.VerificationState == "Verified" && Matches(replica.StorageProvider, replica.StorageConnectionId));
            var outcome = attempt.DestinationOutcomes.FirstOrDefault(candidate => Matches(candidate.StorageProvider, candidate.StorageConnectionId));
            string state;
            if (verifiedReplica is not null) state = IsUsableVerified(verifiedReplica) ? BackupDestinationStates.Verified : "Deleted";
            else if (outcome is not null) state = outcome.State;
            // A destination the attempt neither stored nor reported (an agent that predates the report, which is only accepted for a single destination).
            else state = BackupDestinationStates.NotAttempted;
            return new BackupDestinationHealth(destination.StorageProvider, destination.StorageConnectionId ?? outcome?.StorageConnectionId ?? verifiedReplica?.StorageConnectionId,
                destination.IsPrimary, state, outcome?.FailureCode,
                outcome?.ObjectMayRemain ?? false, verifiedReplica?.Id, verifiedReplica?.Status.ToString());
        }).ToArray();
        var required = Math.Max(1, snapshot.RequiredVerifiedReplicaCount);
        var verified = destinations.Count(destination => destination.State == BackupDestinationStates.Verified);
        return new(required, verified, destinations.Length, StatusOf(required, verified, destinations.Length), verified >= required, destinations);
    }

    private static bool IsUsableVerified(BackupReplica replica) => replica.VerificationState == "Verified" && replica.IsUsable;
}
