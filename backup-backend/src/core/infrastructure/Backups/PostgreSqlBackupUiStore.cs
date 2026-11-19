using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

public sealed class PostgreSqlBackupUiStore(DbContextOptions<PlatformDbContext> options) : IBackupUiStore
{
    public async Task<IReadOnlyList<BackupJobView>> ListJobsAsync(TenantId tenantId, DateTimeOffset now, bool includeArchived, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await ReadJobsAsync(context, tenantId, null, includeArchived, now, cancellationToken);
    }

    public async Task<BackupJobView?> GetJobAsync(TenantId tenantId, Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return (await ReadJobsAsync(context, tenantId, jobId, true, now, cancellationToken)).SingleOrDefault();
    }

    public async Task<BackupRunPage> ListRunsAsync(TenantId tenantId, BackupRunQuery filter, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var query = Filter(context.BackupRuns.AsNoTracking().Where(run => run.TenantId == tenantId), filter);
        var total = await query.LongCountAsync(cancellationToken);
        var ids = await query.OrderByDescending(run => run.CreatedAt).ThenBy(run => run.Id)
            .Skip(checked((filter.Page - 1) * filter.PageSize)).Take(filter.PageSize).Select(run => run.Id).ToArrayAsync(cancellationToken);
        var runs = await PostgreSqlBackupRunStore.Runs(context).AsNoTracking().Where(run => run.TenantId == tenantId && ids.Contains(run.Id))
            .OrderByDescending(run => run.CreatedAt).ThenBy(run => run.Id).ToListAsync(cancellationToken);
        return new(await RunViewsAsync(context, tenantId, runs, cancellationToken), total, filter.Page, filter.PageSize);
    }

