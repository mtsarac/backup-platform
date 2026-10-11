using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

public sealed record BackupLeaseCommand(TenantId TenantId, AgentId AgentId, Guid AttemptId, Guid LeaseToken, DateTimeOffset Now)
{
    public string? SoftwareVersion { get; init; }
    public string? ProtocolVersion { get; init; }
    public string? Platform { get; init; }
}
public sealed record BackupProgress(string Stage, double? Percent);
public sealed record BackupCompletion(string Outcome, string? ReasonCode, BackupArtifactResult? Artifact, IReadOnlyList<BackupDestinationReport>? Destinations = null);
public sealed record BackupArtifactResult(long SizeBytes, string SourceChecksum, string ArtifactChecksum, string ChecksumAlgorithm, DateTimeOffset CreatedAt, string SourceMetadataJson, string AppliedOptionsJson, IReadOnlyList<BackupReplicaResult> Replicas);
public sealed record BackupReplicaResult(string StorageProvider, string StorageTarget, string Key, long SizeBytes, string Checksum, string ChecksumAlgorithm, string VerificationState, string VerificationMethod, DateTimeOffset CreatedAt, DateTimeOffset? VerifiedAt, Guid? StorageConnectionId = null);
/// <summary>What the agent reported for one snapshot destination (already validated against the run's snapshot).</summary>
public sealed record BackupDestinationReport(string StorageProvider, Guid? StorageConnectionId, string State, string Upload, string Verification, string? FailureCode, string? Key, bool ObjectMayRemain, DateTimeOffset StartedAt, DateTimeOffset CompletedAt);
public sealed record BackupTransferIntent(Guid StorageConnectionId, string Key, long SizeBytes, string Checksum);
public sealed record BackupClaim(BackupRun Run, BackupRunAttempt Attempt)
{
    public long? MaxArtifactBytes { get; init; }
}
public sealed record BackupLeaseResult(DateTimeOffset LeaseExpiresAt, bool CancellationRequested);
public enum BackupMutationResult { Accepted, NotFound, Conflict }

public enum BackupStartOutcome
{
    /// <summary>A new run was created.</summary>
    Started,
    /// <summary>The Idempotency-Key was already used for this job: the original run is returned, nothing new was created.</summary>
    Replayed,
    NotFound,
    /// <summary>The job's overlap policy is Skip and another run of the job is queued, retrying or executing. Nothing was created.</summary>
    SkippedOverlap,
    /// <summary>The admission policy refused a new run (<see cref="BackupStartResult.DenialCode"/>). Nothing was created.</summary>
    AdmissionDenied
}
public sealed record BackupStartResult(BackupStartOutcome Outcome, BackupRun? Run, Guid? ActiveRunId = null, string? DenialCode = null);

public enum BackupRetryOutcome
{
    /// <summary>A new attempt was queued on the same run.</summary>
    Started,
    NotFound,
    Succeeded,
    Cancelled,
    /// <summary>An attempt is already queued or running.</summary>
    InProgress,
    AttemptsExhausted,
    /// <summary>The job's Skip policy forbids reopening this run while another run of the job is active.</summary>
    OverlapActive
}
public sealed record BackupRetryResult(BackupRetryOutcome Outcome, BackupRun? Run);

/// <summary>A scheduled retry that became a Pending attempt; the owning agent can be hinted.</summary>
public sealed record BackupRetryStarted(Guid RunId, AgentId AgentId);

/// <summary>
/// Omitted policies keep the job's current values; the store resolves them under its lock. <paramref name="ApplyUnderLock"/> runs against the job
/// loaded under the same agent lock and inside the same transaction as the update, so schedule, retention and enabled changes commit together with
/// the general fields or not at all: any exception it throws abandons the whole edit.
/// </summary>
public sealed record BackupJobUpdate(string Name, AgentId AgentId, BackupConfiguration Configuration, BackupRetryPolicy? RetryPolicy, BackupOverlapPolicy? OverlapPolicy,
    Action<BackupJob>? ApplyUnderLock = null);

/// <summary>
/// All mutations are atomic, ownership-filtered and durably committed before returning. Retries and concurrency limits live
/// entirely in the database: a retry is a row (RetryPending + NextAttemptAt) and a slot is a Leased/Running attempt, so neither
/// depends on process memory.
/// </summary>
public interface IBackupRunStore
{
    Task<BackupJob?> CreateJobAsync(BackupJob job, CancellationToken cancellationToken);
    Task<BackupJob?> GetJobAsync(TenantId tenantId, Guid jobId, CancellationToken cancellationToken);
    Task<BackupJob?> UpdateJobAsync(TenantId tenantId, Guid jobId, BackupJobUpdate update, CancellationToken cancellationToken);
    /// <summary>Idempotent per (tenant, job, key). Applies the job's overlap policy: Skip refuses, Queue enqueues.</summary>
    Task<BackupStartResult> StartRunAsync(TenantId tenantId, Guid jobId, string idempotencyKey, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupRun>> ListRunsAsync(TenantId tenantId, Guid? jobId, int limit, CancellationToken cancellationToken);
    Task<BackupRun?> GetRunAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken);
    /// <summary>Cancels Pending and RetryPending runs outright; requests cooperative cancellation of Leased/Running ones.</summary>
    Task<BackupRun?> RequestCancellationAsync(TenantId tenantId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>Operator retry: a new attempt on the same run, immediately, within the policy's attempt budget. Not related to the manual-start Idempotency-Key.</summary>
    Task<BackupRetryResult> RetryRunAsync(TenantId tenantId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>
    /// Claims the agent's next eligible attempt if the agent, tenant and global limits all have a free slot; null otherwise.
    /// Order is attempt eligibility, run creation, run id.
    /// </summary>
    Task<BackupClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<BackupClaim?> GetAttemptAsync(TenantId tenantId, AgentId agentId, Guid attemptId, CancellationToken cancellationToken);
    Task<BackupMutationResult> StartAttemptAsync(BackupLeaseCommand command, CancellationToken cancellationToken);
    Task<BackupLeaseResult?> RenewLeaseAsync(BackupLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<BackupMutationResult> RecordTransferAsync(BackupLeaseCommand command, BackupTransferIntent transfer, CancellationToken cancellationToken);
    Task<BackupMutationResult> ReportProgressAsync(BackupLeaseCommand command, BackupProgress progress, CancellationToken cancellationToken);
    Task<BackupMutationResult> CompleteAttemptAsync(BackupLeaseCommand command, BackupCompletion completion, CancellationToken cancellationToken);
    /// <summary>Marks expired Leased/Running attempts AgentLost and, per policy, schedules their retry or ends the run. Returns the attempts expired.</summary>
    Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>Creates the next attempt of every RetryPending run whose NextAttemptAt has passed. Safe to run from any number of workers or instances.</summary>
    Task<IReadOnlyList<BackupRetryStarted>> StartDueRetriesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
}
