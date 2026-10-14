using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>
/// Scheduling decisions in PostgreSQL. Each evaluation runs under the same tenant-qualified agent row lock that serialises manual
/// starts, claims, completions and retries of that agent's jobs, so a scheduled run cannot race any other way a run of the job is created or
/// finished. Beneath the lock sit two unique keys that stay authoritative if the lock is ever bypassed (another node, another code path):
/// one occurrence row per (job, instant) and one run per (job, instant). Neither the scheduler library nor process memory holds any
/// scheduling state.
/// </summary>
public sealed class PostgreSqlBackupScheduleStore(DbContextOptions<PlatformDbContext> options, IBackupAdmissionPolicy admission, AgentConnectivityOptions? connectivity = null) : IBackupScheduleStore
{
    private readonly AgentConnectivityOptions _connectivity = connectivity ?? new AgentConnectivityOptions();

    public async Task<BackupScheduleEvaluation> EvaluateAsync(TenantId tenantId, Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var evaluation = await EvaluateOnceAsync(tenantId, jobId, now, cancellationToken);
                if (evaluation is not null) return evaluation;
            }
            catch (DbUpdateException exception) when (PostgreSqlBackupRunStore.IsUniqueViolation(exception) && attempt < 2)
            {
                // Another writer decided the same instant first. Re-evaluate against what it committed: there is nothing left to do for it.
            }
        }
    }

    /// <summary>Null when the job moved to another agent between reading its owner and locking it; the caller evaluates again.</summary>
    private async Task<BackupScheduleEvaluation?> EvaluateOnceAsync(TenantId tenantId, Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.Id == jobId).Select(job => job.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return BackupScheduleEvaluation.Missing;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, tenantId, owner, cancellationToken)) return BackupScheduleEvaluation.Missing;
        // Everything below is read under the lock: a schedule edit, a disable or a manual start that committed first is already visible.
        var job = await context.BackupJobs.SingleOrDefaultAsync(job => job.TenantId == tenantId && job.Id == jobId, cancellationToken);
        if (job is null) return BackupScheduleEvaluation.Missing;
        if (job.AgentId != owner) return null;
        if (job.Schedule is null) return new BackupScheduleEvaluation([], null, null, false);

        var last = await context.BackupScheduleOccurrences.Where(occurrence => occurrence.TenantId == tenantId && occurrence.JobId == jobId)
            .MaxAsync(occurrence => (DateTimeOffset?)occurrence.ScheduledAt, cancellationToken);
        var active = job.OverlapPolicy == BackupOverlapPolicy.Skip ? await PostgreSqlBackupRunStore.ActiveRunIdAsync(context, tenantId, jobId, null, cancellationToken) : null;
        var plan = BackupScheduleEvaluator.Plan(new BackupScheduleFacts(job.Schedule, job.Enabled, job.OverlapPolicy, last,
            await AgentAvailableAsync(context, tenantId, owner, now, cancellationToken), active, now));
        if (plan.Resolutions.Count == 0 && plan.CreateRunFor is null) return new BackupScheduleEvaluation([], null, null, plan.Deferred is not null);

        var recorded = new List<BackupScheduleOccurrence>();
        foreach (var resolution in plan.Resolutions)
        {
            recorded.Add(new BackupScheduleOccurrence(Guid.NewGuid(), tenantId, jobId, resolution.ScheduledAt, resolution.Outcome, resolution.Reason, resolution.RunId, now));
        }
        BackupRun? run = null;
        if (plan.CreateRunFor is { } instant)
        {
            // Same gate as a manual start. A refusal is a decided occurrence like any other: recorded with its reason, so it is never silently lost.
            var runId = Guid.NewGuid();
            var target = await StorageTargetGate.ForNewRunAsync(context, tenantId, owner, job.Configuration, now, cancellationToken);
            var decision = target.Allowed
                ? await BackupAdmissionGate.AdmitAsync(admission, context, new BackupAdmissionRequest(tenantId, jobId, runId, BackupTriggerTypes.Scheduled, now) { Configuration = target.Configuration }, cancellationToken)
                : BackupAdmissionDecision.Denied(target.DenialCode!);
            if (decision.Allowed)
            {
                run = BackupRun.ForScheduledOccurrence(runId, job, instant, now, target.Configuration);
                run.QueueFirstAttempt(Guid.NewGuid(), now);
                context.BackupRuns.Add(run);
                context.AuditEvents.Add(AuditEvent.Record(
                    now, UserId.System, AuditActorKind.System, AuditActions.BackupRunTriggered, tenantId,
                    "backup_run", run.Id.ToString("D"),
                    new Dictionary<string, object?> { ["jobId"] = jobId.ToString("D"), ["runId"] = run.Id.ToString("D"), ["trigger"] = "Scheduled", ["result"] = "started", ["scheduledFor"] = instant, ["attemptId"] = run.CurrentAttempt.Id.ToString("D") }));
                recorded.Add(new BackupScheduleOccurrence(Guid.NewGuid(), tenantId, jobId, instant, BackupOccurrenceOutcome.RunCreated, BackupOccurrenceReasons.RunCreated, run.Id, now));
            }
            else
            {
                recorded.Add(new BackupScheduleOccurrence(Guid.NewGuid(), tenantId, jobId, instant, BackupOccurrenceOutcome.AdmissionDenied, decision.DenialCode!, null, now));
            }
        }
        context.BackupScheduleOccurrences.AddRange(recorded);
        foreach (var occurrence in recorded)
            context.AuditEvents.Add(AuditEvent.Record(
                now, UserId.System, AuditActorKind.System, AuditActions.BackupScheduleOccurrenceDecided, tenantId,
                "backup_schedule_occurrence", occurrence.Id.ToString("D"),
                new Dictionary<string, object?>
                {
                    ["jobId"] = jobId.ToString("D"), ["runId"] = occurrence.RunId?.ToString("D"),
                    ["scheduledFor"] = occurrence.ScheduledAt, ["result"] = occurrence.Outcome.ToString(),
                    ["reason"] = occurrence.Outcome == BackupOccurrenceOutcome.AdmissionDenied
                        ? BackupAdmissionDenialCodes.IsKnown(occurrence.Reason) ? occurrence.Reason : "unknown_denial"
                        : occurrence.Outcome.ToString()
                }));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // Admitted but not created (a lost race on the occurrence key, a failed write): give the reservation back.
            if (run is not null) await BackupAdmissionGate.ReleaseAfterFailureAsync(admission, tenantId, run.Id);
            throw;
        }
        return new BackupScheduleEvaluation(recorded, run?.Id, run?.AgentId, plan.Deferred is not null);
    }

    public async Task<IReadOnlyList<ScheduledJobReference>> FindDueJobsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var candidates = await context.BackupJobs.AsNoTracking().Where(job => job.Enabled && job.Schedule != null && job.Schedule.Enabled)
            .Select(job => new
            {
                job.TenantId,
                job.Id,
                job.Schedule,
                Last = context.BackupScheduleOccurrences.Where(occurrence => occurrence.TenantId == job.TenantId && occurrence.JobId == job.Id)
                    .Max(occurrence => (DateTimeOffset?)occurrence.ScheduledAt)
            }).ToListAsync(cancellationToken);
        return candidates.Where(candidate => BackupScheduleEvaluator.HasDueOccurrence(candidate.Schedule!, candidate.Last, now))
            .OrderBy(candidate => candidate.Id).Take(Math.Clamp(limit, 1, 1000))
            .Select(candidate => new ScheduledJobReference(candidate.TenantId, candidate.Id)).ToArray();
    }

    public async Task<IReadOnlyList<BackupJob>> ListScheduledJobsAsync(CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.BackupJobs.AsNoTracking().Where(job => job.Enabled && job.Schedule != null && job.Schedule.Enabled).ToListAsync(cancellationToken);
    }

    public async Task<int> ExpireUnclaimedRunsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var scan = PlatformDbContext.CreateForPlatformOperations(options);
        var stale = await scan.BackupRuns.AsNoTracking().Where(run => run.Status == BackupStatus.Pending && run.CurrentAttemptNumber == 1 && run.ClaimDeadline != null && run.ClaimDeadline < now)
            .OrderBy(run => run.ClaimDeadline).ThenBy(run => run.Id).Take(Math.Clamp(limit, 1, 1000))
            .Select(run => new { run.TenantId, run.AgentId, run.Id }).ToListAsync(cancellationToken);
        var expired = 0;
        foreach (var candidate in stale)
        {
            await using var context = PlatformDbContext.CreateForPlatformOperations(options);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, candidate.TenantId, candidate.AgentId, cancellationToken)) continue;
            // Re-read under the lock: an agent that claimed the run, or a cancellation, may have got there first.
            var run = await PostgreSqlBackupRunStore.Runs(context).SingleOrDefaultAsync(run => run.TenantId == candidate.TenantId && run.Id == candidate.Id && run.AgentId == candidate.AgentId, cancellationToken);
            if (run is null) continue;
            var wasTerminal = run.IsTerminal;
            var attemptWasTerminal = run.CurrentAttempt.IsTerminal;
            if (!run.ExpireUnclaimed(now)) continue;
            BackupAudit.RecordCompletion(context, run, wasTerminal, attemptWasTerminal, now);
            try { await context.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { continue; }
            await transaction.CommitAsync(cancellationToken);
            expired++;
        }
        return expired;
    }

    public async Task<IReadOnlyList<BackupScheduleOccurrence>> ListOccurrencesAsync(TenantId tenantId, Guid jobId, int limit, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.BackupScheduleOccurrences.AsNoTracking().Where(occurrence => occurrence.TenantId == tenantId && occurrence.JobId == jobId)
            .OrderByDescending(occurrence => occurrence.ScheduledAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
    }

    public async Task<BackupJob?> ModifyJobAsync(TenantId tenantId, Guid jobId, Action<BackupJob> change, CancellationToken cancellationToken, UserId? actor = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        while (true)
        {
            await using var context = PlatformDbContext.CreateForPlatformOperations(options);
            var owner = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.Id == jobId).Select(job => job.AgentId).SingleOrDefaultAsync(cancellationToken);
            if (owner is null) return null;
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, tenantId, owner, cancellationToken)) return null;
            var job = await context.BackupJobs.SingleOrDefaultAsync(job => job.TenantId == tenantId && job.Id == jobId, cancellationToken);
            if (job is null) return null;
            if (job.AgentId != owner) continue;
            var before = BackupAudit.Capture(job);
            change(job);
            BackupAudit.RecordJobChanges(context, job, before, actor, at);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return job;
        }
    }

    /// <summary>Active and recently seen. Liveness is the persisted last sign of life against the server clock, never a connection that may have dropped a moment ago.</summary>
    private async Task<bool> AgentAvailableAsync(PlatformDbContext context, TenantId tenantId, AgentId agentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == agentId && agent.Status == AgentStatus.Active, cancellationToken)) return false;
        var lastSeen = await context.AgentConnectivity.AsNoTracking().Where(record => record.TenantId == tenantId && record.AgentId == agentId)
            .Select(record => (DateTimeOffset?)record.LastSeenAt).SingleOrDefaultAsync(cancellationToken);
        return AgentConnectivityEvaluator.Evaluate(lastSeen, now, _connectivity.OfflineTimeout) == AgentConnectivityState.Online;
    }
}
