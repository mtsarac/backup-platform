using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

/// <summary>Retry policy as it crosses the API. Omitted members take the platform default; delays are seconds.</summary>
public sealed record BackupRetryPolicyRequest(int? MaxAttempts = null, double? InitialDelaySeconds = null, double? MaxDelaySeconds = null, string? Backoff = null, double? Jitter = null);
public sealed record BackupRetryPolicyView(int MaxAttempts, double InitialDelaySeconds, double MaxDelaySeconds, string Backoff, double Jitter);
/// <summary>Retention policy as it crosses the API. Omitted members keep the platform default (on create) or the job's current value (on change); durations are seconds.</summary>
public sealed record BackupRetentionPolicyRequest(bool? Enabled = null, double? KeepForSeconds = null, double? DeleteGracePeriodSeconds = null);
public sealed record BackupRetentionPolicyView(bool Enabled, double KeepForSeconds, double DeleteGracePeriodSeconds);
public sealed record CreateBackupJobRequest(string Name, Guid AgentId, BackupExecutionConfiguration Configuration, BackupRetryPolicyRequest? RetryPolicy = null, string? OverlapPolicy = null, BackupScheduleRequest? Schedule = null, BackupRetentionPolicyRequest? Retention = null);
public sealed record BackupJobView(Guid Id, Guid AgentId, string Name, string BackupType, string OverlapPolicy, BackupRetryPolicyView RetryPolicy, bool Enabled, DateTimeOffset CreatedAt, BackupExecutionConfiguration Configuration, BackupScheduleView? Schedule = null, BackupRetentionPolicyView? Retention = null);
/// <summary>A run. <see cref="ScheduledFor"/> and <see cref="ScheduleDelaySeconds"/> are set for scheduled runs only: the instant the schedule asked for, and how long after it the run was actually created.</summary>
public sealed record BackupRunView(Guid Id, Guid JobId, Guid AgentId, string Status, string BackupType, string TriggerType, BackupExecutionConfiguration Configuration, int CurrentAttemptNumber, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset? CancellationRequestedAt, string? ReasonCode, string? Reason, BackupRetryView Retry, IReadOnlyList<BackupAttemptView> Attempts, DateTimeOffset? ScheduledFor = null, double? ScheduleDelaySeconds = null, DateTimeOffset? ClaimDeadline = null, BackupRetentionPolicyView? Retention = null);
/// <summary>Where the run stands in its retry lifecycle. <see cref="AttemptsUsed"/> counts attempts created, <see cref="MaxAttempts"/> is the total allowed (not a retry count).</summary>
public sealed record BackupRetryView(int MaxAttempts, int AttemptsUsed, string State, DateTimeOffset? NextAttemptAt, string? LastFailureCode, string? LastFailureReason, bool? LastFailureRetryable, string OverlapPolicy, BackupRetryPolicyView Policy);
public sealed record BackupAttemptView(Guid Id, int Number, string Status, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? ReasonCode, string? Reason, bool? Retryable, string? ProgressStage, double? ProgressPercent, DateTimeOffset? ProgressAt, IReadOnlyList<BackupArtifactView> Artifacts, string? Trigger = null);
/// <summary>
/// A stored backup. <see cref="State"/> is Available, DeletePending or Deleted (derived from the replicas). <see cref="RetentionExpiresAt"/> is fixed when the artifact
/// was accepted; <see cref="DeleteAfter"/> is the earliest physical deletion of any replica still waiting. RunId, JobId and AttemptId let a history view link back.
/// </summary>
public sealed record BackupArtifactView(Guid Id, long SizeBytes, string SourceChecksum, string ArtifactChecksum, string ChecksumAlgorithm, DateTimeOffset CreatedAt, IReadOnlyList<BackupReplicaView> Replicas,
    string State = "Available", DateTimeOffset? AcceptedAt = null, DateTimeOffset? RetentionExpiresAt = null, DateTimeOffset? RetentionPinnedAt = null, DateTimeOffset? DeleteAfter = null, string? DeleteReason = null,
    Guid? RunId = null, Guid? JobId = null, Guid? AttemptId = null);
