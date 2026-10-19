using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ApplicationCompletion = BackupPlatform.Application.Backups.BackupCompletion;
using DomainCompletion = BackupPlatform.Domain.Backups.BackupCompletion;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>
/// All lifecycle writes acquire the tenant-qualified agent row lock first, fencing concurrent polls and callbacks of one agent.
/// Slot-consuming transitions (claim) additionally take one transaction-scoped advisory lock FIRST, so the global, tenant and
/// agent limits are decided against a consistent count of Leased/Running attempts by any number of backend instances. Releasing
/// transitions (complete, expire, cancel) never wait on that lock, so the two lock orders cannot cycle.
/// </summary>
public sealed class PostgreSqlBackupRunStore(DbContextOptions<PlatformDbContext> options, IBackupAdmissionPolicy admission, BackupExecutionOptions? limits = null, Func<double>? jitter = null) : IBackupRunStore
{
    // Arbitrary fixed application key for the claim critical section.
    private const long ClaimLockKey = 0x4250_434C_4149_4D01;
    private static readonly BackupStatus[] ActiveStatuses = [BackupStatus.Pending, BackupStatus.Leased, BackupStatus.Running, BackupStatus.RetryPending];
    private readonly BackupExecutionOptions _limits = limits ?? new BackupExecutionOptions();

    /// <summary>A sample in [0,1) for retry jitter.</summary>
    private double Jitter() => jitter is null ? Random.Shared.NextDouble() : Math.Clamp(jitter(), 0, 0.999999);

