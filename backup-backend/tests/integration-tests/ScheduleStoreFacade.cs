using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Contracts.Flags;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

/// <summary>Test seam over both ScheduleStore stores: tenant calls run bound to the tenant the call names, agent and worker calls on the platform-operations store.</summary>
internal sealed class ScheduleStoreFacade(DbContextOptions<PlatformDbContext> options, IBackupAdmissionPolicy admission, AgentConnectivityOptions? connectivity = null, FeatureFlags? flags = null) : IBackupScheduleStore, IBackupSchedulePlatformStore
{
    private readonly PostgreSqlBackupSchedulePlatformStore _platform = new(options, admission, connectivity, flags);
    private IBackupScheduleStore For(TenantId tenantId) => new PostgreSqlBackupScheduleStore(options, new MutableTenantContext(tenantId));

    public Task<IReadOnlyList<BackupScheduleOccurrence>> ListOccurrencesAsync(TenantId tenantId, Guid jobId, int limit, CancellationToken cancellationToken) => For(tenantId).ListOccurrencesAsync(tenantId, jobId, limit, cancellationToken);
    public Task<BackupJob?> ModifyJobAsync(TenantId tenantId, Guid jobId, Action<BackupJob> change, CancellationToken cancellationToken, UserId? actor = null, DateTimeOffset? now = null) => For(tenantId).ModifyJobAsync(tenantId, jobId, change, cancellationToken, actor, now);
    public Task<BackupJob?> ArchiveJobAsync(TenantId tenantId, Guid jobId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).ArchiveJobAsync(tenantId, jobId, actor, now, cancellationToken);
    public Task<BackupScheduleEvaluation> EvaluateAsync(TenantId tenantId, Guid jobId, DateTimeOffset now, CancellationToken cancellationToken) => _platform.EvaluateAsync(tenantId, jobId, now, cancellationToken);
    public Task<IReadOnlyList<ScheduledJobReference>> FindDueJobsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) => _platform.FindDueJobsAsync(now, limit, cancellationToken);
    public Task<IReadOnlyList<BackupJob>> ListScheduledJobsAsync(CancellationToken cancellationToken) => _platform.ListScheduledJobsAsync(cancellationToken);
    public Task<int> ExpireUnclaimedRunsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) => _platform.ExpireUnclaimedRunsAsync(now, limit, cancellationToken);
}
