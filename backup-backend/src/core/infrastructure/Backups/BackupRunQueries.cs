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

/// <summary>Row-level helpers the tenant and platform-operations run stores share.</summary>
internal static class BackupRunQueries
{
    private static readonly BackupStatus[] ActiveStatuses = [BackupStatus.Pending, BackupStatus.Leased, BackupStatus.Running, BackupStatus.RetryPending, BackupStatus.Paused];
    internal static async Task<bool> LockUnarchivedJobAsync(PlatformDbContext context, TenantId tenantId, Guid jobId, CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM backup_jobs WHERE \"TenantId\" = {tenantId.Value} AND \"Id\" = {jobId} AND \"ArchivedAt\" IS NULL FOR UPDATE")
            .ToListAsync(cancellationToken)).Count != 0;
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
}
