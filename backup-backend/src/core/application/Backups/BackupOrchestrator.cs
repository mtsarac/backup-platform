using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Storage;
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
public sealed record CreateBackupJobRequest(string Name, Guid AgentId, BackupExecutionConfiguration Configuration, BackupRetryPolicyRequest? RetryPolicy = null, string? OverlapPolicy = null, BackupScheduleRequest? Schedule = null, BackupRetentionPolicyRequest? Retention = null, bool? Enabled = null);
/// <summary>
/// Every editable part of a job in one request. Omitted (null) members keep their current value; <see cref="RemoveSchedule"/> removes the schedule
/// (it cannot be combined with <see cref="Schedule"/>). The whole request is validated and committed as one unit.
/// </summary>
public sealed record UpdateBackupJobRequest(string Name, Guid AgentId, BackupExecutionConfiguration Configuration, BackupRetryPolicyRequest? RetryPolicy = null, string? OverlapPolicy = null,
    BackupScheduleRequest? Schedule = null, bool RemoveSchedule = false, BackupRetentionPolicyRequest? Retention = null, bool? Enabled = null);
public sealed record BackupLastRunView(Guid Id, string Status, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? ReasonCode);
public sealed record BackupJobView(Guid Id, Guid AgentId, string Name, string BackupType, string OverlapPolicy, BackupRetryPolicyView RetryPolicy, bool Enabled, DateTimeOffset CreatedAt, BackupExecutionConfiguration Configuration, BackupScheduleView? Schedule = null, BackupRetentionPolicyView? Retention = null, BackupLastRunView? LastRun = null, string? AgentName = null, DateTimeOffset? NextOccurrenceAt = null);
/// <summary>A run. <see cref="ScheduledFor"/> and <see cref="ScheduleDelaySeconds"/> are set for scheduled runs only: the instant the schedule asked for, and how long after it the run was actually created.</summary>
public sealed record BackupRunView(Guid Id, Guid JobId, Guid AgentId, string Status, string BackupType, string TriggerType, BackupExecutionConfiguration Configuration, int CurrentAttemptNumber, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset? CancellationRequestedAt, string? ReasonCode, string? Reason, BackupRetryView Retry, IReadOnlyList<BackupAttemptView> Attempts, DateTimeOffset? ScheduledFor = null, double? ScheduleDelaySeconds = null, DateTimeOffset? ClaimDeadline = null, BackupRetentionPolicyView? Retention = null, string? JobName = null, string? AgentName = null, BackupReplicationView? Replication = null);
/// <summary>Where the run stands in its retry lifecycle. <see cref="AttemptsUsed"/> counts attempts created, <see cref="MaxAttempts"/> is the total allowed (not a retry count).</summary>
public sealed record BackupRetryView(int MaxAttempts, int AttemptsUsed, string State, DateTimeOffset? NextAttemptAt, string? LastFailureCode, string? LastFailureReason, bool? LastFailureRetryable, string OverlapPolicy, BackupRetryPolicyView Policy);
public sealed record BackupAttemptView(Guid Id, int Number, string Status, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? ReasonCode, string? Reason, bool? Retryable, string? ProgressStage, double? ProgressPercent, DateTimeOffset? ProgressAt, IReadOnlyList<BackupArtifactView> Artifacts, string? Trigger = null, BackupReplicationView? Replication = null);
/// <summary>
/// A stored backup. <see cref="State"/> is Available, DeletePending or Deleted (derived from the replicas). <see cref="RetentionExpiresAt"/> is fixed when the artifact
/// was accepted; <see cref="DeleteAfter"/> is the earliest physical deletion of any replica still waiting. RunId, JobId and AttemptId let a history view link back.
/// </summary>
public sealed record BackupArtifactView(Guid Id, long SizeBytes, string SourceChecksum, string ArtifactChecksum, string ChecksumAlgorithm, DateTimeOffset CreatedAt, IReadOnlyList<BackupReplicaView> Replicas,
    string State = "Available", DateTimeOffset? AcceptedAt = null, DateTimeOffset? RetentionExpiresAt = null, DateTimeOffset? RetentionPinnedAt = null, DateTimeOffset? DeleteAfter = null, string? DeleteReason = null,
    Guid? RunId = null, Guid? JobId = null, Guid? AttemptId = null, string? JobName = null, string? DatabaseName = null, string? AgentName = null, bool CancelDeleteAllowed = false, Guid? AgentId = null, string BackupType = "Full", BackupMetadataView? BackupMetadata = null,
    BackupReplicationView? Replication = null, ArtifactEncryptionView? Encryption = null);
