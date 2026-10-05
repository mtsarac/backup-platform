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
public sealed record BackupCompletion(string Outcome, string? ReasonCode, BackupArtifactResult? Artifact);
public sealed record BackupArtifactResult(long SizeBytes, string SourceChecksum, string ArtifactChecksum, string ChecksumAlgorithm, DateTimeOffset CreatedAt, string SourceMetadataJson, string AppliedOptionsJson, IReadOnlyList<BackupReplicaResult> Replicas);
public sealed record BackupReplicaResult(string StorageProvider, string StorageTarget, string Key, long SizeBytes, string Checksum, string ChecksumAlgorithm, string VerificationState, string VerificationMethod, DateTimeOffset CreatedAt, DateTimeOffset? VerifiedAt);
public sealed record BackupClaim(BackupRun Run, BackupRunAttempt Attempt);
public sealed record BackupLeaseResult(DateTimeOffset LeaseExpiresAt, bool CancellationRequested);
public enum BackupMutationResult { Accepted, NotFound, Conflict }

/// <summary>All mutations are atomic, ownership-filtered and durably committed before returning. No automatic retry is created.</summary>
public interface IBackupRunStore
{
    Task<BackupJob?> CreateJobAsync(BackupJob job, CancellationToken cancellationToken);
    Task<BackupJob?> GetJobAsync(TenantId tenantId, Guid jobId, CancellationToken cancellationToken);
    Task<BackupRun?> StartRunAsync(TenantId tenantId, Guid jobId, string idempotencyKey, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupRun>> ListRunsAsync(TenantId tenantId, Guid? jobId, int limit, CancellationToken cancellationToken);
    Task<BackupRun?> GetRunAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken);
    Task<BackupRun?> RequestCancellationAsync(TenantId tenantId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<BackupClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<BackupClaim?> GetAttemptAsync(TenantId tenantId, AgentId agentId, Guid attemptId, CancellationToken cancellationToken);
    Task<BackupMutationResult> StartAttemptAsync(BackupLeaseCommand command, CancellationToken cancellationToken);
    Task<BackupLeaseResult?> RenewLeaseAsync(BackupLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<BackupMutationResult> ReportProgressAsync(BackupLeaseCommand command, BackupProgress progress, CancellationToken cancellationToken);
    Task<BackupMutationResult> CompleteAttemptAsync(BackupLeaseCommand command, BackupCompletion completion, CancellationToken cancellationToken);
    Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