    public async Task<BackupJob?> CreateJobAsync(BackupJob job, CancellationToken cancellationToken, UserId? actor = null)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, job.TenantId, job.AgentId, cancellationToken)) return null;
        if (!await context.Agents.AnyAsync(agent => agent.Id == job.AgentId && agent.TenantId == job.TenantId && agent.Status == AgentStatus.Active, cancellationToken)) return null;
        await StorageTargetGate.EnsureJobTargetAsync(context, job.TenantId, job.Configuration, cancellationToken);
        context.BackupJobs.Add(job);
        BackupAudit.AddJobEvent(context, job, actor, job.CreatedAt, AuditActions.BackupJobCreated);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job;
    }
    public async Task<BackupJob?> GetJobAsync(TenantId tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.BackupJobs.AsNoTracking().SingleOrDefaultAsync(job => job.TenantId == tenantId && job.Id == jobId, cancellationToken);
    }
    public async Task<BackupJob?> UpdateJobAsync(TenantId tenantId, Guid jobId, BackupJobUpdate update, CancellationToken cancellationToken, UserId? actor = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        while (true)
        {
            var (job, stale) = await UpdateJobOnceAsync(tenantId, jobId, update, actor, at, cancellationToken);
            if (!stale) return job;
        }
    }
    private async Task<(BackupJob? Job, bool Stale)> UpdateJobOnceAsync(TenantId tenantId, Guid jobId, BackupJobUpdate update, UserId? actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.Id == jobId)
            .Select(job => job.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return (null, false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var firstAgent = owner.Value.CompareTo(update.AgentId.Value) <= 0 ? owner : update.AgentId;
        var secondAgent = firstAgent == owner ? update.AgentId : owner;
        if (!await LockAgentAsync(context, tenantId, firstAgent, cancellationToken)) return (null, false);
        if (secondAgent != firstAgent && !await LockAgentAsync(context, tenantId, secondAgent, cancellationToken)) return (null, false);
        var job = await context.BackupJobs.SingleOrDefaultAsync(job => job.TenantId == tenantId && job.Id == jobId, cancellationToken);
        if (job is null) return (null, false);
        // Another edit moved the job to a different agent after the owner was read: start over under the current owner's locks.
        if (job.AgentId != owner) return (null, true);
        // The target must be Active only when the job moves to it: an unchanged owner that was since disabled or revoked must not block editing (or disabling) the job.
        if (update.AgentId != owner && !await context.Agents.AnyAsync(agent => agent.Id == update.AgentId && agent.TenantId == tenantId && agent.Status == AgentStatus.Active, cancellationToken)) return (null, false);
        // Omitted policies keep the values committed so far, read under the lock rather than from an earlier snapshot.
        await StorageTargetGate.EnsureJobTargetAsync(context, tenantId, update.Configuration, cancellationToken);
        var before = BackupAudit.Capture(job);
        job.Update(update.Name, update.AgentId, update.Configuration, update.RetryPolicy ?? job.RetryPolicy, update.OverlapPolicy ?? job.OverlapPolicy);
        update.ApplyUnderLock?.Invoke(job);
        BackupAudit.RecordJobChanges(context, job, before, actor, now);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (job, false);
    }
    public async Task<BackupStartResult> StartRunAsync(TenantId tenantId, Guid jobId, string idempotencyKey, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null)
    {
        while (true)
        {
            var result = await StartRunOnceAsync(tenantId, jobId, idempotencyKey, now, cancellationToken, actor);
            if (result is not null) return result;
        }
    }
    /// <summary>Null when the job moved to another agent between reading its owner and locking it; the caller starts over.</summary>
    private async Task<BackupStartResult?> StartRunOnceAsync(TenantId tenantId, Guid jobId, string idempotencyKey, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.Id == jobId).Select(job => job.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return new BackupStartResult(BackupStartOutcome.NotFound, null);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, tenantId, owner, cancellationToken)) return new BackupStartResult(BackupStartOutcome.NotFound, null);
        // The key is checked first: replaying a request must return its original run even when the job has since become busy.
        var existing = await Runs(context).SingleOrDefaultAsync(run => run.TenantId == tenantId && run.JobId == jobId && run.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null) return new BackupStartResult(BackupStartOutcome.Replayed, existing);
        var job = await context.BackupJobs.SingleOrDefaultAsync(job => job.TenantId == tenantId && job.Id == jobId && job.Enabled, cancellationToken);
        if (job is not null && job.AgentId != owner) return null;
        if (job is null || !await context.Agents.AnyAsync(agent => agent.Id == owner && agent.TenantId == tenantId && agent.Status == AgentStatus.Active, cancellationToken))
            return new BackupStartResult(BackupStartOutcome.NotFound, null);
        if (job.OverlapPolicy == BackupOverlapPolicy.Skip && await ActiveRunIdAsync(context, tenantId, jobId, null, cancellationToken) is { } active)
            return new BackupStartResult(BackupStartOutcome.SkippedOverlap, null, active);
        // The destination of a NEW run: refused while its storage connection is disabled (or missing), resolved and snapshotted otherwise. No reservation exists yet to give back.
        var target = await StorageTargetGate.ForNewRunAsync(context, tenantId, owner, job.Configuration, now, cancellationToken);
        if (!target.Allowed)
        {
            await transaction.CommitAsync(cancellationToken);
            return new BackupStartResult(BackupStartOutcome.AdmissionDenied, null, null, target.DenialCode);
        }
        // The gate for a NEW run, after everything that makes the request a no-op (replay, skip) and before anything is created. A retry attempt never comes through here.
        var runId = Guid.NewGuid();
        var decision = await BackupAdmissionGate.AdmitAsync(admission, context,
            new BackupAdmissionRequest(tenantId, job.Id, runId, BackupTriggerTypes.Manual, now) { Configuration = target.Configuration }, cancellationToken);
        if (!decision.Allowed)
        {
            // Nothing was created; committing keeps the maintenance the gate did on the way (stale reservations released, a missing projection computed).
            await transaction.CommitAsync(cancellationToken);
            return new BackupStartResult(BackupStartOutcome.AdmissionDenied, null, null, decision.DenialCode);
        }
        try
        {
            var run = new BackupRun(runId, tenantId, job.Id, owner, target.Configuration!, idempotencyKey, now, job.RetryPolicy, job.OverlapPolicy, retentionPolicy: job.RetentionPolicy);
            run.QueueFirstAttempt(Guid.NewGuid(), now);
            context.BackupRuns.Add(run);
            context.AuditEvents.Add(AuditEvent.Record(
                now, actor ?? UserId.System, BackupAudit.ActorKind(actor), AuditActions.BackupRunTriggered, tenantId,
                "backup_run", run.Id.ToString("D"),
                new Dictionary<string, object?> { ["jobId"] = job.Id.ToString("D"), ["runId"] = run.Id.ToString("D"), ["trigger"] = "Manual", ["result"] = "started", ["attemptId"] = run.CurrentAttempt.Id.ToString("D") }));
            try { await context.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // The unique index on active Skip runs is the backstop behind the agent lock.
                await BackupAdmissionGate.ReleaseAfterFailureAsync(admission, tenantId, runId);
                return new BackupStartResult(BackupStartOutcome.SkippedOverlap, null, await ActiveRunIdAsync(context, tenantId, jobId, null, cancellationToken));
            }
            await transaction.CommitAsync(cancellationToken);
            return new BackupStartResult(BackupStartOutcome.Started, run);
        }
        catch
        {
            // Admitted but never created: give the reservation back now instead of waiting for it to lapse.
            await BackupAdmissionGate.ReleaseAfterFailureAsync(admission, tenantId, runId);
            throw;
        }
    }
    public async Task<IReadOnlyList<BackupRun>> ListRunsAsync(TenantId tenantId, Guid? jobId, int limit, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await Runs(context).AsNoTracking().Where(run => run.TenantId == tenantId && (jobId == null || run.JobId == jobId))
            .OrderByDescending(run => run.CreatedAt).ThenBy(run => run.Id).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
    }
    public async Task<BackupRun?> GetRunAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await Runs(context).AsNoTracking().SingleOrDefaultAsync(run => run.TenantId == tenantId && run.Id == runId, cancellationToken);
    }
    public async Task<BackupRun?> RequestCancellationAsync(TenantId tenantId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await context.BackupRuns.AsNoTracking().Where(run => run.TenantId == tenantId && run.Id == runId).Select(run => run.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return null;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, tenantId, owner, cancellationToken)) return null;
        // Loaded under the lock, so a failure or retry that committed first is already visible and cancellation wins from here on.
        var run = await Runs(context).SingleAsync(run => run.TenantId == tenantId && run.Id == runId && run.AgentId == owner, cancellationToken);
        var wasTerminal = run.IsTerminal;
        var attemptWasTerminal = run.CurrentAttempt.IsTerminal;
        var requestedAt = run.CancellationRequestedAt;
        run.RequestCancellation(now);
        if (requestedAt != run.CancellationRequestedAt)
            context.AuditEvents.Add(AuditEvent.Record(
                now, actor ?? UserId.System, BackupAudit.ActorKind(actor), AuditActions.BackupRunCancelRequested, tenantId,
                "backup_run", runId.ToString("D"),
                new Dictionary<string, object?> { ["jobId"] = run.JobId.ToString("D"), ["runId"] = run.Id.ToString("D"), ["attemptId"] = run.CurrentAttempt.Id.ToString("D"), ["result"] = "requested" }));
        BackupAudit.RecordCompletion(context, run, wasTerminal, attemptWasTerminal, now);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return run;
    }
    public async Task<BackupRetryResult> RetryRunAsync(TenantId tenantId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await context.BackupRuns.AsNoTracking().Where(run => run.TenantId == tenantId && run.Id == runId).Select(run => run.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return new BackupRetryResult(BackupRetryOutcome.NotFound, null);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, tenantId, owner, cancellationToken)) return new BackupRetryResult(BackupRetryOutcome.NotFound, null);
        var run = await Runs(context).SingleOrDefaultAsync(run => run.TenantId == tenantId && run.Id == runId && run.AgentId == owner, cancellationToken);
        if (run is null) return new BackupRetryResult(BackupRetryOutcome.NotFound, null);
        var reopens = run.IsTerminal && run.OverlapPolicy == BackupOverlapPolicy.Skip
            && await ActiveRunIdAsync(context, tenantId, run.JobId, run.Id, cancellationToken) is not null;
        var decision = run.RequestManualRetry(Guid.NewGuid(), now);
        var outcome = decision switch
        {
            BackupManualRetryResult.Succeeded => BackupRetryOutcome.Succeeded,
            BackupManualRetryResult.Cancelled => BackupRetryOutcome.Cancelled,
            BackupManualRetryResult.InProgress => BackupRetryOutcome.InProgress,
            BackupManualRetryResult.AttemptsExhausted => BackupRetryOutcome.AttemptsExhausted,
            _ => reopens ? BackupRetryOutcome.OverlapActive : BackupRetryOutcome.Started
        };
        if (outcome != BackupRetryOutcome.Started) return new BackupRetryResult(outcome, run); // nothing is saved: the transaction rolls back
        context.AuditEvents.Add(AuditEvent.Record(
            now, actor ?? UserId.System, BackupAudit.ActorKind(actor), AuditActions.BackupRunRetryRequested, tenantId,
            "backup_run", runId.ToString("D"),
            new Dictionary<string, object?> { ["jobId"] = run.JobId.ToString("D"), ["runId"] = run.Id.ToString("D"), ["attemptId"] = run.CurrentAttempt.Id.ToString("D"), ["retry"] = "Manual", ["result"] = "started" }));
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return new BackupRetryResult(BackupRetryOutcome.OverlapActive, run);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new BackupRetryResult(BackupRetryOutcome.InProgress, run);
        }
        await transaction.CommitAsync(cancellationToken);
        return new BackupRetryResult(BackupRetryOutcome.Started, run);
    }
    public async Task<BackupClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({ClaimLockKey})", cancellationToken);
        if (!await LockAgentAsync(context, tenantId, agentId, cancellationToken)) return null;
        if (!await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == agentId && agent.Status == AgentStatus.Active, cancellationToken)) return null;
        var held = await Runs(context).Where(run => run.TenantId == tenantId && run.AgentId == agentId &&
            (run.Status == BackupStatus.Leased || run.Status == BackupStatus.Running)).ToListAsync(cancellationToken);
        foreach (var run in held)
        {
            var wasTerminal = run.IsTerminal;
            var attemptWasTerminal = run.CurrentAttempt.IsTerminal;
            run.Expire(now, Jitter());
            BackupAudit.RecordCompletion(context, run, wasTerminal, attemptWasTerminal, now);
        }
        await context.SaveChangesAsync(cancellationToken);
        // A backup never runs beside a restore of the same agent: a live (unexpired Leased/Running) restore attempt holds the agent whatever the slot limits say. Decided under the
        // same agent lock the restore claim takes, so exactly one of the two can ever hold the agent. An expired restore lease does not count: the restore sweep will end it.
        if (await context.RestoreAttempts.AsNoTracking().AnyAsync(attempt => attempt.TenantId == tenantId && attempt.AgentId == agentId &&
            (attempt.Status == RestoreStatus.Leased || attempt.Status == RestoreStatus.Running) && attempt.LeaseExpiresAt > now, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        // A slot is a live (unexpired) Leased/Running attempt. Counting rows, not holding tokens, means a crashed
        // worker, a lost agent or a restarted backend cannot leak a slot: the lease simply runs out.
        var live = await context.BackupRunAttempts.AsNoTracking()
            .Where(attempt => (attempt.Status == BackupStatus.Leased || attempt.Status == BackupStatus.Running) && attempt.LeaseExpiresAt > now)
            .Select(attempt => new { attempt.TenantId, attempt.AgentId }).ToListAsync(cancellationToken);
        if (live.Count >= _limits.GlobalMaxConcurrentBackups
            || live.Count(slot => slot.TenantId == tenantId) >= _limits.TenantLimit(tenantId)
            || live.Count(slot => slot.TenantId == tenantId && slot.AgentId == agentId) >= _limits.AgentMaxConcurrentBackups)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var candidateIds = await (from attempt in context.BackupRunAttempts
                                  join run in context.BackupRuns on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                                  where attempt.TenantId == tenantId && attempt.AgentId == agentId && attempt.Status == BackupStatus.Pending && run.Status == BackupStatus.Pending
                                     // A scheduled run's first attempt that missed its grace period never starts; the unclaimed-run sweep ends it.
                                     && (run.ClaimDeadline == null || run.ClaimDeadline >= now || attempt.Number > 1)
                                  orderby attempt.EligibleAt, run.CreatedAt, run.Id
                                  select run.Id).Take(16).ToListAsync(cancellationToken);
        BackupRun? next = null;
        bool? supportsMultiDestination = null;
        bool? supportsQuotaBound = null;
        bool? supportsUploadLimits = null;
        long? maxArtifactBytes = null;
        foreach (var candidateId in candidateIds)
        {
            var candidate = await Runs(context).SingleAsync(run => run.TenantId == tenantId && run.Id == candidateId, cancellationToken);
            if (candidate.Configuration.Engine == "postgres" && !await context.AgentCapabilities.AsNoTracking().AnyAsync(
                capability => capability.TenantId == tenantId && capability.AgentId == agentId &&
                    capability.Identifier == "postgres.backup.full" && capability.Support == BackupPlatform.Domain.Capabilities.CapabilitySupport.Supported, cancellationToken)) continue;
            // A retry or a queued run reaches an agent without passing admission again, so the capability is fenced here too: such a run waits for an agent that can execute it.
            supportsMultiDestination ??= await StorageTargetGate.SupportsMultiDestinationAsync(context, tenantId, agentId, cancellationToken);
            if (supportsMultiDestination != true) continue;
            // The backend paces every upload it relays itself; only an AgentDirect destination depends on the agent. A limit that such an agent cannot follow would be
            // silently ignored, so that run waits for an agent that can pace its uploads.
            if (candidate.Configuration.Destinations.Any(destination => destination.StorageExecutionLocation == BackupStorageExecutionLocation.Agent)
                && await PostgreSqlUploadBandwidthStore.AnyAsync(context, tenantId, agentId, candidate.JobId, cancellationToken))
            {
                supportsUploadLimits ??= await PostgreSqlUploadBandwidthStore.SupportsAsync(context, tenantId, agentId, cancellationToken);
                if (supportsUploadLimits != true) continue;
            }
            var bound = await TenantUsageEngine.ReadRunMaxArtifactBytesAsync(context, candidate, now, cancellationToken);
            if (bound is not null)
            {
                supportsQuotaBound ??= await TenantUsageEngine.SupportsQuotaBoundedUploadAsync(context, tenantId, agentId, cancellationToken);
                if (supportsQuotaBound != true) continue;
            }
            maxArtifactBytes = bound;
            next = candidate;
            break;
        }
        if (next is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        next.Claim(Guid.NewGuid(), now, leaseDuration);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BackupClaim(next, next.CurrentAttempt)
        {
            MaxArtifactBytes = maxArtifactBytes
        };
    }
    public async Task<BackupClaim?> GetAttemptAsync(TenantId tenantId, AgentId agentId, Guid attemptId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var run = await Runs(context).AsNoTracking().SingleOrDefaultAsync(run => run.TenantId == tenantId && run.AgentId == agentId &&
            run.Attempts.Any(attempt => attempt.Id == attemptId && attempt.TenantId == tenantId && attempt.AgentId == agentId), cancellationToken);
        return run is null ? null : new BackupClaim(run, run.Attempts.Single(attempt => attempt.Id == attemptId))
        {
            MaxArtifactBytes = await TenantUsageEngine.ReadRunMaxArtifactBytesAsync(context, run, run.CurrentAttempt.LeasedAt ?? run.CreatedAt, cancellationToken)
        };
    }
    public Task<BackupMutationResult> StartAttemptAsync(BackupLeaseCommand command, CancellationToken cancellationToken) =>
        MutateAsync(command, run => run.Start(command.LeaseToken, command.Now, command.SoftwareVersion!, command.ProtocolVersion!, command.Platform!), cancellationToken);
    public Task<BackupMutationResult> ReportProgressAsync(BackupLeaseCommand command, BackupProgress progress, CancellationToken cancellationToken) =>
        MutateAsync(command, run => run.Progress(command.LeaseToken, command.Now, progress.Stage, progress.Percent), cancellationToken);
    public Task<BackupMutationResult> RecordTransferAsync(BackupLeaseCommand command, BackupTransferIntent transfer, CancellationToken cancellationToken) =>
        MutateAsync(command, run =>
        {
            var destination = run.Configuration.Destinations.SingleOrDefault(d => d.StorageConnectionId == transfer.StorageConnectionId);
            if (destination is not { StorageProvider: "sftp" or "google-drive", StorageExecutionLocation: BackupStorageExecutionLocation.Backend } ||
                !ManagedBackupLayout.TryParseStorageKey(transfer.Key, out var layout) || layout is null || layout.IsPartial ||
                layout.TenantId != command.TenantId.Value || layout.JobId != run.JobId || layout.RunId != run.Id || layout.AttemptId != command.AttemptId)
                throw new ArgumentException("The upload intent does not belong to this attempt.");
            var artifact = new BackupArtifact(Guid.NewGuid(), command.TenantId, command.AttemptId, transfer.SizeBytes, transfer.Checksum,
                transfer.Checksum, "sha256", command.Now, "{\"kind\":\"remote-upload-intent\"}", "{}");
            artifact.AddReplica(new BackupReplica(Guid.NewGuid(), command.TenantId, artifact.Id, destination.StorageProvider, transfer.StorageConnectionId.ToString("D"),
                transfer.Key, transfer.SizeBytes, transfer.Checksum, "sha256", "Unverified", "None", command.Now, null,
                BackupStorageExecutionLocation.Backend, storageConnectionId: transfer.StorageConnectionId));
            run.CurrentAttempt.RecordTransfer(command.LeaseToken, command.Now, artifact);
        }, cancellationToken);
    public Task<BackupMutationResult> CompleteAttemptAsync(BackupLeaseCommand command, ApplicationCompletion completion, CancellationToken cancellationToken)
    {
        // Replicas the agent already transferred are carried across the completion under the same agent lock; each destination is resolved from the run's snapshot when the domain completion is mapped.
        var connections = new Dictionary<string, Guid>(StringComparer.Ordinal);
        IReadOnlyList<BackupArtifactResult> retained = [];
        return MutateAsync(command, run => run.Complete(command.LeaseToken, command.Now, MapCompletion(command, completion, run, connections, retained), Jitter()), cancellationToken,
            async (context, run) =>
            {
                var pending = run.CurrentAttempt.Artifacts.ToArray();
                retained = pending.Select(artifact => new BackupArtifactResult(artifact.SizeBytes, artifact.SourceChecksum, artifact.ArtifactChecksum,
                    artifact.ChecksumAlgorithm, artifact.CreatedAt, artifact.SourceMetadataJson, artifact.AppliedOptionsJson,
                    artifact.Replicas.Select(replica => new BackupReplicaResult(replica.StorageProvider, replica.StorageTarget, replica.Key, replica.SizeBytes,
                        replica.Checksum, replica.ChecksumAlgorithm, replica.VerificationState, replica.VerificationMethod, replica.CreatedAt, replica.VerifiedAt,
                        replica.StorageConnectionId)).ToArray(), artifact.Format, artifact.EncryptionKeyId, artifact.EncryptionKeyVersion, artifact.EncryptionWrappedDekBase64, artifact.EncryptionRecipientsJson)).ToArray();
                if (!run.CurrentAttempt.IsTerminal && pending.Length > 0 && run.CurrentAttempt.HasLease(command.LeaseToken, command.Now))
                {
                    context.BackupReplicas.RemoveRange(pending.SelectMany(artifact => artifact.Replicas));
                    context.BackupArtifacts.RemoveRange(pending);
                    await context.SaveChangesAsync(cancellationToken);
                }
                // A snapshot that predates storage connections names none: its copies belong to the tenant's default for the provider. Publishing what already ran is never refused.
                foreach (var destination in run.Configuration.Destinations.Where(d => d.StorageConnectionId is null))
                {
                    connections[destination.StorageProvider] = await StorageTargetGate.ForPublishAsync(context, command.TenantId, destination.StorageProvider, command.Now, cancellationToken);
                }
            });
    }
    public async Task<BackupLeaseResult?> RenewLeaseAsync(BackupLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        BackupLeaseResult? result = null;
        var mutation = await MutateAsync(command, run =>
        {
            run.Renew(command.LeaseToken, command.Now, leaseDuration);
            result = new BackupLeaseResult(run.CurrentAttempt.LeaseExpiresAt!.Value, run.CancellationRequestedAt.HasValue);
        }, cancellationToken);
        return mutation == BackupMutationResult.Accepted ? result : null;
    }
    public async Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scan = PlatformDbContext.CreateForPlatformOperations(options);
        var stale = await scan.BackupRunAttempts.AsNoTracking().Where(attempt =>
            (attempt.Status == BackupStatus.Leased || attempt.Status == BackupStatus.Running) && attempt.LeaseExpiresAt <= now)
            .Select(attempt => new { attempt.TenantId, attempt.AgentId, attempt.Id }).ToListAsync(cancellationToken);
        var expired = 0;
        foreach (var attempt in stale)
        {
            var changed = false;
            var result = await MutateAsync(new BackupLeaseCommand(attempt.TenantId, attempt.AgentId, attempt.Id, Guid.Empty, now),
                run => changed = run.Expire(now, Jitter()), cancellationToken);
            if (result == BackupMutationResult.Accepted && changed) expired++;
        }
        return expired;
    }
    public async Task<IReadOnlyList<BackupRetryStarted>> StartDueRetriesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var scan = PlatformDbContext.CreateForPlatformOperations(options);
        var due = await scan.BackupRuns.AsNoTracking().Where(run => run.Status == BackupStatus.RetryPending && run.NextAttemptAt <= now)
            .OrderBy(run => run.NextAttemptAt).ThenBy(run => run.Id).Take(Math.Clamp(limit, 1, 500))
            .Select(run => new { run.TenantId, run.AgentId, run.Id }).ToListAsync(cancellationToken);
        var started = new List<BackupRetryStarted>();
        foreach (var candidate in due)
        {
            await using var context = PlatformDbContext.CreateForPlatformOperations(options);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (!await LockAgentAsync(context, candidate.TenantId, candidate.AgentId, cancellationToken)) continue;
            // Re-read under the lock: a concurrent worker, a manual retry or a cancellation may already have moved the run on.
            var run = await Runs(context).SingleOrDefaultAsync(run => run.TenantId == candidate.TenantId && run.Id == candidate.Id && run.AgentId == candidate.AgentId, cancellationToken);
            if (run is null || !run.StartScheduledRetry(Guid.NewGuid(), now)) continue;
            context.AuditEvents.Add(AuditEvent.Record(
                now, UserId.System, AuditActorKind.System, AuditActions.BackupRunRetryRequested, run.TenantId,
                "backup_run", run.Id.ToString("D"),
                new Dictionary<string, object?> { ["jobId"] = run.JobId.ToString("D"), ["runId"] = run.Id.ToString("D"), ["attemptId"] = run.CurrentAttempt.Id.ToString("D"), ["retry"] = "Automatic", ["result"] = "started" }));
            try { await context.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException) { continue; } // lost a race the lock did not cover (another instance); the winner's attempt stands
            await transaction.CommitAsync(cancellationToken);
            started.Add(new BackupRetryStarted(run.Id, run.AgentId));
        }
        return started;
    }
    private async Task<BackupMutationResult> MutateAsync(BackupLeaseCommand command, Action<BackupRun> mutate, CancellationToken cancellationToken, Func<PlatformDbContext, BackupRun, Task>? prepare = null)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, command.TenantId, command.AgentId, cancellationToken)) return BackupMutationResult.NotFound;
        var run = await Runs(context).SingleOrDefaultAsync(run => run.TenantId == command.TenantId && run.AgentId == command.AgentId &&
            run.Attempts.Any(attempt => attempt.Id == command.AttemptId && attempt.TenantId == command.TenantId && attempt.AgentId == command.AgentId), cancellationToken);
        if (run is null) return BackupMutationResult.NotFound;
        // Only the run's current attempt can be addressed: once a retry exists, every callback of an older attempt is stale.
        if (run.CurrentAttempt.Id != command.AttemptId) return BackupMutationResult.Conflict;
        if (prepare is not null) await prepare(context, run);
        var wasTerminal = run.IsTerminal;
        var attemptWasTerminal = run.CurrentAttempt.IsTerminal;
        try { mutate(run); }
        catch (InvalidOperationException) { return BackupMutationResult.Conflict; }
        catch (ArgumentException) { return BackupMutationResult.Conflict; }
        BackupAudit.RecordCompletion(context, run, wasTerminal, attemptWasTerminal, command.Now);
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return BackupMutationResult.Conflict; }
        await transaction.CommitAsync(cancellationToken);
        return BackupMutationResult.Accepted;
    }
    internal static async Task<Guid?> ActiveRunIdAsync(PlatformDbContext context, TenantId tenantId, Guid jobId, Guid? exceptRunId, CancellationToken cancellationToken)
    {
        var id = await context.BackupRuns.AsNoTracking().Where(run => run.TenantId == tenantId && run.JobId == jobId && ActiveStatuses.Contains(run.Status) && run.Id != exceptRunId)
            .OrderBy(run => run.CreatedAt).Select(run => (Guid?)run.Id).FirstOrDefaultAsync(cancellationToken);
        return id;
    }
    internal static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    internal static async Task<bool> LockAgentAsync(PlatformDbContext context, TenantId tenantId, AgentId agentId, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT \"Id\" FROM agents WHERE \"Id\" = {agentId.Value} AND \"TenantId\" = {tenantId.Value} FOR UPDATE", cancellationToken);
        return await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == agentId, cancellationToken);
    }
    internal static IQueryable<BackupRun> Runs(PlatformDbContext context) => context.BackupRuns
        .Include(run => run.Attempts).ThenInclude(attempt => attempt.Artifacts).ThenInclude(artifact => artifact.Replicas)
        .Include(run => run.Attempts).ThenInclude(attempt => attempt.DestinationOutcomes)
        .Include(run => run.Facts).AsSplitQuery();
    private static DomainCompletion MapCompletion(BackupLeaseCommand command, ApplicationCompletion result, BackupRun run,
        IReadOnlyDictionary<string, Guid> connections, IReadOnlyList<BackupArtifactResult> retained)
    {
        var status = result.Outcome switch
        {
            "Succeeded" => BackupStatus.Succeeded,
            "Failed" => BackupStatus.Failed,
            "Cancelled" => BackupStatus.Cancelled,
            _ => throw new ArgumentException("Invalid terminal outcome.")
        };
        // Every destination is looked up in the RUN'S SNAPSHOT: what the agent names that the snapshot does not contain is refused, never created.
        var snapshot = run.Configuration.Destinations;
        (BackupDestination Destination, Guid Connection) Resolve(string provider, Guid? named)
        {
            var candidates = snapshot.Where(d => d.StorageProvider == provider && (named is null || d.StorageConnectionId == named)).ToArray();
            if (candidates.Length != 1) throw new ArgumentException("The destination is not part of the run snapshot.");
            var connection = candidates[0].StorageConnectionId ?? connections.GetValueOrDefault(provider);
            return connection == Guid.Empty ? throw new ArgumentException("The destination has no storage connection.") : (candidates[0], connection);
        }
        // A replica carries what the agent reported about THAT copy; the domain compares it with the artifact, so a copy that disagrees is refused rather than silently corrected.
        BackupReplica NewReplica(BackupDestination destination, Guid connection, Guid artifactId, string target, string key, long size, string checksum, string algorithm, string state, string method, DateTimeOffset createdAt, DateTimeOffset? verifiedAt) =>
            new(Guid.NewGuid(), command.TenantId, artifactId, destination.StorageProvider, target, key, size, checksum, algorithm, state, method,
                createdAt, verifiedAt, destination.StorageExecutionLocation, destination.StorageExecutionLocation == BackupStorageExecutionLocation.Agent ? run.AgentId.Value : null, connection);
        BackupArtifact? artifact = null;
        var replicaIds = new Dictionary<(string Provider, Guid Connection), Guid>();
        if (result.Artifact is { } input)
        {
            artifact = new BackupArtifact(Guid.NewGuid(), command.TenantId, command.AttemptId, input.SizeBytes,
                input.SourceChecksum, input.ArtifactChecksum, input.ChecksumAlgorithm, input.CreatedAt, input.SourceMetadataJson, input.AppliedOptionsJson,
                input.Format, input.EncryptionKeyId, input.EncryptionKeyVersion, input.EncryptionWrappedDekBase64, input.EncryptionRecipientsJson);
            foreach (var replica in input.Replicas)
            {
                var (destination, connection) = Resolve(replica.StorageProvider, replica.StorageConnectionId);
                var copy = NewReplica(destination, connection, artifact.Id, replica.StorageTarget, replica.Key, replica.SizeBytes, replica.Checksum, replica.ChecksumAlgorithm,
                    replica.VerificationState, replica.VerificationMethod, replica.CreatedAt, replica.VerifiedAt);
                artifact.AddReplica(copy);
                replicaIds[(destination.StorageProvider, connection)] = copy.Id;
            }
        }
        foreach (var pending in retained)
        {
            artifact ??= new BackupArtifact(Guid.NewGuid(), command.TenantId, command.AttemptId, pending.SizeBytes, pending.SourceChecksum,
                pending.ArtifactChecksum, pending.ChecksumAlgorithm, pending.CreatedAt, pending.SourceMetadataJson, pending.AppliedOptionsJson,
                pending.Format, pending.EncryptionKeyId, pending.EncryptionKeyVersion, pending.EncryptionWrappedDekBase64, pending.EncryptionRecipientsJson);
            foreach (var replica in pending.Replicas)
            {
                if (artifact.Replicas.Any(r => r.StorageConnectionId == replica.StorageConnectionId) ||
                    result.Destinations?.Any(d => d.StorageConnectionId == replica.StorageConnectionId && d.ObjectMayRemain) == true) continue;
                var (destination, connection) = Resolve(replica.StorageProvider, replica.StorageConnectionId);
                artifact.AddReplica(NewReplica(destination, connection, artifact.Id, replica.StorageTarget, replica.Key, replica.SizeBytes,
                    replica.Checksum, replica.ChecksumAlgorithm, "Failed", "None", replica.CreatedAt, null));
            }
        }
        var outcomes = new List<BackupDestinationOutcome>();
        foreach (var report in result.Destinations ?? [])
        {
            var (destination, connection) = Resolve(report.StorageProvider, report.StorageConnectionId);
            Guid? replicaId = report.State == BackupDestinationStates.Verified
                ? replicaIds.TryGetValue((destination.StorageProvider, connection), out var id) ? id : throw new ArgumentException("A verified destination has no replica.")
                : null;
            if (report.ObjectMayRemain)
            {
                if (artifact is null || !ManagedBackupLayout.TryParseStorageKey(report.Key, out var layout) || layout is null ||
                    layout.TenantId != command.TenantId.Value || layout.JobId != run.JobId || layout.RunId != run.Id || layout.AttemptId != command.AttemptId)
                    throw new ArgumentException("An orphan requires artifact metadata and an object key owned by this attempt.");
                // The object exists physically but never verified: it is recorded as a Failed replica so its bytes stay accounted for and retention can remove it.
                var target = destination.StorageProvider is "sftp" or "google-drive" ? connection.ToString("D") : destination.StorageProvider;
                var orphan = NewReplica(destination, connection, artifact.Id, target, report.Key!, artifact.SizeBytes, artifact.ArtifactChecksum, artifact.ChecksumAlgorithm,
                    "Failed", "None", report.CompletedAt, null);
                artifact.AddReplica(orphan);
            }
            outcomes.Add(new BackupDestinationOutcome(Guid.NewGuid(), command.TenantId, run.Id, command.AttemptId, destination.StorageProvider, connection, destination.IsPrimary,
                destination.StorageExecutionLocation, report.State, report.Upload, report.Verification, report.FailureCode, report.Key, report.ObjectMayRemain, report.StartedAt, report.CompletedAt, replicaId));
        }
        return new DomainCompletion(status, result.ReasonCode, artifact, outcomes);
    }
}
