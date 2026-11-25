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
using BackupPlatform.Application.Tenancy;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Health evidence of the request's tenant on the filtered context.</summary>
public sealed class PostgreSqlBackupHealthStore(DbContextOptions<PlatformDbContext> options, ITenantContext tenant, AgentConnectivityOptions connectivity, FeatureFlags? flags = null) : IBackupHealthStore
{
    private readonly BackupHealthStoreCore core = new(tenantId => PlatformDbContext.ForTenant(options, tenant, tenantId), connectivity, flags);

    public Task<IReadOnlyList<BackupHealthEvidence>> ReadAsync(TenantId tenantId, Guid? jobId, DateTimeOffset now, CancellationToken cancellationToken) => core.ReadAsync(tenantId, jobId, now, cancellationToken);
    public Task<IReadOnlyList<BackupCapacityRisk>> ReadCapacityRisksAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken) => core.ReadCapacityRisksAsync(tenantId, now, cancellationToken);
    public Task<(IReadOnlyList<BackupRecentSizeAnomaly> Items, int Total)> ReadRecentSizeAnomaliesAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken) => core.ReadRecentSizeAnomaliesAsync(tenantId, now, cancellationToken);
}
