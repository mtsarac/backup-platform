using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Infrastructure.Notifications;

/// <summary>
/// The detectors: they READ the platform's existing facts (runs, agent liveness, quota standing, replica health) and report what they see. They write
/// nothing, hook into no backup code path and add no work to backup or restore, so a notification problem can never reach them, and they reuse the platform's
/// own definitions (<see cref="AgentConnectivityEvaluator"/>-style timeout, <see cref="QuotaState"/>, <see cref="BackupReplicationEvaluator"/>) instead of
/// restating them. A type whose read fails is omitted from the result, never reported as empty.
/// </summary>
public sealed class PostgreSqlNotificationObservationSource(
    DbContextOptions<PlatformDbContext> options, IStorageUsageService usage, AgentConnectivityOptions connectivity, ILogger<PostgreSqlNotificationObservationSource> logger)
    : INotificationObservationSource
{
    private const int MaximumFailuresPerSweep = 200;
    private static readonly TimeSpan FailureLookback = TimeSpan.FromDays(7);

    public async Task<IReadOnlyList<ObservationSet>> CollectAsync(NotificationPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sets = new List<ObservationSet>();
        foreach (var type in policy.EventTypes)
        {
            try
            {
                sets.Add(type switch
                {
                    NotificationEventTypes.BackupFailed => new(type, false, await FailedRunsAsync(policy, now, cancellationToken)),
                    NotificationEventTypes.BackupStale => new(type, true, await StaleJobsAsync(policy, now, cancellationToken)),
                    NotificationEventTypes.AgentOffline => new(type, true, await OfflineAgentsAsync(policy, now, cancellationToken)),
                    NotificationEventTypes.QuotaCritical => new(type, true, await QuotaAsync(policy, now, cancellationToken)),
                    NotificationEventTypes.ReplicaDegraded => new(type, true, await DegradedReplicasAsync(policy, cancellationToken)),
                    _ => throw new InvalidOperationException("Unknown notification event type.")
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError("Notification detector {EventType} for tenant {TenantId} failed ({ExceptionType}); its conditions are left untouched.", type, policy.TenantId, exception.GetType().Name);
            }
        }
        return sets;
    }

    private async Task<IReadOnlyList<NotificationObservation>> FailedRunsAsync(NotificationPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        // Only failures after the policy was switched on (and within the lookback) are news; older ones are history.
        var since = new[] { policy.ActiveSince ?? policy.UpdatedAt, now - FailureLookback }.Max();
        var failures = await context.BackupRuns.AsNoTracking()
            .Where(run => run.TenantId == policy.TenantId && (run.Status == BackupStatus.Failed || run.Status == BackupStatus.AgentLost) && run.CompletedAt >= since)
            .OrderByDescending(run => run.CompletedAt).Take(MaximumFailuresPerSweep)
            .Select(run => new { run.Id, run.JobId, run.Status, run.TerminalReason, run.CompletedAt }).ToListAsync(cancellationToken);
        var jobNames = await JobNamesAsync(context, policy, failures.Select(run => run.JobId).Distinct().ToList(), cancellationToken);
        return failures.Select(run => new NotificationObservation(
            $"{NotificationEventTypes.BackupFailed}:{run.Id:N}", NotificationEventTypes.BackupFailed, NotificationSeverity.Critical,
            new("backup_job", run.JobId, jobNames.GetValueOrDefault(run.JobId, "Backup job")), run.CompletedAt!.Value,
            new Dictionary<string, string> { ["code"] = Code(run.TerminalReason, run.Status == BackupStatus.AgentLost ? "agent_lost" : "failed") })).ToList();
    }

    private async Task<IReadOnlyList<NotificationObservation>> StaleJobsAsync(NotificationPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var threshold = TimeSpan.FromHours(policy.StaleAfterHours);
        var jobs = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == policy.TenantId && job.Enabled)
            .Select(job => new { job.Id, job.Name, job.CreatedAt }).ToListAsync(cancellationToken);
        var lastSuccess = await context.BackupRuns.AsNoTracking().Where(run => run.TenantId == policy.TenantId && run.Status == BackupStatus.Succeeded)
            .GroupBy(run => run.JobId).Select(group => new { JobId = group.Key, At = group.Max(run => run.CompletedAt) }).ToDictionaryAsync(row => row.JobId, row => row.At, cancellationToken);
        var stale = new List<NotificationObservation>();
        foreach (var job in jobs)
        {
            // A job that never succeeded is measured from its creation, so a brand-new job gets the full threshold before it is called stale.
            var reference = lastSuccess.TryGetValue(job.Id, out var last) && last is { } completed ? completed : job.CreatedAt;
            if (now - reference < threshold) continue;
            stale.Add(new($"{NotificationEventTypes.BackupStale}:{job.Id:N}", NotificationEventTypes.BackupStale, NotificationSeverity.Warning,
                new("backup_job", job.Id, job.Name), reference + threshold, new Dictionary<string, string> { ["hours"] = ((long)(now - reference).TotalHours).ToString() }));
        }
        return stale;
    }

    private async Task<IReadOnlyList<NotificationObservation>> OfflineAgentsAsync(NotificationPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        // Never earlier than the platform's own offline timeout, so "offline" here always agrees with what the console shows.
        var threshold = TimeSpan.FromMinutes(policy.AgentOfflineAfterMinutes) is var configured && configured > connectivity.OfflineTimeout ? configured : connectivity.OfflineTimeout;
        var cutoff = now - threshold;
        var seen = await context.AgentConnectivity.AsNoTracking().Where(record => record.TenantId == policy.TenantId && record.LastSeenAt < cutoff)
            .Select(record => new { record.AgentId, record.LastSeenAt }).ToListAsync(cancellationToken);
        if (seen.Count == 0) return [];
        var agents = await context.Agents.AsNoTracking().Where(agent => agent.TenantId == policy.TenantId && agent.Status == AgentStatus.Active)
            .Select(agent => new { agent.Id, agent.HostName }).ToListAsync(cancellationToken);
        var names = agents.ToDictionary(agent => agent.Id.Value, agent => agent.HostName);
        return seen.Where(record => names.ContainsKey(record.AgentId.Value)).Select(record => new NotificationObservation(
            $"{NotificationEventTypes.AgentOffline}:{record.AgentId.Value:N}", NotificationEventTypes.AgentOffline, NotificationSeverity.Critical,
            new("agent", record.AgentId.Value, names[record.AgentId.Value] is { Length: > 0 } host ? host : "Agent " + record.AgentId.Value.ToString("N")[..8]),
            record.LastSeenAt + threshold, new Dictionary<string, string> { ["minutes"] = ((long)(now - record.LastSeenAt).TotalMinutes).ToString() })).ToList();
    }

    private async Task<IReadOnlyList<NotificationObservation>> QuotaAsync(NotificationPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await usage.GetQuotaStatusAsync(policy.TenantId, now, cancellationToken) is not { LimitBytes: > 0 } status || status.State is not (QuotaState.Critical or QuotaState.OverQuota)) return [];
        var committed = QuotaStateResolver.Committed(status.UsedBytes, status.ReservedBytes);
        var percent = (long)Math.Min(999, (double)committed * 100 / status.LimitBytes.Value);
        return [new($"{NotificationEventTypes.QuotaCritical}:{policy.TenantId.Value:N}", NotificationEventTypes.QuotaCritical, NotificationSeverity.Critical,
            new("quota", policy.TenantId.Value, "Storage quota"), now,
            new Dictionary<string, string> { ["percent"] = percent.ToString(), ["state"] = status.State == QuotaState.OverQuota ? "exceeded" : "critical" })];
    }

    private async Task<IReadOnlyList<NotificationObservation>> DegradedReplicasAsync(NotificationPolicy policy, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var jobs = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == policy.TenantId && job.Enabled).Select(job => new { job.Id, job.Name }).ToListAsync(cancellationToken);
        if (jobs.Count == 0) return [];
        var jobIds = jobs.Select(job => job.Id).ToList();
        var latest = await context.BackupRuns.AsNoTracking().Where(run => run.TenantId == policy.TenantId && run.Status == BackupStatus.Succeeded && jobIds.Contains(run.JobId))
            .GroupBy(run => run.JobId).Select(group => group.OrderByDescending(run => run.CompletedAt).ThenByDescending(run => run.Id).Select(run => run.Id).First()).ToListAsync(cancellationToken);
        var runs = await PostgreSqlBackupRunStore.Runs(context).AsNoTracking().Where(run => run.TenantId == policy.TenantId && latest.Contains(run.Id)).ToListAsync(cancellationToken);
        var names = jobs.ToDictionary(job => job.Id, job => job.Name);
        var result = new List<NotificationObservation>();
        foreach (var run in runs)
        {
            // The attempt that decided the run (the succeeded one) judges its replication, exactly as the run view does.
            var health = run.Attempts.Where(attempt => attempt.Status == BackupStatus.Succeeded).OrderByDescending(attempt => attempt.Number)
                .Select(attempt => BackupReplicationEvaluator.ForAttempt(run.Configuration, attempt)).FirstOrDefault(candidate => candidate is not null);
            if (health is null || health.Status == BackupReplicationStatuses.Healthy) continue;
            var severity = health.Status == BackupReplicationStatuses.Degraded ? NotificationSeverity.Warning : NotificationSeverity.Critical;
            result.Add(new($"{NotificationEventTypes.ReplicaDegraded}:{run.JobId:N}", NotificationEventTypes.ReplicaDegraded, severity, new("backup_job", run.JobId, names.GetValueOrDefault(run.JobId, "Backup job")),
                run.CompletedAt ?? run.CreatedAt, new Dictionary<string, string>
                {
                    ["status"] = health.Status == BackupReplicationStatuses.Degraded ? "degraded" : "insufficient",
                    ["verified"] = health.VerifiedReplicas.ToString(), ["required"] = health.RequiredVerifiedReplicas.ToString()
                }));
        }
        return result;
    }

    private static async Task<Dictionary<Guid, string>> JobNamesAsync(PlatformDbContext context, NotificationPolicy policy, List<Guid> jobIds, CancellationToken cancellationToken) =>
        jobIds.Count == 0 ? [] : await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == policy.TenantId && jobIds.Contains(job.Id)).ToDictionaryAsync(job => job.Id, job => job.Name, cancellationToken);

    /// <summary>A stable failure code only: lowercase letters, digits, '_' and '-', bounded. Anything else (free text) is replaced, never forwarded.</summary>
    private static string Code(string? value, string fallback) =>
        value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.') ? value : fallback;
}
