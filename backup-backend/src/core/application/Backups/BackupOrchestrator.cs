using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

public sealed record CreateBackupJobRequest(string Name, Guid AgentId, BackupExecutionConfiguration Configuration);
public sealed record BackupJobView(Guid Id, Guid AgentId, string Name, string BackupType, string OverlapPolicy, bool Enabled, DateTimeOffset CreatedAt, BackupExecutionConfiguration Configuration);
public sealed record BackupRunView(Guid Id, Guid JobId, Guid AgentId, string Status, string BackupType, string TriggerType, BackupExecutionConfiguration Configuration, int CurrentAttemptNumber, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset? CancellationRequestedAt, string? ReasonCode, string? Reason, IReadOnlyList<BackupAttemptView> Attempts);
public sealed record BackupAttemptView(Guid Id, int Number, string Status, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? ReasonCode, string? Reason, string? ProgressStage, double? ProgressPercent, DateTimeOffset? ProgressAt, IReadOnlyList<BackupArtifactView> Artifacts);
public sealed record BackupArtifactView(Guid Id, long SizeBytes, string SourceChecksum, string ArtifactChecksum, string ChecksumAlgorithm, DateTimeOffset CreatedAt, IReadOnlyList<BackupReplicaView> Replicas);
public sealed record BackupReplicaView(Guid Id, string StorageProvider, string StorageTarget, string Key, long SizeBytes, string Checksum, string ChecksumAlgorithm, string VerificationState, string VerificationMethod, DateTimeOffset CreatedAt, DateTimeOffset? VerifiedAt);
public sealed class BackupOrchestrator(IBackupRunStore store, TimeProvider time)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(120);

    public async Task<BackupJobView?> CreateJobAsync(TenantId tenantId, CreateBackupJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Configuration);
        if (!BackupInputValidation.IsValid(request))
        {
            throw new ArgumentException("Invalid backup job configuration.");
        }
        var c = request.Configuration;
        var configuration = new BackupConfiguration(c.Engine, c.Database, c.Host, c.Port, c.IntegratedSecurity, c.DatabaseCredentialReference,
            c.Encrypt, c.TrustServerCertificate, c.StagingReference, c.StorageProvider, c.StoragePrefix, c.CopyOnly, c.ChecksumPolicy, c.CompressionPolicy);
        var job = await store.CreateJobAsync(new BackupJob(Guid.NewGuid(), tenantId, request.Name.Trim(), new AgentId(request.AgentId), configuration, time.GetUtcNow()), cancellationToken);
        return job is null ? null : new BackupJobView(job.Id, job.AgentId.Value, job.Name, job.BackupType, job.OverlapPolicy, job.Enabled, job.CreatedAt, ToWire(job.Configuration));
    }

    public Task<BackupRun?> StartRunAsync(TenantId tenantId, Guid jobId, string key, CancellationToken cancellationToken)
    {
        if (!BackupInputValidation.IsValidKey(key))
        {
            throw new ArgumentException("A bounded Idempotency-Key is required.");
        }
        return store.StartRunAsync(tenantId, jobId, key, time.GetUtcNow(), cancellationToken);
    }

    public Task<BackupRun?> CancelAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken) =>
        store.RequestCancellationAsync(tenantId, runId, time.GetUtcNow(), cancellationToken);

    public async Task<BackupTaskSnapshot?> ClaimAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        var claim = await store.ClaimNextAsync(identity.TenantId, identity.AgentId, time.GetUtcNow(), LeaseDuration, cancellationToken);
        if (claim is null) return null;
        return new BackupTaskSnapshot
        {
            RunId = claim.Run.Id, JobId = claim.Run.JobId, TenantId = claim.Run.TenantId.Value,
            AttemptId = claim.Attempt.Id, AttemptNumber = claim.Attempt.Number,
            LeaseToken = claim.Attempt.LeaseToken!.Value, LeaseExpiresAt = claim.Attempt.LeaseExpiresAt!.Value,
            Configuration = ToWire(claim.Run.Configuration), CancellationRequested = claim.Run.CancellationRequestedAt is not null
        };
    }

    public BackupLeaseCommand Command(AgentIdentity identity, Guid attemptId, Guid leaseToken) =>
        new(identity.TenantId, identity.AgentId, attemptId, leaseToken, time.GetUtcNow());

    public async Task<BackupMutationResult> CompleteAsync(AgentIdentity identity, Guid attemptId, BackupTaskCompletionRequest request, CancellationToken cancellationToken)
    {
        var claim = await store.GetAttemptAsync(identity.TenantId, identity.AgentId, attemptId, cancellationToken);
        if (claim is null) return BackupMutationResult.NotFound;
        if (!BackupInputValidation.IsValidCompletion(request, claim.Run.Configuration))
        {
            throw new ArgumentException("Invalid completion metadata.");
        }
        var a = request.Artifact;
        var artifact = a is null ? null : new BackupArtifactResult(a.SizeBytes, a.SourceChecksum, a.ArtifactChecksum, a.ChecksumAlgorithm,
            a.CreatedAt, a.SourceMetadataJson, a.AppliedOptionsJson, a.Replicas.Select(r => new BackupReplicaResult(r.StorageProvider, r.StorageTarget,
                r.Key, r.SizeBytes, r.Checksum, r.ChecksumAlgorithm, r.VerificationState, r.VerificationMethod, r.CreatedAt, r.VerifiedAt)).ToArray());
        return await store.CompleteAttemptAsync(Command(identity, attemptId, request.LeaseToken), new BackupCompletion(request.Status, request.FailureCode, artifact), cancellationToken);
    }

    public static BackupRunView ToView(BackupRun run) => new(run.Id, run.JobId, run.AgentId.Value, run.Status.ToString(), run.BackupType, run.TriggerType,
        ToWire(run.Configuration), run.CurrentAttemptNumber, run.CreatedAt, run.StartedAt, run.CompletedAt, run.CancellationRequestedAt,
        run.TerminalReason, Reason(run.TerminalReason), run.Attempts.Select(a => new BackupAttemptView(a.Id, a.Number,
            a.Status.ToString(), a.CreatedAt, a.StartedAt, a.CompletedAt, a.TerminalReason, Reason(a.TerminalReason), a.ProgressStage, a.ProgressPercent,
            a.ProgressAt, a.Artifacts.Select(artifact => new BackupArtifactView(artifact.Id, artifact.SizeBytes, artifact.SourceChecksum,
                artifact.ArtifactChecksum, artifact.ChecksumAlgorithm, artifact.CreatedAt, artifact.Replicas.Select(r =>
                    new BackupReplicaView(r.Id, r.StorageProvider, r.StorageTarget, r.Key, r.SizeBytes, r.Checksum, r.ChecksumAlgorithm,
                        r.VerificationState, r.VerificationMethod, r.CreatedAt, r.VerifiedAt)).ToArray())).ToArray())).ToArray());

    private static string? Reason(string? code) => code switch
    {
        null => null,
        "PreCheckFailed" => "The database backup pre-check failed.",
        "SqlBackupFailed" => "SQL Server could not complete the full backup.",
        "VerificationFailed" => "The database backup did not pass verification.",
        "HashFailed" => "The backup checksum could not be computed.",
        "StorageWriteFailed" => "The backup artifact could not be written to storage.",
        "ReplicaVerificationFailed" => "The stored backup replica did not pass verification.",
        "Cancelled" => "The backup was cancelled.",
        "AgentLost" => "The agent lease expired before the backup completed.",
        _ => "The backup could not complete because of an internal error."
    };
    public static BackupExecutionConfiguration ToWire(BackupConfiguration c) => new()
    {
        Engine = c.Engine, Database = c.Database, Host = c.Host, Port = c.Port, IntegratedSecurity = c.IntegratedSecurity,
        DatabaseCredentialReference = c.DatabaseCredentialReference, Encrypt = c.Encrypt, TrustServerCertificate = c.TrustServerCertificate,
        StagingReference = c.StagingReference, StorageProvider = c.StorageProvider, StoragePrefix = c.StoragePrefix,
        CopyOnly = c.CopyOnly, ChecksumPolicy = c.ChecksumPolicy, CompressionPolicy = c.CompressionPolicy
    };
}