/// <summary>An encrypted artifact's key index: which tenant wrap key sealed it and which recipients its header names. No wrapped key, no key material.</summary>
public sealed record ArtifactEncryptionView(string Format, string KeyId, int KeyVersion, IReadOnlyList<ArtifactRecipientView> Recipients);
public sealed record ArtifactRecipientView(string Type, string KeyId, int KeyVersion);
/// <summary>One physical copy. <see cref="Usable"/> is true while the bytes are intact and may be restored from (Available, or DeletePending inside its grace period).</summary>
public sealed record BackupReplicaView(Guid Id, string StorageProvider, string StorageTarget, string Key, long SizeBytes, string Checksum, string ChecksumAlgorithm, string VerificationState, string VerificationMethod, DateTimeOffset CreatedAt, DateTimeOffset? VerifiedAt,
    string Status = "Available", bool Usable = true, DateTimeOffset? DeleteRequestedAt = null, DateTimeOffset? DeleteAfter = null, string? DeleteReason = null, DateTimeOffset? DeletedAt = null,
    int DeleteAttempts = 0, string? DeleteFailureCode = null, DateTimeOffset? DeleteNextAttemptAt = null,
    string DeletionExecutor = "Backend", bool WaitingForAgent = false, Guid? StorageConnectionId = null);