/// <summary>One physical copy. <see cref="Usable"/> is true while the bytes are intact and may be restored from (Available, or DeletePending inside its grace period).</summary>
public sealed record BackupReplicaView(Guid Id, string StorageProvider, string StorageTarget, string Key, long SizeBytes, string Checksum, string ChecksumAlgorithm, string VerificationState, string VerificationMethod, DateTimeOffset CreatedAt, DateTimeOffset? VerifiedAt,
    string Status = "Available", bool Usable = true, DateTimeOffset? DeleteRequestedAt = null, DateTimeOffset? DeleteAfter = null, string? DeleteReason = null, DateTimeOffset? DeletedAt = null,
    int DeleteAttempts = 0, string? DeleteFailureCode = null, DateTimeOffset? DeleteNextAttemptAt = null);
public sealed class BackupOrchestrator(IBackupRunStore store, TimeProvider time, BackupSchedulingOptions? scheduling = null, BackupRetentionOptions? retention = null)
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
        var now = time.GetUtcNow();
        var options = scheduling ?? new BackupSchedulingOptions();
        var overlap = ResolveOverlap(request);
        var schedule = request.Schedule is null ? null : BackupScheduleService.ToDomain(request.Schedule, null, now, options);
        var job = await store.CreateJobAsync(new BackupJob(Guid.NewGuid(), tenantId, request.Name.Trim(), new AgentId(request.AgentId), configuration, now,
            ToDomain(request.RetryPolicy), overlap, schedule, ToDomain(request.Retention, retention ?? new BackupRetentionOptions(), null)), cancellationToken);
        return job is null ? null : ToView(job, now);
    }

    public Task<BackupStartResult> StartRunAsync(TenantId tenantId, Guid jobId, string key, CancellationToken cancellationToken)
    {
        if (!BackupInputValidation.IsValidKey(key))
        {
            throw new ArgumentException("A bounded Idempotency-Key is required.");
        }
        return store.StartRunAsync(tenantId, jobId, key, time.GetUtcNow(), cancellationToken);
    }

    public Task<BackupRun?> CancelAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken) =>
        store.RequestCancellationAsync(tenantId, runId, time.GetUtcNow(), cancellationToken);

    public Task<BackupRetryResult> RetryAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken) =>
        store.RetryRunAsync(tenantId, runId, time.GetUtcNow(), cancellationToken);

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

    public static BackupRetryPolicy ToDomain(BackupRetryPolicyRequest? request)
    {
        if (request is null) return BackupRetryPolicy.Default;
        var policy = BackupRetryPolicy.Default;
        BackupBackoffStrategy? backoff = request.Backoff is null ? null
            : TryParseName<BackupBackoffStrategy>(request.Backoff, out var parsed) ? parsed
            : throw new ArgumentException("Unknown backoff strategy.");
        return (policy with
        {
            MaxAttempts = request.MaxAttempts ?? policy.MaxAttempts,
            InitialDelay = request.InitialDelaySeconds is { } initial ? Seconds(initial) : policy.InitialDelay,
            MaxDelay = request.MaxDelaySeconds is { } max ? Seconds(max) : policy.MaxDelay,
            Backoff = backoff ?? policy.Backoff,
            Jitter = request.Jitter ?? policy.Jitter
        }).Validate();
    }

    public static BackupOverlapPolicy ToDomain(string? overlap) => overlap is null ? BackupOverlapPolicy.Skip
        : TryParseName<BackupOverlapPolicy>(overlap, out var parsed) ? parsed
        : throw new ArgumentException("Unknown overlap policy.");

    /// <summary>Matches a defined member by name only: <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> would also accept numeric text such as "1".</summary>
    private static bool TryParseName<T>(string value, out T result) where T : struct, Enum
    {
        foreach (var name in Enum.GetNames<T>())
        {
            if (!string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) continue;
            result = Enum.Parse<T>(name);
            return true;
        }
        result = default;
        return false;
    }

    private static TimeSpan Seconds(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 86_400 ? TimeSpan.FromSeconds(value) : throw new ArgumentException("A delay is out of range.");

    public static BackupRetryPolicyView ToView(BackupRetryPolicy policy) => new(policy.MaxAttempts, policy.InitialDelay.TotalSeconds,
        policy.MaxDelay.TotalSeconds, policy.Backoff.ToString(), policy.Jitter);

    public static BackupRetentionPolicyView ToView(BackupRetentionPolicy policy) => new(policy.Enabled, policy.KeepFor.TotalSeconds, policy.DeleteGracePeriod.TotalSeconds);

    /// <summary>
    /// The policy a request asks for. <paramref name="basis"/> is what omitted members keep (the job's current policy); null means the platform defaults.
    /// A policy that is not disabled, and any grace period, must respect the operational minimums of <paramref name="options"/>.
    /// </summary>
    public static BackupRetentionPolicy ToDomain(BackupRetentionPolicyRequest? request, BackupRetentionOptions options, BackupRetentionPolicy? basis)
    {
        var start = basis ?? options.DefaultPolicy();
        if (request is null) return start.Validate();
        var policy = (start with
        {
            Enabled = request.Enabled ?? start.Enabled,
            KeepFor = request.KeepForSeconds is { } keep ? RetentionSeconds(keep) : start.KeepFor,
            DeleteGracePeriod = request.DeleteGracePeriodSeconds is { } grace ? RetentionSeconds(grace) : start.DeleteGracePeriod
        }).Validate();
        if (policy.Enabled && policy.KeepFor < options.MinimumKeepFor) throw new ArgumentException("KeepFor is below the platform minimum.");
        if (policy.DeleteGracePeriod < options.MinimumDeleteGracePeriod) throw new ArgumentException("DeleteGracePeriod is below the platform minimum.");
        return policy;
    }

    private static TimeSpan RetentionSeconds(double value) =>
        double.IsFinite(value) && value > 0 && value <= BackupRetentionPolicy.MaximumKeepForLimit.TotalSeconds ? TimeSpan.FromSeconds(value) : throw new ArgumentException("A retention duration is out of range.");

    /// <summary>The artifact as the API shows it. <paramref name="runId"/> and <paramref name="jobId"/> are looked up by the caller (they are not on the artifact).</summary>
    public static BackupArtifactView ToView(BackupArtifact artifact, Guid? runId = null, Guid? jobId = null)
    {
        var waiting = artifact.Replicas.Where(r => r.DeleteAfter is not null && r.Status is BackupReplicaStatus.DeletePending or BackupReplicaStatus.DeleteFailed or BackupReplicaStatus.Deleting).ToArray();
        return new(artifact.Id, artifact.SizeBytes, artifact.SourceChecksum, artifact.ArtifactChecksum, artifact.ChecksumAlgorithm, artifact.CreatedAt,
            artifact.Replicas.Select(ToView).ToArray(), artifact.State.ToString(), artifact.AcceptedAt, artifact.RetentionExpiresAt, artifact.RetentionPinnedAt,
            waiting.Length == 0 ? null : waiting.Min(r => r.DeleteAfter), waiting.OrderBy(r => r.DeleteAfter).FirstOrDefault()?.DeleteReason?.ToString(),
            runId, jobId, artifact.AttemptId);
    }

    public static BackupReplicaView ToView(BackupReplica r) => new(r.Id, r.StorageProvider, r.StorageTarget, r.Key, r.SizeBytes, r.Checksum, r.ChecksumAlgorithm,
        r.VerificationState, r.VerificationMethod, r.CreatedAt, r.VerifiedAt, r.Status.ToString(), r.IsUsable, r.DeleteRequestedAt, r.DeleteAfter,
        r.DeleteReason?.ToString(), r.DeletedAt, r.DeleteAttempts, r.DeleteFailureCode, r.DeleteNextAttemptAt);

    public static BackupJobView ToView(BackupJob job, DateTimeOffset now) => new(job.Id, job.AgentId.Value, job.Name, job.BackupType, job.OverlapPolicy.ToString(),
        ToView(job.RetryPolicy), job.Enabled, job.CreatedAt, ToWire(job.Configuration), job.Schedule is null ? null : ToView(job, job.Schedule, now), ToView(job.RetentionPolicy));

    private static BackupScheduleView ToView(BackupJob job, BackupSchedule schedule, DateTimeOffset now) => new(schedule.Enabled, schedule.CronExpression,
        schedule.TimeZoneId, schedule.MissedRunGracePeriod.TotalSeconds, job.OverlapPolicy.ToString(), schedule.EffectiveFrom,
        job.IsScheduled ? schedule.NextOccurrenceAfter(now) : null);

    /// <summary>The overlap policy a create request asks for: the job's, the schedule's, or Skip. Two different answers are a conflict, not a guess.</summary>
    private static BackupOverlapPolicy ResolveOverlap(CreateBackupJobRequest request)
    {
        var job = request.OverlapPolicy is null ? (BackupOverlapPolicy?)null : ToDomain(request.OverlapPolicy);
        var schedule = request.Schedule?.OverlapPolicy is null ? (BackupOverlapPolicy?)null : ToDomain(request.Schedule.OverlapPolicy);
        if (job is { } a && schedule is { } b && a != b) throw new ArgumentException("The job and schedule overlap policies disagree.");
        return job ?? schedule ?? BackupOverlapPolicy.Skip;
    }

    public static BackupRunView ToView(BackupRun run)
    {
        var lastFailure = run.Attempts.Where(a => a.Status is BackupStatus.Failed or BackupStatus.AgentLost).OrderBy(a => a.Number).LastOrDefault();
        var retry = new BackupRetryView(run.RetryPolicy.MaxAttempts, run.CurrentAttemptNumber, run.RetryState.ToString(), run.NextAttemptAt,
            lastFailure?.TerminalReason, Reason(lastFailure?.TerminalReason), lastFailure is null ? null : BackupFailureCodes.IsRetryable(lastFailure.TerminalReason),
            run.OverlapPolicy.ToString(), ToView(run.RetryPolicy));
        return new(run.Id, run.JobId, run.AgentId.Value, run.Status.ToString(), run.BackupType, run.TriggerType,
            ToWire(run.Configuration), run.CurrentAttemptNumber, run.CreatedAt, run.StartedAt, run.CompletedAt, run.CancellationRequestedAt,
            run.TerminalReason, Reason(run.TerminalReason), retry, run.Attempts.OrderBy(a => a.Number).Select(a => new BackupAttemptView(a.Id, a.Number,
                a.Status.ToString(), a.CreatedAt, a.StartedAt, a.CompletedAt, a.TerminalReason, Reason(a.TerminalReason),
                a.Status is BackupStatus.Failed or BackupStatus.AgentLost ? BackupFailureCodes.IsRetryable(a.TerminalReason) : null, a.ProgressStage, a.ProgressPercent,
                a.ProgressAt, a.Artifacts.Select(artifact => ToView(artifact, run.Id, run.JobId)).ToArray(),
                a.Number == 1 ? run.TriggerType : BackupTriggerTypes.Retry)).ToArray(),
            run.ScheduledOccurrence, run.ScheduledOccurrence is { } scheduledFor ? Math.Max(0, (run.CreatedAt - scheduledFor).TotalSeconds) : null, run.ClaimDeadline, ToView(run.RetentionPolicy));
    }

    private static string? Reason(string? code) => code switch
    {
        null => null,
        BackupFailureCodes.PreCheckFailed => "The database backup pre-check failed.",
        BackupFailureCodes.SqlBackupFailed => "SQL Server could not complete the full backup.",
        BackupFailureCodes.VerificationFailed => "The database backup did not pass verification.",
        BackupFailureCodes.HashFailed => "The backup checksum could not be computed.",
        BackupFailureCodes.StorageWriteFailed => "The backup artifact could not be written to storage.",
        BackupFailureCodes.ReplicaVerificationFailed => "The stored backup replica did not pass verification.",
        BackupFailureCodes.DatabaseUnavailable => "The database server could not be reached.",
        BackupFailureCodes.OperationTimedOut => "A backup operation timed out.",
        BackupFailureCodes.StorageUnavailable => "The backup storage was temporarily unavailable.",
        BackupFailureCodes.DatabaseNotFound => "The database does not exist on the server.",
        BackupFailureCodes.InvalidConfiguration => "The backup configuration is invalid.",
        BackupFailureCodes.UnsupportedCapability => "The database does not support a requested backup capability.",
        BackupFailureCodes.AuthenticationFailed => "The database credentials were rejected.",
        BackupFailureCodes.PermissionDenied => "A required permission is missing.",
        BackupFailureCodes.StagingInvalid => "The staging directory is not usable.",
        BackupFailureCodes.Cancelled => "The backup was cancelled.",
        BackupFailureCodes.ScheduleGraceExpired => "The scheduled backup could not start within its grace period and was dropped.",
        BackupFailureCodes.AgentLost => "The agent lease expired before the backup completed.",
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
