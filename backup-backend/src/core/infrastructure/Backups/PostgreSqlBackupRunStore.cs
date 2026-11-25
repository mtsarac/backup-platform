using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Flags;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.Infrastructure.Notifications;
using BackupPlatform.Notifications;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Storage;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ApplicationCompletion = BackupPlatform.Application.Backups.BackupCompletion;
using DomainCompletion = BackupPlatform.Domain.Backups.BackupCompletion;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>
/// Tenant-facing run and job operations of the request's tenant, on the filtered context. The tenant's own writes and their audit rows commit together;
/// agent and worker flows (claim, lease, expiry, retries) are <see cref="PostgreSqlBackupRunPlatformStore"/>.
/// </summary>
public sealed class PostgreSqlBackupRunStore(DbContextOptions<PlatformDbContext> options, ITenantContext tenant, IBackupAdmissionPolicy admission, FeatureFlags? flags = null) : IBackupRunStore
{
    private PlatformDbContext Open(TenantId tenantId) => PlatformDbContext.ForTenant(options, tenant, tenantId);

    public async Task<BackupJob?> CreateJobAsync(BackupJob job, CancellationToken cancellationToken, UserId? actor = null)
    {
        // A key already used by this tenant answers with its job before anything else is looked at (the agent may have been revoked since).
        if (job.IdempotencyKey is not null && await FindJobByIdempotencyKeyAsync(job.TenantId, job.IdempotencyKey, cancellationToken) is { } replayed) return replayed;
        await using var context = Open(job.TenantId);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await BackupRunQueries.LockAgentAsync(context, job.TenantId, job.AgentId, cancellationToken)) return null;
        if (!await context.Agents.AnyAsync(agent => agent.Id == job.AgentId && agent.TenantId == job.TenantId && agent.Status == AgentStatus.Active, cancellationToken)) return null;
        await StorageTargetGate.EnsureJobTargetAsync(context, job.TenantId, job.Configuration, cancellationToken);
        context.BackupJobs.Add(job);
        BackupAudit.AddJobEvent(context, job, actor, job.CreatedAt, AuditActions.BackupJobCreated);
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (job.IdempotencyKey is not null && BackupRunQueries.IsUniqueViolation(exception))
        {
            // Another request (any node) won the unconditional (tenant, key) index: this one creates and audits nothing and answers with the winner.
            await transaction.RollbackAsync(CancellationToken.None);
            return await FindJobByIdempotencyKeyAsync(job.TenantId, job.IdempotencyKey, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return job;
    }
    public async Task<BackupJob?> FindJobByIdempotencyKeyAsync(TenantId tenantId, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var context = Open(tenantId);
        return await context.BackupJobs.AsNoTracking().SingleOrDefaultAsync(job => job.TenantId == tenantId && job.IdempotencyKey == idempotencyKey, cancellationToken);
    }
    public async Task<BackupJob?> GetJobAsync(TenantId tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var context = Open(tenantId);
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
        await using var context = Open(tenantId);
        var owner = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.Id == jobId)
            .Select(job => job.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return (null, false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var firstAgent = owner.Value.CompareTo(update.AgentId.Value) <= 0 ? owner : update.AgentId;
        var secondAgent = firstAgent == owner ? update.AgentId : owner;
        if (!await BackupRunQueries.LockAgentAsync(context, tenantId, firstAgent, cancellationToken)) return (null, false);
        if (secondAgent != firstAgent && !await BackupRunQueries.LockAgentAsync(context, tenantId, secondAgent, cancellationToken)) return (null, false);
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
        await using var context = Open(tenantId);
        var owner = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.Id == jobId).Select(job => job.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return new BackupStartResult(BackupStartOutcome.NotFound, null);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await BackupRunQueries.LockAgentAsync(context, tenantId, owner, cancellationToken)) return new BackupStartResult(BackupStartOutcome.NotFound, null);
        // The key is checked first: replaying a request must return its original run even when the job has since become busy.
        var existing = await BackupRunQueries.Runs(context).SingleOrDefaultAsync(run => run.TenantId == tenantId && run.JobId == jobId && run.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null) return new BackupStartResult(BackupStartOutcome.Replayed, existing);
        var job = await context.BackupJobs.SingleOrDefaultAsync(job => job.TenantId == tenantId && job.Id == jobId && job.Enabled, cancellationToken);
        if (job is not null && job.AgentId != owner) return null;
        if (job is null || !await context.Agents.AnyAsync(agent => agent.Id == owner && agent.TenantId == tenantId && agent.Status == AgentStatus.Active, cancellationToken))
            return new BackupStartResult(BackupStartOutcome.NotFound, null);
        if (job.OverlapPolicy == BackupOverlapPolicy.Skip && await BackupRunQueries.ActiveRunIdAsync(context, tenantId, jobId, null, cancellationToken) is { } active)
            return new BackupStartResult(BackupStartOutcome.SkippedOverlap, null, active);
        // The destination of a NEW run: refused while its storage connection is disabled (or missing), resolved and snapshotted otherwise. No reservation exists yet to give back.
        var target = await StorageTargetGate.ForNewRunAsync(context, tenantId, owner, job.Configuration, now, cancellationToken, flags);
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
            context.RecordAudit(AuditEvent.Record(
                now, actor ?? UserId.System, BackupAudit.ActorKind(actor), AuditActions.BackupRunTriggered, tenantId,
                "backup_run", run.Id.ToString("D"),
                new Dictionary<string, object?> { ["jobId"] = job.Id.ToString("D"), ["runId"] = run.Id.ToString("D"), ["trigger"] = "Manual", ["result"] = "started", ["attemptId"] = run.CurrentAttempt.Id.ToString("D") }));
            try { await context.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException exception) when (BackupRunQueries.IsUniqueViolation(exception))
            {
                // The unique index on active Skip runs is the backstop behind the agent lock.
                await BackupAdmissionGate.ReleaseAfterFailureAsync(admission, tenantId, runId);
                return new BackupStartResult(BackupStartOutcome.SkippedOverlap, null, await BackupRunQueries.ActiveRunIdAsync(context, tenantId, jobId, null, cancellationToken));
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
        await using var context = Open(tenantId);
        return await BackupRunQueries.Runs(context).AsNoTracking().Where(run => run.TenantId == tenantId && (jobId == null || run.JobId == jobId))
            .OrderByDescending(run => run.CreatedAt).ThenBy(run => run.Id).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
    }
    public async Task<BackupRun?> GetRunAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken)
    {
        await using var context = Open(tenantId);
        return await BackupRunQueries.Runs(context).AsNoTracking().SingleOrDefaultAsync(run => run.TenantId == tenantId && run.Id == runId, cancellationToken);
    }
    public async Task<BackupRun?> RequestCancellationAsync(TenantId tenantId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null)
    {
        await using var context = Open(tenantId);
        var owner = await context.BackupRuns.AsNoTracking().Where(run => run.TenantId == tenantId && run.Id == runId).Select(run => run.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return null;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await BackupRunQueries.LockAgentAsync(context, tenantId, owner, cancellationToken)) return null;
        // Loaded under the lock, so a failure or retry that committed first is already visible and cancellation wins from here on.
        var run = await BackupRunQueries.Runs(context).SingleAsync(run => run.TenantId == tenantId && run.Id == runId && run.AgentId == owner, cancellationToken);
        var wasTerminal = run.IsTerminal;
        var attemptWasTerminal = run.CurrentAttempt.IsTerminal;
        var requestedAt = run.CancellationRequestedAt;
        run.RequestCancellation(now);
        if (requestedAt != run.CancellationRequestedAt)
            context.RecordAudit(AuditEvent.Record(
                now, actor ?? UserId.System, BackupAudit.ActorKind(actor), AuditActions.BackupRunCancelRequested, tenantId,
                "backup_run", runId.ToString("D"),
                new Dictionary<string, object?> { ["jobId"] = run.JobId.ToString("D"), ["runId"] = run.Id.ToString("D"), ["attemptId"] = run.CurrentAttempt.Id.ToString("D"), ["result"] = "requested" }));
        BackupAudit.RecordCompletion(context, run, wasTerminal, attemptWasTerminal, now);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return run;
    }
    public async Task<BackupMutationResult> SetPauseAsync(TenantId tenantId, Guid runId, bool resume, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null)
    {
        await using var context = Open(tenantId);
        var owner = await context.BackupRuns.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == runId).Select(x => x.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return BackupMutationResult.NotFound;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await BackupRunQueries.LockAgentAsync(context, tenantId, owner, cancellationToken)) return BackupMutationResult.NotFound;
        var run = await BackupRunQueries.Runs(context).SingleAsync(x => x.TenantId == tenantId && x.Id == runId, cancellationToken);
        try
        {
            if (resume) run.Resume(now); else run.RequestPause();
        }
        catch (InvalidOperationException) { return BackupMutationResult.Conflict; }
        context.RecordAudit(AuditEvent.Record(
            now, actor ?? UserId.System, BackupAudit.ActorKind(actor), resume ? AuditActions.BackupRunResumed : AuditActions.BackupRunPauseRequested, tenantId,
            "backup_run", runId.ToString("D"),
            new Dictionary<string, object?> { ["jobId"] = run.JobId.ToString("D"), ["runId"] = run.Id.ToString("D"), ["attemptId"] = run.CurrentAttempt.Id.ToString("D"), ["result"] = resume ? "resumed" : "requested" }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BackupMutationResult.Accepted;
    }
    public async Task<BackupRetryResult> RetryRunAsync(TenantId tenantId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null)
    {
        await using var context = Open(tenantId);
        var owner = await context.BackupRuns.AsNoTracking().Where(run => run.TenantId == tenantId && run.Id == runId).Select(run => run.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return new BackupRetryResult(BackupRetryOutcome.NotFound, null);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await BackupRunQueries.LockAgentAsync(context, tenantId, owner, cancellationToken)) return new BackupRetryResult(BackupRetryOutcome.NotFound, null);
        var run = await BackupRunQueries.Runs(context).SingleOrDefaultAsync(run => run.TenantId == tenantId && run.Id == runId && run.AgentId == owner, cancellationToken);
        if (run is null) return new BackupRetryResult(BackupRetryOutcome.NotFound, null);
        if (!await BackupRunQueries.LockUnarchivedJobAsync(context, tenantId, run.JobId, cancellationToken))
            return new BackupRetryResult(BackupRetryOutcome.JobArchived, run);
        var reopens = run.IsTerminal && run.OverlapPolicy == BackupOverlapPolicy.Skip
            && await BackupRunQueries.ActiveRunIdAsync(context, tenantId, run.JobId, run.Id, cancellationToken) is not null;
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
        context.RecordAudit(AuditEvent.Record(
            now, actor ?? UserId.System, BackupAudit.ActorKind(actor), AuditActions.BackupRunRetryRequested, tenantId,
            "backup_run", runId.ToString("D"),
            new Dictionary<string, object?> { ["jobId"] = run.JobId.ToString("D"), ["runId"] = run.Id.ToString("D"), ["attemptId"] = run.CurrentAttempt.Id.ToString("D"), ["retry"] = "Manual", ["result"] = "started" }));
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (BackupRunQueries.IsUniqueViolation(exception))
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
}
