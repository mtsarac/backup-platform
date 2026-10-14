using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Application.Backups;

/// <summary>Schedule settings as they cross the API. Omitted members take the platform default; <see cref="Enabled"/> defaults to true.</summary>
public sealed record BackupScheduleRequest(bool? Enabled = null, string? CronExpression = null, string? TimeZone = null, double? MissedRunGraceSeconds = null, string? OverlapPolicy = null);
public sealed record BackupScheduleView(bool Enabled, string CronExpression, string TimeZone, double MissedRunGraceSeconds, string OverlapPolicy, DateTimeOffset EffectiveFrom, DateTimeOffset? NextOccurrenceAt);
public sealed record BackupOccurrenceView(Guid Id, DateTimeOffset ScheduledAt, string Outcome, string Reason, Guid? RunId, DateTimeOffset DecidedAt, double DelaySeconds);

/// <summary>The outcome of one sweep. Errors are collected per item so one bad job never stops the rest; the host logs them.</summary>
public sealed record BackupSweepReport(int JobsEvaluated, int RunsCreated, int RunsExpired, BackupScheduleReconciliation? Reconciliation, IReadOnlyList<Exception> Errors);

/// <summary>
/// Scheduled backups, as a use-case on top of the existing run orchestration. The database decides what is due and what happens to it
/// (<see cref="IBackupScheduleStore"/>); the scheduler library only wakes this service up and is kept in line with the jobs through
/// <see cref="IBackupScheduleCoordinator"/>. A scheduled run enters the same Pending → claim → lease pipeline as a manual one, so it obeys
/// the same concurrency limits, retry policy and overlap rules.
/// </summary>
public sealed class BackupScheduleService(
    IBackupScheduleStore schedules, IBackupRunStore runs, IBackupScheduleCoordinator coordinator, IBackupWorkSignal signal,
    TimeProvider time, BackupSchedulingOptions options, ILogger<BackupScheduleService> logger)
{
    /// <summary>
    /// Evaluates one job now (a wake-up, or the sweep), then re-aims its wake-up at the next occurrence. The evaluation result stands even
    /// if re-aiming fails: the next reconciliation repairs the wake-up.
    /// </summary>
    public async Task<BackupScheduleEvaluation> EvaluateAsync(TenantId tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var evaluation = await schedules.EvaluateAsync(tenantId, jobId, time.GetUtcNow(), cancellationToken);
        if (evaluation.AgentToNotify is { } agent) await NotifyAsync(agent);
        await TrySynchronizeAsync(tenantId, jobId, cancellationToken);
        return evaluation;
    }

    /// <summary>Re-reads the job from the database and makes its wake-up match. Throws if the scheduler store is unreachable.</summary>
    public async Task SynchronizeAsync(TenantId tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await runs.GetJobAsync(tenantId, jobId, cancellationToken);
        await coordinator.SynchronizeAsync(jobId, job is null ? null : TargetFor(job, time.GetUtcNow()), cancellationToken);
    }

    /// <summary>Like <see cref="SynchronizeAsync"/> but never throws: used after a database commit that must not be undone by a scheduler failure.</summary>
    public async Task<bool> TrySynchronizeAsync(TenantId tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await SynchronizeAsync(tenantId, jobId, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not update the scheduler wake-up for backup job {JobId}; reconciliation will repair it.", jobId);
            return false;
        }
    }

    /// <summary>
    /// The safety net that makes the database authoritative: expires unclaimed scheduled runs, evaluates every job with a due occurrence
    /// (including ones whose wake-up was lost, and ones waiting for an offline agent), then reconciles the scheduler's wake-ups.
    /// Safe from any number of nodes at once.
    /// </summary>
    public async Task<BackupSweepReport> SweepAsync(CancellationToken cancellationToken)
    {
        var errors = new List<Exception>();
        var evaluated = 0;
        var created = 0;
        var expired = 0;
        try { expired = await schedules.ExpireUnclaimedRunsAsync(time.GetUtcNow(), options.SweepBatchSize, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { errors.Add(exception); }
        try
        {
            foreach (var due in await schedules.FindDueJobsAsync(time.GetUtcNow(), options.SweepBatchSize, cancellationToken))
            {
                try
                {
                    var evaluation = await EvaluateAsync(due.TenantId, due.JobId, cancellationToken);
                    evaluated++;
                    if (evaluation.CreatedRunId is not null) created++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException) { errors.Add(exception); }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { errors.Add(exception); }
        BackupScheduleReconciliation? reconciliation = null;
        try { reconciliation = await ReconcileAsync(cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { errors.Add(exception); }
        return new BackupSweepReport(evaluated, created, expired, reconciliation, errors);
    }

    /// <summary>Compares the scheduler's wake-ups with the jobs in the database and repairs the difference. Idempotent: a matching store is not touched.</summary>
    public async Task<BackupScheduleReconciliation> ReconcileAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var desired = (await schedules.ListScheduledJobsAsync(cancellationToken)).Select(job => TargetFor(job, now)).OfType<BackupScheduleTarget>().ToArray();
        return await coordinator.ReconcileAsync(desired, cancellationToken);
    }

    /// <summary>Sets, replaces (a request) or removes (null) the job's schedule. Null when the job does not exist.</summary>
    public async Task<BackupJobView?> ChangeScheduleAsync(TenantId tenantId, Guid jobId, BackupScheduleRequest? request, CancellationToken cancellationToken, UserId? actor = null)
    {
        var now = time.GetUtcNow();
        var overlap = request?.OverlapPolicy is null ? (BackupOverlapPolicy?)null : BackupOrchestrator.ToDomain(request.OverlapPolicy);
        var job = await schedules.ModifyJobAsync(tenantId, jobId, target =>
            target.ChangeSchedule(request is null ? null : ToDomain(request, target.Schedule, now, options), overlap), cancellationToken, actor, now);
        if (job is null) return null;
        await TrySynchronizeAsync(tenantId, jobId, cancellationToken);
        return BackupOrchestrator.ToView(job, now);
    }

    /// <summary>Enables or disables the whole job (manual and scheduled starts alike). Null when the job does not exist.</summary>
    public async Task<BackupJobView?> SetEnabledAsync(TenantId tenantId, Guid jobId, bool enabled, CancellationToken cancellationToken, UserId? actor = null)
    {
        var now = time.GetUtcNow();
        var job = await schedules.ModifyJobAsync(tenantId, jobId, target =>
        {
            if (enabled) target.Enable(now);
            else target.Disable();
        }, cancellationToken, actor, now);
        if (job is null) return null;
        await TrySynchronizeAsync(tenantId, jobId, cancellationToken);
        return BackupOrchestrator.ToView(job, now);
    }

    public async Task<IReadOnlyList<BackupOccurrenceView>?> ListOccurrencesAsync(TenantId tenantId, Guid jobId, int limit, CancellationToken cancellationToken)
    {
        if (await runs.GetJobAsync(tenantId, jobId, cancellationToken) is null) return null;
        return (await schedules.ListOccurrencesAsync(tenantId, jobId, limit, cancellationToken)).Select(ToView).ToArray();
    }

    public static BackupOccurrenceView ToView(BackupScheduleOccurrence occurrence) => new(occurrence.Id, occurrence.ScheduledAt, occurrence.Outcome.ToString(),
        occurrence.Reason, occurrence.RunId, occurrence.DecidedAt, Math.Max(0, (occurrence.DecidedAt - occurrence.ScheduledAt).TotalSeconds));

    /// <summary>The wake-up a job needs: its next occurrence, or none when the job or its schedule is off.</summary>
    public static BackupScheduleTarget? TargetFor(BackupJob job, DateTimeOffset now)
    {
        if (!job.IsScheduled) return null;
        var schedule = job.Schedule!;
        return schedule.NextOccurrenceAfter(now) is { } next ? new BackupScheduleTarget(job.TenantId, job.Id, schedule.Fingerprint, next) : null;
    }

    /// <summary>Validates a request into a schedule. <paramref name="existing"/> (null for a new schedule) decides whether the anchor moves.</summary>
    public static BackupSchedule ToDomain(BackupScheduleRequest request, BackupSchedule? existing, DateTimeOffset now, BackupSchedulingOptions options)
    {
        var grace = request.MissedRunGraceSeconds is { } seconds
            ? double.IsFinite(seconds) && seconds is >= 0 and <= 31_536_000
                ? TimeSpan.FromSeconds(seconds)
                : throw new InvalidBackupScheduleException("missedRunGraceSeconds", "invalid_grace", "The missed-run grace period must be between 1 minute and 7 days.")
            : options.DefaultMissedRunGrace;
        var enabled = request.Enabled ?? true;
        return existing is null
            ? BackupSchedule.Create(enabled, request.CronExpression, request.TimeZone, grace, now, options.AllowSecondPrecisionCron)
            : existing.Change(enabled, request.CronExpression, request.TimeZone, grace, now, options.AllowSecondPrecisionCron);
    }

    private async Task NotifyAsync(AgentId agent)
    {
        try { await signal.WorkAvailableAsync(agent, CancellationToken.None); }
        catch (Exception exception) { logger.LogWarning(exception, "Scheduled backup hint delivery failed; durable polling remains authoritative."); }
    }
}
