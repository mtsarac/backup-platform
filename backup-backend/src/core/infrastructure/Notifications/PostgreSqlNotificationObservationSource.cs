using System.Globalization;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Infrastructure.Notifications;

/// <summary>
/// The detectors read platform facts and report observations without performing transport I/O. Size anomalies also use the same one-shot
/// observation at completion, so their outbox commits with the successful run; later sweeps are idempotent. A type whose read fails is
/// omitted from the result, never reported as empty.
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
                    NotificationEventTypes.StorageCapacityCritical => new(type, true, await StorageCapacityAsync(policy, now, cancellationToken)),
                    NotificationEventTypes.BackupSizeAnomaly => new(type, false, await SizeAnomaliesAsync(policy, now, cancellationToken)),
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

    public async Task<IReadOnlySet<Guid>> RetiredSubjectsAsync(TenantId tenantId, string type, IReadOnlyCollection<Guid> subjectIds, CancellationToken cancellationToken)
    {
        if (subjectIds.Count == 0) return new HashSet<Guid>();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        // The same definitions the detectors observe with: an agent counts while it is Active, a job while it is Enabled. Anything else (or gone) was taken out of service.
        HashSet<Guid> inService;
        switch (type)
        {
            case NotificationEventTypes.AgentOffline:
                var agentIds = subjectIds.Select(id => new AgentId(id)).ToList();
                inService = (await context.Agents.AsNoTracking().Where(agent => agent.TenantId == tenantId && agentIds.Contains(agent.Id) && agent.Status == AgentStatus.Active)
                    .Select(agent => agent.Id).ToListAsync(cancellationToken)).Select(id => id.Value).ToHashSet();
                break;
            case NotificationEventTypes.BackupStale or NotificationEventTypes.ReplicaDegraded:
                inService = (await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && subjectIds.Contains(job.Id) && job.Enabled)
                    .Select(job => job.Id).ToListAsync(cancellationToken)).ToHashSet();
                break;
            case NotificationEventTypes.StorageCapacityCritical:
                inService = (await context.StorageConnections.AsNoTracking().Where(conn => conn.TenantId == tenantId && subjectIds.Contains(conn.Id) && conn.Enabled)
                    .Select(conn => conn.Id).ToListAsync(cancellationToken)).ToHashSet();
                break;
            default:
                return new HashSet<Guid>(); // quota (the tenant itself) and one-shot types have no subject that can be taken out of service
        }
        return subjectIds.Where(id => !inService.Contains(id)).ToHashSet();
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

    private async Task<IReadOnlyList<NotificationObservation>> SizeAnomaliesAsync(NotificationPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var since = new[] { policy.ActiveSince ?? policy.UpdatedAt, now - FailureLookback }.Max();
        var runs = await context.BackupRuns.AsNoTracking()
            .Where(run => run.TenantId == policy.TenantId && run.Status == BackupStatus.Succeeded &&
                run.SizeAnomalyStatus == BackupSizeAnomalyStatuses.SizeAnomalyWarning && run.CompletedAt >= since &&
                !context.NotificationEvents.Any(e => e.TenantId == policy.TenantId && e.DedupKey == NotificationEventTypes.BackupSizeAnomaly + ":" + run.Id.ToString().Replace("-", "")))
            .OrderBy(run => run.CompletedAt).ThenBy(run => run.Id).Take(MaximumFailuresPerSweep)
            .Include(run => run.Attempts).ThenInclude(attempt => attempt.Artifacts).ToListAsync(cancellationToken);
        var names = await JobNamesAsync(context, policy, runs.Select(run => run.JobId).Distinct().ToList(), cancellationToken);
        return runs.Select(run => SizeAnomaly(run, names.GetValueOrDefault(run.JobId, "Backup job"))).ToList();
    }

    internal static NotificationObservation SizeAnomaly(BackupRun run, string jobName) => new(
        $"{NotificationEventTypes.BackupSizeAnomaly}:{run.Id:N}", NotificationEventTypes.BackupSizeAnomaly, NotificationSeverity.Warning,
        new("backup_job", run.JobId, jobName), run.CompletedAt!.Value,
        new Dictionary<string, string>
        {
            ["runId"] = run.Id.ToString("D"),
            ["sizeBytes"] = run.CurrentAttempt.Artifacts.Sum(artifact => artifact.SizeBytes).ToString(CultureInfo.InvariantCulture),
            ["baselineBytes"] = run.SizeAnomalyBaselineBytes!.Value.ToString(CultureInfo.InvariantCulture),
            ["deviationPercent"] = (run.SizeAnomalyDeviationRatio!.Value * 100).ToString("0.##", CultureInfo.InvariantCulture)
        });


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

    private async Task<IReadOnlyList<NotificationObservation>> StorageCapacityAsync(NotificationPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await CollectStorageCapacityAsync(context, policy.TenantId, cancellationToken);
    }

    internal static async Task<IReadOnlyList<NotificationObservation>> CollectStorageCapacityAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken)
    {
        var connections = await context.StorageConnections.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Enabled && !c.AdministrativelyDisabled)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(cancellationToken);
        if (connections.Count == 0) return [];

        var names = connections.ToDictionary(c => c.Id, c => c.Name);
        var observations = new List<NotificationObservation>();
        foreach (var conn in connections)
        {
            var snapshot = await context.StorageCapacitySnapshots.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.ConnectionId == conn.Id && s.Status != Domain.Storage.StorageCapacityStatus.CapacityUnavailable)
                .OrderByDescending(s => s.ReportedAt)
                .ThenByDescending(s => s.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (snapshot is null) continue;

            var evaluated = Domain.Storage.StorageCapacityStatus.Evaluate(snapshot.TotalBytes, snapshot.UsedBytes, snapshot.FreeBytes);
            if (evaluated != Domain.Storage.StorageCapacityStatus.Critical) continue;
            var percent = snapshot.TotalBytes > 0 && snapshot.UsedBytes.HasValue
                ? (long)Math.Min(999, (double)snapshot.UsedBytes.Value * 100 / snapshot.TotalBytes.Value)
                : 90;
            observations.Add(new(
                $"{NotificationEventTypes.StorageCapacityCritical}:{snapshot.ConnectionId:N}",
                NotificationEventTypes.StorageCapacityCritical,
                NotificationSeverity.Critical,
                new("storage_connection", snapshot.ConnectionId, names.GetValueOrDefault(snapshot.ConnectionId, "Storage connection")),
                snapshot.ReportedAt,
                new Dictionary<string, string>
                {
                    ["percent"] = percent.ToString(),
                    ["totalBytes"] = snapshot.TotalBytes?.ToString() ?? "",
                    ["usedBytes"] = snapshot.UsedBytes?.ToString() ?? "",
                    ["freeBytes"] = snapshot.FreeBytes?.ToString() ?? ""
                }));
        }
        return observations;
    }

    private static async Task<Dictionary<Guid, string>> JobNamesAsync(PlatformDbContext context, NotificationPolicy policy, List<Guid> jobIds, CancellationToken cancellationToken) =>
        jobIds.Count == 0 ? [] : await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == policy.TenantId && jobIds.Contains(job.Id)).ToDictionaryAsync(job => job.Id, job => job.Name, cancellationToken);

    /// <summary>A stable failure code only: lowercase letters, digits, '_' and '-', bounded. Anything else (free text) is replaced, never forwarded.</summary>
    private static string Code(string? value, string fallback) =>
        value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.') ? value : fallback;
}
