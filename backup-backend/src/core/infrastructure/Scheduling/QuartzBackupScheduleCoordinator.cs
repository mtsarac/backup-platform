using BackupPlatform.Application.Backups;
using Microsoft.Extensions.Logging;
using Quartz;

namespace BackupPlatform.Infrastructure.Scheduling;

/// <summary>
/// Keeps Quartz in line with the jobs in the database: exactly one one-shot wake-up per scheduled job, at the instant the database says.
/// Quartz never receives the cron expression or time zone, so nothing stored in it can be mistaken for configuration. Its trigger names
/// carry the fire instant, so replacing a wake-up never collides with the one that is firing right now (a completing one-shot trigger
/// deletes itself by name).
/// </summary>
internal sealed class QuartzBackupScheduleCoordinator(ISchedulerFactory factory, BackupSchedulingOptions options, ILogger<QuartzBackupScheduleCoordinator> logger) : IBackupScheduleCoordinator
{
    public const string Group = "backup-schedule";
    private const string FingerprintKey = "fingerprint";

    public async Task SynchronizeAsync(Guid jobId, BackupScheduleTarget? desired, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return;
        var scheduler = await factory.GetScheduler(cancellationToken);
        if (desired is null)
        {
            await scheduler.DeleteJob(KeyOf(jobId), cancellationToken);
            return;
        }
        await ApplyAsync(scheduler, desired, cancellationToken);
    }

    public async Task<BackupScheduleReconciliation> ReconcileAsync(IReadOnlyCollection<BackupScheduleTarget> desired, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return new BackupScheduleReconciliation(0, 0, 0, 0);
        var scheduler = await factory.GetScheduler(cancellationToken);
        var wanted = desired.ToDictionary(target => target.JobId);
        var removed = 0;
        foreach (var key in await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(Group), cancellationToken))
        {
            // Disabled, deleted or foreign: whatever is not a scheduled job in the database has no business being scheduled.
            if (Guid.TryParseExact(key.Name, "N", out var id) && wanted.ContainsKey(id)) continue;
            if (await scheduler.DeleteJob(key, cancellationToken)) removed++;
        }
        int created = 0, rewritten = 0, unchanged = 0;
        foreach (var target in wanted.Values)
        {
            switch (await ApplyAsync(scheduler, target, cancellationToken))
            {
                case Change.Created: created++; break;
                case Change.Rewritten: rewritten++; break;
                default: unchanged++; break;
            }
        }
        var report = new BackupScheduleReconciliation(created, rewritten, removed, unchanged);
        if (report.Changed) logger.LogInformation("Scheduler reconciliation: {Created} created, {Rewritten} rewritten, {Removed} removed, {Unchanged} unchanged.", created, rewritten, removed, unchanged);
        return report;
    }

    private enum Change { Unchanged, Created, Rewritten }

    private static JobKey KeyOf(Guid jobId) => new(jobId.ToString("N"), Group);

    private static async Task<Change> ApplyAsync(IScheduler scheduler, BackupScheduleTarget target, CancellationToken cancellationToken)
    {
        var key = KeyOf(target.JobId);
        if (!await scheduler.Exists(key, cancellationToken))
        {
            var detail = JobBuilder.Create<ScheduledBackupJob>().WithIdentity(key).StoreDurably()
                .UsingJobData(ScheduledBackupJob.TenantKey, target.TenantId.Value.ToString()).UsingJobData(ScheduledBackupJob.JobKey, target.JobId.ToString()).Build();
            try { await scheduler.AddJob(detail, new AddJobOptions { Replace = false }, cancellationToken); }
            catch (ObjectAlreadyExistsException) { /* another node created it first */ }
        }
        var triggers = await scheduler.GetTriggersOfJob(key, cancellationToken);
        ITrigger? current = null;
        var stale = new List<ITrigger>();
        foreach (var trigger in triggers)
        {
            var healthy = trigger.StartTimeUtc == target.FireAt && trigger.JobDataMap.GetString(FingerprintKey) == target.Fingerprint
                && await scheduler.GetTriggerState(trigger.Key, cancellationToken) is TriggerState.Normal or TriggerState.Blocked or TriggerState.Executing or TriggerState.Complete;
            if (healthy && current is null) current = trigger;
            else stale.Add(trigger);
        }
        foreach (var trigger in stale) await scheduler.UnscheduleJob(trigger.Key, cancellationToken);
        if (current is not null) return stale.Count == 0 ? Change.Unchanged : Change.Rewritten;
        var next = TriggerBuilder.Create()
            .WithIdentity($"{target.JobId:N}.{target.FireAt.ToUnixTimeMilliseconds()}", Group).ForJob(key)
            .StartAt(target.FireAt).UsingJobData(FingerprintKey, target.Fingerprint)
            .WithSimpleSchedule(schedule => schedule.WithMisfireInstruction(SimpleTriggerMisfireInstruction.FireNow)).Build();
        try { await scheduler.ScheduleTrigger(next, TriggerConflict.Keep, cancellationToken); }
        catch (ObjectAlreadyExistsException) { /* another node scheduled the identical wake-up first */ }
        return stale.Count == 0 ? Change.Created : Change.Rewritten;
    }
}