    public async Task<BackupRunView?> GetRunAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var run = await PostgreSqlBackupRunStore.Runs(context).AsNoTracking().SingleOrDefaultAsync(run => run.TenantId == tenantId && run.Id == runId, cancellationToken);
        return run is null ? null : (await RunViewsAsync(context, tenantId, [run], cancellationToken)).Single();
    }

    public async Task<BackupDashboardView> GetDashboardAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        // A revoked computer's jobs no longer protect anything: they stay in the job list, not in the dashboard numbers.
        var jobs = (await ReadJobsAsync(context, tenantId, null, false, now, cancellationToken)).Where(job => !job.AgentRevoked).ToList();
        var runs = context.BackupRuns.AsNoTracking().Where(run => run.TenantId == tenantId);
        var since = now.AddHours(-24);
        var active = await runs.CountAsync(run => run.Status == BackupStatus.Pending || run.Status == BackupStatus.Leased || run.Status == BackupStatus.Running || run.Status == BackupStatus.RetryPending || run.Status == BackupStatus.Paused, cancellationToken);
        var failed = await runs.CountAsync(run => run.CompletedAt >= since && (run.Status == BackupStatus.Failed || run.Status == BackupStatus.AgentLost), cancellationToken);
        var successful = await runs.Where(run => run.CompletedAt >= since && run.Status == BackupStatus.Succeeded)
            .Select(run => new
            {
                run.Configuration,
                VerifiedReplicas = run.Attempts.Where(attempt => attempt.Number == run.CurrentAttemptNumber)
                    .SelectMany(attempt => attempt.DestinationOutcomes).Count(outcome => outcome.State == BackupDestinationStates.Verified)
            }).ToListAsync(cancellationToken);
        // Completion outcomes are immutable: intentional retention must not become a failed replication execution.
        var replicationInsufficient = successful.Count(run => run.Configuration.RequiredVerifiedReplicaCount > 1 &&
            run.VerifiedReplicas < run.Configuration.RequiredVerifiedReplicaCount);
        var artifacts = context.BackupArtifacts.AsNoTracking().Where(artifact => artifact.TenantId == tenantId);
        var total = await artifacts.CountAsync(cancellationToken);
        var running = await runs.CountAsync(run => run.Status == BackupStatus.Running, cancellationToken);
        var retryPending = await runs.CountAsync(run => run.Status == BackupStatus.RetryPending, cancellationToken);
        var cancellationRequested = await runs.CountAsync(run => run.CancellationRequestedAt != null && (run.Status == BackupStatus.Leased || run.Status == BackupStatus.Running), cancellationToken);
        var storageFailed = await runs.CountAsync(run => run.CompletedAt >= since &&
            (run.TerminalReason == BackupFailureCodes.StorageWriteFailed || run.TerminalReason == BackupFailureCodes.StorageUnavailable || run.TerminalReason == BackupFailureCodes.ReplicaVerificationFailed), cancellationToken);
        var size = await artifacts.Where(artifact => artifact.Replicas.Any(replica => replica.Status != BackupReplicaStatus.Deleted)).SumAsync(artifact => (long?)artifact.SizeBytes, cancellationToken) ?? 0;
        var pending = await artifacts.CountAsync(artifact => artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.DeletePending || replica.Status == BackupReplicaStatus.Deleting), cancellationToken);
        var deleteFailed = await artifacts.CountAsync(artifact => artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.DeleteFailed), cancellationToken);
        var recent = await PostgreSqlBackupRunStore.Runs(context).AsNoTracking().Where(run => run.TenantId == tenantId)
            .OrderByDescending(run => run.CreatedAt).ThenBy(run => run.Id).Take(8).ToListAsync(cancellationToken);
        return new(new(jobs.Count, jobs.Count(job => job.Enabled), jobs.Count(job => job.Enabled && job.Schedule is { Enabled: true })),
            new(active, failed, successful.Count, running, retryPending, cancellationRequested, storageFailed, replicationInsufficient), new(total, size, pending, deleteFailed), await RunViewsAsync(context, tenantId, recent, cancellationToken),
            jobs.Where(job => job.NextOccurrenceAt is not null).OrderBy(job => job.NextOccurrenceAt).Take(5).ToArray());
    }

    // shortcut: one narrow row per run of the last ~16 days is read and bucketed in memory (a zone's midnights cannot be cut in SQL portably); push it into SQL if a tenant's run count makes this heavy.
    public async Task<BackupHealthTimelineView> GetHealthTimelineAsync(TenantId tenantId, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var jobs = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.ArchivedAt == null
                && !context.Agents.Any(agent => agent.TenantId == tenantId && agent.Id == job.AgentId && agent.Status == AgentStatus.Revoked))
            .OrderBy(job => job.Name).ThenBy(job => job.Id).Select(job => new HealthTimelineJobRow(job.Id, job.Name, job.CreatedAt)).ToListAsync(cancellationToken);
        var since = BackupHealthTimeline.RunsSince(now);
        var runs = await context.BackupRuns.AsNoTracking().Where(run => run.TenantId == tenantId && run.CreatedAt >= since)
            .Select(run => new HealthTimelineRunRow(run.JobId, run.CreatedAt, run.Status)).ToListAsync(cancellationToken);
        return new(zone.Id, BackupHealthTimeline.Build(zone, now, jobs, runs));
    }

    private static IQueryable<BackupRun> Filter(IQueryable<BackupRun> query, BackupRunQuery filter)
    {
        if (filter.JobId is { } jobId) query = query.Where(run => run.JobId == jobId);
        if (filter.AgentId is { } agentId) query = query.Where(run => run.AgentId == new AgentId(agentId));
        if (filter.Status is { } status)
        {
            if (status == "CancellationRequested")
                query = query.Where(run => run.CancellationRequestedAt != null && (run.Status == BackupStatus.Leased || run.Status == BackupStatus.Running));
            else
            {
                var parsed = Enum.Parse<BackupStatus>(status, true);
                query = query.Where(run => run.Status == parsed);
                if (parsed is BackupStatus.Leased or BackupStatus.Running) query = query.Where(run => run.CancellationRequestedAt == null);
            }
        }
        if (filter.From is { } from) query = query.Where(run => run.CreatedAt >= from);
        if (filter.To is { } to) query = query.Where(run => run.CreatedAt <= to);
        if (filter.Trigger == "Retry") query = query.Where(run => run.CurrentAttemptNumber > 1);
        else if (filter.Trigger is { } trigger) query = query.Where(run => run.TriggerType == trigger);
        return query;
    }

    private static async Task<IReadOnlyList<BackupJobView>> ReadJobsAsync(PlatformDbContext context, TenantId tenantId, Guid? jobId, bool includeArchived, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && (jobId == null || job.Id == jobId) && (includeArchived || job.ArchivedAt == null))
            .OrderBy(job => job.Name).ThenBy(job => job.Id).Select(job => new
            {
                Job = job,
                AgentName = context.Agents.Where(agent => agent.TenantId == tenantId && agent.Id == job.AgentId).Select(agent => agent.HostName).FirstOrDefault(),
                AgentRevoked = context.Agents.Any(agent => agent.TenantId == tenantId && agent.Id == job.AgentId && agent.Status == AgentStatus.Revoked),
                LastRun = context.BackupRuns.Where(run => run.TenantId == tenantId && run.JobId == job.Id).OrderByDescending(run => run.CreatedAt).ThenBy(run => run.Id)
                    .Select(run => new { run.Id, run.Status, run.CreatedAt, run.StartedAt, run.CompletedAt, run.TerminalReason, run.CancellationRequestedAt }).FirstOrDefault()
            }).ToListAsync(cancellationToken);
        return rows.Select(row =>
        {
            var view = BackupOrchestrator.ToView(row.Job, now);
            var last = row.LastRun;
            return view with { AgentName = row.AgentName, AgentRevoked = row.AgentRevoked, LastRun = last is null ? null : new(last.Id, BackupOrchestrator.RunStatus(last.Status, last.CancellationRequestedAt), last.CreatedAt, last.StartedAt, last.CompletedAt, last.TerminalReason), NextOccurrenceAt = view.Schedule?.NextOccurrenceAt };
        }).ToArray();
    }

    private static async Task<IReadOnlyList<BackupRunView>> RunViewsAsync(PlatformDbContext context, TenantId tenantId, IReadOnlyList<BackupRun> runs, CancellationToken cancellationToken)
    {
        if (runs.Count == 0) return [];
        var jobIds = runs.Select(run => run.JobId).Distinct().ToArray();
        var agentIds = runs.Select(run => run.AgentId).Distinct().ToArray();
        var jobs = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && jobIds.Contains(job.Id)).ToDictionaryAsync(job => job.Id, job => job.Name, cancellationToken);
        var agents = await context.Agents.AsNoTracking().Where(agent => agent.TenantId == tenantId && agentIds.Contains(agent.Id)).ToDictionaryAsync(agent => agent.Id, agent => agent.HostName, cancellationToken);
        return runs.Select(run => BackupOrchestrator.ToView(run) with { JobName = jobs.GetValueOrDefault(run.JobId), AgentName = agents.GetValueOrDefault(run.AgentId) }).ToArray();
    }
}
