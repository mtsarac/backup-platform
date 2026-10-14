using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;

namespace BackupPlatform.Infrastructure.Backups;

internal static class BackupAudit
{
    internal readonly record struct JobState(string Name, AgentId AgentId, BackupConfiguration Configuration, BackupRetryPolicy Retry,
        BackupOverlapPolicy Overlap, bool Enabled, BackupRetentionPolicy Retention,
        (bool Enabled, string Cron, string Zone, TimeSpan Grace, DateTimeOffset Anchor)? Schedule);

    internal static JobState Capture(BackupJob job) => new(job.Name, job.AgentId, job.Configuration, job.RetryPolicy, job.OverlapPolicy,
        job.Enabled, job.RetentionPolicy, job.Schedule is { } schedule
            ? (schedule.Enabled, schedule.CronExpression, schedule.TimeZoneId, schedule.MissedRunGracePeriod, schedule.EffectiveFrom) : null);

    internal static void RecordJobChanges(PlatformDbContext context, BackupJob job, JobState before, UserId? actor, DateTimeOffset now)
    {
        var after = Capture(job);
        if (before.Name != after.Name || before.AgentId != after.AgentId || before.Configuration != after.Configuration || before.Retry != after.Retry || before.Overlap != after.Overlap)
            AddJobEvent(context, job, actor, now, AuditActions.BackupJobUpdated);
        if (before.Enabled != after.Enabled)
            AddJobEvent(context, job, actor, now, job.Enabled ? AuditActions.BackupJobEnabled : AuditActions.BackupJobDisabled);
        if (before.Retention != after.Retention)
            AddJobEvent(context, job, actor, now, AuditActions.BackupJobRetentionChanged);
        if (before.Schedule != after.Schedule)
            AddJobEvent(context, job, actor, now, job.Schedule is null ? AuditActions.BackupJobScheduleRemoved : AuditActions.BackupJobScheduleChanged);
    }

    internal static void AddJobEvent(PlatformDbContext context, BackupJob job, UserId? actor, DateTimeOffset now, string action) =>
        context.AuditEvents.Add(AuditEvent.Record(now, actor ?? UserId.System, ActorKind(actor), action, job.TenantId,
            "backup_job", job.Id.ToString("D"), new Dictionary<string, object?>
            {
                ["result"] = "succeeded", ["agentId"] = job.AgentId.ToString(), ["enabled"] = job.Enabled,
                ["scheduleEnabled"] = job.Schedule?.Enabled, ["cronExpression"] = job.Schedule?.CronExpression,
                ["timeZone"] = job.Schedule?.TimeZoneId, ["missedRunGraceSeconds"] = job.Schedule?.MissedRunGracePeriod.TotalSeconds,
                ["overlapPolicy"] = job.OverlapPolicy.ToString(), ["retentionEnabled"] = job.RetentionPolicy.Enabled,
                ["retentionKeepForSeconds"] = job.RetentionPolicy.KeepFor.TotalSeconds,
                ["retentionDeleteGraceSeconds"] = job.RetentionPolicy.DeleteGracePeriod.TotalSeconds
            }));

    internal static AuditActorKind ActorKind(UserId? actor) => actor is null || actor == UserId.System ? AuditActorKind.System : AuditActorKind.User;

    internal static void RecordCompletion(PlatformDbContext context, BackupRun run, bool wasTerminal, bool attemptWasTerminal, DateTimeOffset now)
    {
        if (!attemptWasTerminal && run.CurrentAttempt.IsTerminal)
        {
            AddCompletion(context, run, now, AuditActions.BackupRunAttemptCompleted, run.CurrentAttempt.Status, run.CurrentAttempt.TerminalReason);
            foreach (var artifact in run.CurrentAttempt.Artifacts.Where(artifact => artifact.AcceptedAt.HasValue))
                foreach (var replica in artifact.Replicas.Where(replica => replica.VerificationState != "Verified" && replica.Status == BackupReplicaStatus.DeletePending))
                    context.AuditEvents.Add(AuditEvent.Record(now, UserId.System, AuditActorKind.System, AuditActions.BackupReplicaOrphanRecorded,
                        run.TenantId, "backup_replica", replica.Id.ToString("D"), new Dictionary<string, object?>
                        {
                            ["jobId"] = run.JobId.ToString("D"), ["runId"] = run.Id.ToString("D"), ["attemptId"] = run.CurrentAttempt.Id.ToString("D"),
                            ["artifactId"] = artifact.Id.ToString("D"), ["replicaId"] = replica.Id.ToString("D"),
                            ["storageConnectionId"] = replica.StorageConnectionId.ToString("D"), ["provider"] = replica.StorageProvider,
                            ["result"] = "cleanup_queued", ["reason"] = "unverified_completion"
                        }));
        }
        if (!wasTerminal && run.IsTerminal)
            AddCompletion(context, run, now, AuditActions.BackupRunCompleted, run.Status, run.TerminalReason);
    }

    private static void AddCompletion(PlatformDbContext context, BackupRun run, DateTimeOffset now, string action, BackupStatus status, string? reason) =>
        context.AuditEvents.Add(AuditEvent.Record(now, UserId.System, AuditActorKind.System, action, run.TenantId,
            "backup_run", run.Id.ToString("D"), new Dictionary<string, object?>
            {
                ["jobId"] = run.JobId.ToString("D"), ["runId"] = run.Id.ToString("D"), ["attemptId"] = run.CurrentAttempt.Id.ToString("D"),
                ["attemptNumber"] = run.CurrentAttempt.Number, ["status"] = status.ToString(), ["result"] = status.ToString(),
                ["terminalReason"] = reason is null ? null : BackupFailureCodes.IsAgentReportable(reason) ||
                    reason is BackupFailureCodes.AgentLost or BackupFailureCodes.Cancelled or BackupFailureCodes.ScheduleGraceExpired ? reason : "Unknown"
            }));
}