/// <summary>
/// Replication compliance, the backend's authoritative answer to "do the verified copies satisfy the job's policy". It is independent of the run's status and of
/// the engine's outcome: a run can be Succeeded and Degraded, or Succeeded and InsufficientReplicas. <see cref="Status"/> is Healthy, Degraded, InsufficientReplicas or Unavailable.
/// <see cref="Destinations"/> lists each snapshot destination (empty on an artifact listing, where only the replicas are known).
/// </summary>
public sealed record BackupReplicationView(int RequiredVerifiedReplicas, int VerifiedReplicas, int DestinationCount, string Status, bool PolicySatisfied, IReadOnlyList<BackupDestinationHealthView> Destinations);
public sealed record BackupDestinationHealthView(string StorageProvider, Guid? StorageConnectionId, bool IsPrimary, string State, string? FailureCode, string? FailureReason, bool ObjectMayRemain, Guid? ReplicaId, string? ReplicaStatus);
public sealed class BackupOrchestrator(IBackupRunStore store, TimeProvider time, BackupSchedulingOptions? scheduling = null, BackupRetentionOptions? retention = null, ITenantWrapKeyStore? wrapKeys = null,
    ArtifactRecoveryOptions? recovery = null, ITenantRecoveryKeyStore? tenantRecoveryKeys = null, IStorageCapacityProbeTrigger? probeTrigger = null)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(120);

    public async Task<BackupJobView?> CreateJobAsync(TenantId tenantId, CreateBackupJobRequest request, CancellationToken cancellationToken, UserId? actor = null)
    {
        BackupInputValidation.Validate(request);
        var configuration = ToDomain(request.Configuration);
        var now = time.GetUtcNow();
        var options = scheduling ?? new BackupSchedulingOptions();
        var overlap = ResolveOverlap(request);
        var schedule = request.Schedule is null ? null : BackupScheduleService.ToDomain(request.Schedule, null, now, options);
        var newJob = new BackupJob(Guid.NewGuid(), tenantId, request.Name.Trim(), new AgentId(request.AgentId), configuration, now,
            ToDomain(request.RetryPolicy), overlap, schedule, ToDomain(request.Retention, retention ?? new BackupRetentionOptions(), null));
        if (request.Enabled is false) newJob.Disable();
        var job = await store.CreateJobAsync(newJob, cancellationToken, actor);
        return job is null ? null : ToView(job, now);
    }

    /// <summary>
    /// Validates everything first, then applies general fields, schedule, retention and enabled state to the job loaded under the agent lock, in one
    /// transaction. Any invalid part (or a retention change the caller may not make) leaves the job untouched.
    /// </summary>
    public async Task<BackupJobView?> UpdateJobAsync(TenantId tenantId, Guid jobId, UpdateBackupJobRequest request, CancellationToken cancellationToken, UserId? actor = null)
    {
        var validation = new CreateBackupJobRequest(request.Name, request.AgentId, request.Configuration, request.RetryPolicy, request.OverlapPolicy, request.Schedule, request.Retention);
        BackupInputValidation.Validate(validation);
        if (request.RemoveSchedule && request.Schedule is not null) throw new InvalidBackupJobException("schedule", "conflicting_policy");
        var now = time.GetUtcNow();
        var schedulingOptions = scheduling ?? new BackupSchedulingOptions();
        var retentionOptions = retention ?? new BackupRetentionOptions();
        var scheduleOverlap = request.Schedule?.OverlapPolicy is null ? (BackupOverlapPolicy?)null : ToDomain(request.Schedule.OverlapPolicy);
        void ApplyRest(BackupJob target)
        {
            if (request.RemoveSchedule) target.ChangeSchedule(null);
            else if (request.Schedule is not null) target.ChangeSchedule(BackupScheduleService.ToDomain(request.Schedule, target.Schedule, now, schedulingOptions), scheduleOverlap);
            if (request.Retention is not null)
            {
                try { target.ChangeRetention(ToDomain(request.Retention, retentionOptions, target.RetentionPolicy)); }
                catch (ArgumentException) { throw new InvalidBackupJobException("retention"); }
            }
            if (request.Enabled is true) target.Enable(now);
            else if (request.Enabled is false) target.Disable();
        }
        var command = new BackupJobUpdate(request.Name, new AgentId(request.AgentId), ToDomain(request.Configuration),
            request.RetryPolicy is null ? null : ToDomain(request.RetryPolicy),
            request.OverlapPolicy is null ? null : ToDomain(request.OverlapPolicy), ApplyRest);
        var job = await store.UpdateJobAsync(tenantId, jobId, command, cancellationToken, actor, now);
        return job is null ? null : ToView(job, now);
    }

    private static BackupConfiguration ToDomain(BackupExecutionConfiguration c)
    {
        var destinations = (c.Destinations ?? []).Select(d => new BackupDestination(d.StorageProvider, d.StorageConnectionId, d.IsPrimary)
        {
            StorageExecutionLocation = d.StorageExecutionLocation switch
            {
                "Backend" => BackupStorageExecutionLocation.Backend,
                "Agent" => BackupStorageExecutionLocation.Agent,
                _ => throw new ArgumentException("Invalid storage execution location for destination.")
            },
            Direct = d.StorageProvider == "local"
        }).ToArray();

        return new(c.Engine, c.Database, c.Host, c.Port,
            c.IntegratedSecurity, c.DatabaseCredentialReference, c.Encrypt, c.TrustServerCertificate, c.StagingReference,
            c.StoragePrefix, c.CopyOnly, c.ChecksumPolicy, c.CompressionPolicy)
        {
            Destinations = destinations,
            RequiredVerifiedReplicaCount = c.RequiredVerifiedReplicaCount > 0 ? c.RequiredVerifiedReplicaCount : 1,
            EncryptionPolicy = c.EncryptionPolicy
        };
    }

    public Task<BackupStartResult> StartRunAsync(TenantId tenantId, Guid jobId, string key, CancellationToken cancellationToken, UserId? actor = null)
    {
        if (!BackupInputValidation.IsValidKey(key))
        {
            throw new ArgumentException("A bounded Idempotency-Key is required.");
        }
        return store.StartRunAsync(tenantId, jobId, key, time.GetUtcNow(), cancellationToken, actor);
    }

    public Task<BackupRun?> CancelAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken, UserId? actor = null) =>
        store.RequestCancellationAsync(tenantId, runId, time.GetUtcNow(), cancellationToken, actor);

    public Task<BackupRetryResult> RetryAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken, UserId? actor = null) =>
        store.RetryRunAsync(tenantId, runId, time.GetUtcNow(), cancellationToken, actor);

    public async Task<BackupTaskSnapshot?> ClaimAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        var claim = await store.ClaimNextAsync(identity.TenantId, identity.AgentId, time.GetUtcNow(), LeaseDuration, cancellationToken);
        if (claim is null) return null;
        var policy = claim.Run.Configuration.EncryptionPolicy;
        BackupEncryptionInstruction? encryption = null;
        if (policy != "Off")
        {
            // A job that asks for encryption is never handed out without a key: silently writing plaintext is the failure this policy exists to prevent.
            var key = await (wrapKeys ?? throw new InvalidOperationException("Encryption requires a tenant wrap key store.")).EnsureActiveAsync(claim.Run.TenantId, cancellationToken);
            var recipients = new List<BackupEncryptionRecipient> { Recipient(BackupRecipientTypes.TenantWrap, key.Version, key.KeyId, key.PublicKeySpki) };
            // Without a platform recovery key the artifact is still encrypted to the tenant key alone (Required included): the platform logs that at startup, and the artifact's
            // recipient list shows it, but a missing recovery key never blocks or downgrades a backup.
            if (recovery?.PlatformRecovery is { } platform) recipients.Add(Recipient(BackupRecipientTypes.PlatformRecovery, platform.Version, platform.KeyId, platform.PublicKeySpki));
            if (tenantRecoveryKeys is not null && await tenantRecoveryKeys.GetAsync(claim.Run.TenantId, cancellationToken) is { } tenantKey)
                recipients.Add(Recipient(BackupRecipientTypes.TenantRecovery, tenantKey.Version, tenantKey.KeyId, tenantKey.PublicKeySpki));
            encryption = new BackupEncryptionInstruction { Required = policy == "Required", Recipients = recipients };
        }
        return new BackupTaskSnapshot
        {
            RunId = claim.Run.Id, JobId = claim.Run.JobId, TenantId = claim.Run.TenantId.Value,
            AttemptId = claim.Attempt.Id, AttemptNumber = claim.Attempt.Number,
            LeaseToken = claim.Attempt.LeaseToken!.Value, LeaseExpiresAt = claim.Attempt.LeaseExpiresAt!.Value,
            MaxArtifactBytes = claim.MaxArtifactBytes,
            Configuration = ToWire(claim.Run.Configuration), CancellationRequested = claim.Run.CancellationRequestedAt is not null,
            Encryption = encryption
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
        request = await ApplyEncryptionPolicyAsync(identity.TenantId, claim.Run.Configuration, request, cancellationToken);
        var a = request.Artifact;
        var artifact = a is null ? null : new BackupArtifactResult(a.SizeBytes, a.SourceChecksum, a.ArtifactChecksum, a.ChecksumAlgorithm,
            a.CreatedAt, a.SourceMetadataJson, a.AppliedOptionsJson, a.Replicas.Select(r => new BackupReplicaResult(r.StorageProvider, r.StorageTarget,
                r.Key, r.SizeBytes, r.Checksum, r.ChecksumAlgorithm, r.VerificationState, r.VerificationMethod, r.CreatedAt, r.VerifiedAt, r.StorageConnectionId)).ToArray(),
            a.Format, a.EncryptionKeyId, a.EncryptionKeyVersion, a.EncryptionWrappedDekBase64, a.Format == ArtifactFormats.Bpe2 ? RecipientIndex(a.EncryptionRecipients) : null);
        var destinations = request.Destinations.Select(d => new BackupDestinationReport(d.StorageProvider, d.StorageConnectionId, d.State, d.Upload, d.Verification,
            d.FailureCode, d.Key, d.ObjectMayRemain, d.StartedAt, d.CompletedAt)).ToArray();
        var mutationResult = await store.CompleteAttemptAsync(Command(identity, attemptId, request.LeaseToken), new BackupCompletion(request.Status, request.FailureCode, artifact, destinations), cancellationToken);
        if (mutationResult == BackupMutationResult.Accepted && probeTrigger is not null)
        {
            var connectionIds = destinations
                .Where(d => d.StorageConnectionId.HasValue && d.StorageConnectionId.Value != Guid.Empty)
                .Select(d => d.StorageConnectionId!.Value)
                .Concat(claim.Run.Configuration.Destinations
                    .Where(d => d.StorageConnectionId.HasValue && d.StorageConnectionId.Value != Guid.Empty)
                    .Select(d => d.StorageConnectionId!.Value))
                .Distinct()
                .ToList();

            foreach (var connId in connectionIds)
            {
                probeTrigger.EnqueueProbe(identity.TenantId, connId);
            }
        }
        return mutationResult;
    }
    /// <summary>
    /// An encrypted artifact must name a key of THIS tenant. A Required job whose artifact is plain is failed (never published); the copies already uploaded are
    /// reported as orphans so retention removes them.
    /// </summary>
    private async Task<BackupTaskCompletionRequest> ApplyEncryptionPolicyAsync(TenantId tenantId, BackupConfiguration configuration, BackupTaskCompletionRequest request, CancellationToken cancellationToken)
    {
        if (request.Artifact is not { } a) return request;
        if (a.Format == ArtifactFormats.Bpe2)
        {
            if (wrapKeys is null || !await wrapKeys.OwnsKeyAsync(tenantId, a.EncryptionKeyId!, a.EncryptionKeyVersion!.Value, cancellationToken)
                || !await RecipientsAreIssuedAsync(tenantId, a.EncryptionRecipients, cancellationToken))
                throw new ArgumentException("Invalid completion metadata.");
            return request;
        }
        if (request.Status != "Succeeded" || configuration.EncryptionPolicy != "Required") return request;
        return request with
        {
            Status = "Failed", FailureCode = BackupFailureCodes.EncryptionRequired, Artifact = a with { Replicas = [] },
            Destinations = request.Destinations.Select(d => d.State != "Verified" ? d : d with
            {
                State = "Failed", Verification = "Failed", FailureCode = BackupFailureCodes.EncryptionRequired, ObjectMayRemain = true
            }).ToArray()
        };
    }

    /// <summary>
    /// Every recipient the agent reports must be one the backend issues: the platform recovery recipient only as the configured key, the tenant recovery recipient only as the tenant's
    /// current key. A tenant-wrap recipient was already matched to the tenant's keys by <c>OwnsKeyAsync</c> (shape and the match with the artifact's key are in the input validation).
    /// A key changed while a backup ran therefore fails that attempt's completion instead of recording a recipient nobody can vouch for.
    /// </summary>
    private async Task<bool> RecipientsAreIssuedAsync(TenantId tenantId, IReadOnlyList<BackupTaskArtifactRecipient> recipients, CancellationToken cancellationToken)
    {
        var platform = recovery?.PlatformRecovery;
        var tenantKey = tenantRecoveryKeys is null ? null : await tenantRecoveryKeys.GetAsync(tenantId, cancellationToken);
        var reportedPlatform = recipients.SingleOrDefault(recipient => recipient.Type == BackupRecipientTypes.PlatformRecovery);
        var reportedTenant = recipients.SingleOrDefault(recipient => recipient.Type == BackupRecipientTypes.TenantRecovery);
        return (platform is null ? reportedPlatform is null : reportedPlatform is not null && reportedPlatform.KeyId == platform.KeyId && reportedPlatform.KeyVersion == platform.Version)
            && (tenantKey is null ? reportedTenant is null : reportedTenant is not null && reportedTenant.KeyId == tenantKey.KeyId && reportedTenant.KeyVersion == tenantKey.Version);
    }

    private static BackupEncryptionRecipient Recipient(string type, int version, string keyId, byte[] spki) =>
        new() { Type = type, KeyVersion = version, KeyId = keyId, PublicKeySpkiBase64 = Convert.ToBase64String(spki) };

    /// <summary>The artifact's recipient index: type, key id and version of each header recipient, no wrapped keys.</summary>
    private static string RecipientIndex(IReadOnlyList<BackupTaskArtifactRecipient> recipients) =>
        System.Text.Json.JsonSerializer.Serialize(recipients.Select(recipient => new { type = recipient.Type, keyId = recipient.KeyId, keyVersion = recipient.KeyVersion }));

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
    public static BackupArtifactView ToView(BackupArtifact artifact, Guid? runId = null, Guid? jobId = null, BackupConfiguration? snapshot = null)
    {
        var waiting = artifact.Replicas.Where(r => r.DeleteAfter is not null && r.Status is BackupReplicaStatus.DeletePending or BackupReplicaStatus.DeleteFailed or BackupReplicaStatus.Deleting).ToArray();
        return new(artifact.Id, artifact.SizeBytes, artifact.SourceChecksum, artifact.ArtifactChecksum, artifact.ChecksumAlgorithm, artifact.CreatedAt,
            artifact.Replicas.Select(ToView).ToArray(), artifact.State.ToString(), artifact.AcceptedAt, artifact.RetentionExpiresAt, artifact.RetentionPinnedAt,
            waiting.Length == 0 ? null : waiting.Min(r => r.DeleteAfter), waiting.OrderBy(r => r.DeleteAfter).FirstOrDefault()?.DeleteReason?.ToString(),
            runId, jobId, artifact.AttemptId, CancelDeleteAllowed: artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.DeletePending && replica.VerificationState == "Verified"), BackupType: artifact.BackupType,
            BackupMetadata: BackupMetadataProjection.Read(artifact.SourceMetadataJson, artifact.AppliedOptionsJson),
            Replication: snapshot is null ? null : ToView(BackupReplicationEvaluator.ForArtifact(snapshot.RequiredVerifiedReplicaCount, snapshot.Destinations.Count, artifact.Replicas)),
            Encryption: ToEncryptionView(artifact));
    }

    private static ArtifactEncryptionView? ToEncryptionView(BackupArtifact artifact)
    {
        if (artifact.Format != BackupArtifact.BpeFormat) return null;
        var recipients = artifact.EncryptionRecipientsJson is null ? []
            : System.Text.Json.JsonSerializer.Deserialize<ArtifactRecipientView[]>(artifact.EncryptionRecipientsJson, System.Text.Json.JsonSerializerOptions.Web) ?? [];
        return new ArtifactEncryptionView(artifact.Format, artifact.EncryptionKeyId!, artifact.EncryptionKeyVersion!.Value, recipients);
    }

    public static BackupReplicaView ToView(BackupReplica r) => new(r.Id, r.StorageProvider, r.StorageTarget, r.Key, r.SizeBytes, r.Checksum, r.ChecksumAlgorithm,
        r.VerificationState, r.VerificationMethod, r.CreatedAt, r.VerifiedAt, r.Status.ToString(), r.IsUsable, r.DeleteRequestedAt, r.DeleteAfter,
        r.DeleteReason?.ToString(), r.DeletedAt, r.DeleteAttempts, r.DeleteFailureCode, r.DeleteNextAttemptAt, r.ExecutionLocation.ToString(), r.WaitingForAgent,
        r.StorageConnectionId == Guid.Empty ? null : r.StorageConnectionId);

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
        return new(run.Id, run.JobId, run.AgentId.Value, RunStatus(run.Status, run.CancellationRequestedAt), run.BackupType, run.TriggerType,
            ToWire(run.Configuration), run.CurrentAttemptNumber, run.CreatedAt, run.StartedAt, run.CompletedAt, run.CancellationRequestedAt,
            run.TerminalReason, Reason(run.TerminalReason), retry, run.Attempts.OrderBy(a => a.Number).Select(a => new BackupAttemptView(a.Id, a.Number,
                a.Status.ToString(), a.CreatedAt, a.StartedAt, a.CompletedAt, a.TerminalReason, Reason(a.TerminalReason),
                a.Status is BackupStatus.Failed or BackupStatus.AgentLost ? BackupFailureCodes.IsRetryable(a.TerminalReason) : null, a.ProgressStage, a.ProgressPercent,
                a.ProgressAt, a.Artifacts.Select(artifact => ToView(artifact, run.Id, run.JobId, run.Configuration)).ToArray(),
                AttemptTrigger(run, a), ToView(BackupReplicationEvaluator.ForAttempt(run.Configuration, a)))).ToArray(),
            run.ScheduledOccurrence, run.ScheduledOccurrence is { } scheduledFor ? Math.Max(0, (run.CreatedAt - scheduledFor).TotalSeconds) : null, run.ClaimDeadline, ToView(run.RetentionPolicy),
            Replication: ToView(EffectiveReplication(run)));
    }

    /// <summary>The replication of the attempt that decides the run: the succeeded one if any, else the latest that actually transferred something.</summary>
    private static BackupReplicationHealth? EffectiveReplication(BackupRun run)
    {
        var health = run.Attempts.OrderByDescending(a => a.Status == BackupStatus.Succeeded).ThenByDescending(a => a.Number)
            .Select(a => BackupReplicationEvaluator.ForAttempt(run.Configuration, a)).FirstOrDefault(h => h is not null);
        return health;
    }

    private static BackupReplicationView? ToView(BackupReplicationHealth? health) => health is null ? null : new(health.RequiredVerifiedReplicas, health.VerifiedReplicas,
        health.DestinationCount, health.Status, health.PolicySatisfied, [.. health.Destinations.Select(d => new BackupDestinationHealthView(
            d.StorageProvider, d.StorageConnectionId, d.IsPrimary, d.State, d.FailureCode, Reason(d.FailureCode), d.ObjectMayRemain, d.ReplicaId, d.ReplicaStatus))]);

    private static string AttemptTrigger(BackupRun run, BackupRunAttempt attempt)
    {
        if (attempt.Number == 1) return run.TriggerType;
        if (run.Facts.Any(fact => fact.AttemptId == attempt.Id && fact.Kind == "BackupManualRetryStarted")) return "ManualRetry";
        if (run.Facts.Any(fact => fact.AttemptId == attempt.Id && fact.Kind == "BackupAutomaticRetryStarted")) return "AutomaticRetry";
        return BackupTriggerTypes.Retry;
    }

    public static string RunStatus(BackupStatus status, DateTimeOffset? cancellationRequestedAt) =>
        cancellationRequestedAt is not null && status is BackupStatus.Leased or BackupStatus.Running ? "CancellationRequested" : status.ToString();

    private static string? Reason(string? code) => code switch
    {
        null => null,
        BackupFailureCodes.PreCheckFailed => "The database backup pre-check failed.",
        BackupFailureCodes.SqlBackupFailed => "SQL Server could not complete the full backup.",
        BackupFailureCodes.VerificationFailed => "The database backup did not pass verification.",
        BackupFailureCodes.HashFailed => "The backup checksum could not be computed.",
        BackupFailureCodes.QuotaExceeded => "The actual backup size exceeds the space available under the hard storage quota; the destination upload was refused.",
        BackupFailureCodes.EncryptionRequired => "The job requires encryption but the backup artifact was not encrypted.",
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
        StagingReference = c.StagingReference, StoragePrefix = c.StoragePrefix,
        CopyOnly = c.CopyOnly, ChecksumPolicy = c.ChecksumPolicy, CompressionPolicy = c.CompressionPolicy, EncryptionPolicy = c.EncryptionPolicy,
        Destinations = c.Destinations.Select(d => new BackupDestinationContract
        {
            StorageProvider = d.StorageProvider,
            StorageConnectionId = d.StorageConnectionId,
            StorageTarget = d.StorageTarget,
            StorageExecutionLocation = d.StorageExecutionLocation.ToString(),
            IsPrimary = d.IsPrimary,
            Direct = d.Direct
        }).ToArray(),
        RequiredVerifiedReplicaCount = c.RequiredVerifiedReplicaCount
    };
}
