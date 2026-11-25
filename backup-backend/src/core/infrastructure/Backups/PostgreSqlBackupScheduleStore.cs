using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Flags;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using BackupPlatform.Application.Tenancy;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Job edits, archiving and occurrence history of the request's tenant on the filtered context. Scheduler evaluation and sweeps are <see cref="PostgreSqlBackupSchedulePlatformStore"/>.</summary>
public sealed class PostgreSqlBackupScheduleStore(DbContextOptions<PlatformDbContext> options, ITenantContext tenant) : IBackupScheduleStore
{
    private PlatformDbContext Open(TenantId tenantId) => PlatformDbContext.ForTenant(options, tenant, tenantId);

    public async Task<IReadOnlyList<BackupScheduleOccurrence>> ListOccurrencesAsync(TenantId tenantId, Guid jobId, int limit, CancellationToken cancellationToken)
    {
        await using var context = Open(tenantId);
        return await context.BackupScheduleOccurrences.AsNoTracking().Where(occurrence => occurrence.TenantId == tenantId && occurrence.JobId == jobId)
            .OrderByDescending(occurrence => occurrence.ScheduledAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
    }

    public async Task<BackupJob?> ModifyJobAsync(TenantId tenantId, Guid jobId, Action<BackupJob> change, CancellationToken cancellationToken, UserId? actor = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        while (true)
        {
            await using var context = Open(tenantId);
            var owner = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.Id == jobId).Select(job => job.AgentId).SingleOrDefaultAsync(cancellationToken);
            if (owner is null) return null;
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (!await BackupRunQueries.LockAgentAsync(context, tenantId, owner, cancellationToken)) return null;
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

    public async Task<BackupJob?> ArchiveJobAsync(TenantId tenantId, Guid jobId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var context = Open(tenantId);
            var owner = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.Id == jobId).Select(job => job.AgentId).SingleOrDefaultAsync(cancellationToken);
            if (owner is null) return null;
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (!await BackupRunQueries.LockAgentAsync(context, tenantId, owner, cancellationToken)) return null;
            // Under the agent lock every start, claim, retry and completion of the job is serialised with this: nothing can queue a run behind the archive.
            var job = await context.BackupJobs.SingleOrDefaultAsync(job => job.TenantId == tenantId && job.Id == jobId, cancellationToken);
            if (job is null) return null;
            if (job.AgentId != owner) continue;
            var before = BackupAudit.Capture(job);
            job.Archive(now);
            BackupAudit.RecordJobChanges(context, job, before, actor, now);
            var waiting = await BackupRunQueries.Runs(context).Where(run => run.TenantId == tenantId && run.JobId == jobId &&
                (run.Status == BackupStatus.Pending || run.Status == BackupStatus.RetryPending || run.Status == BackupStatus.Paused)).ToListAsync(cancellationToken);
            foreach (var run in waiting)
            {
                var attemptWasTerminal = run.CurrentAttempt.IsTerminal;
                BackupAudit.CancelRunOfArchivedJob(context, run, actor, now);
                BackupAudit.RecordCompletion(context, run, false, attemptWasTerminal, now);
            }
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return job;
        }
    }
}
