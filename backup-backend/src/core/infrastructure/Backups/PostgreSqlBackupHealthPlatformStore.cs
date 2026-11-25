using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Flags;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Job-to-tenant resolution and platform-driven health reads on the unfiltered platform-operations context.</summary>
public sealed class PostgreSqlBackupHealthPlatformStore(DbContextOptions<PlatformDbContext> options, AgentConnectivityOptions connectivity, FeatureFlags? flags = null) : IBackupHealthPlatformStore
{
    private readonly BackupHealthStoreCore core = new(_ => PlatformOperationsDbContext.Create(options), connectivity, flags);

    public Task<IReadOnlyList<BackupHealthEvidence>> ReadAsync(TenantId tenantId, Guid? jobId, DateTimeOffset now, CancellationToken cancellationToken) => core.ReadAsync(tenantId, jobId, now, cancellationToken);
    public Task<IReadOnlyList<BackupCapacityRisk>> ReadCapacityRisksAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken) => core.ReadCapacityRisksAsync(tenantId, now, cancellationToken);
    public Task<(IReadOnlyList<BackupRecentSizeAnomaly> Items, int Total)> ReadRecentSizeAnomaliesAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken) => core.ReadRecentSizeAnomaliesAsync(tenantId, now, cancellationToken);
    public Task<TenantId?> FindJobTenantAsync(Guid jobId, CancellationToken cancellationToken) => core.FindJobTenantAsync(jobId, cancellationToken);
}
