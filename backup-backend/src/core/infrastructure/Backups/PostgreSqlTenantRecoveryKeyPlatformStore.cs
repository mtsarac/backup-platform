using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Reads the recovery recipient for agent-driven flows on the unfiltered platform-operations context.</summary>
public sealed class PostgreSqlTenantRecoveryKeyPlatformStore(DbContextOptions<PlatformDbContext> options, TimeProvider time) : ITenantRecoveryKeyPlatformStore
{
    private readonly TenantRecoveryKeyStoreCore core = new(_ => PlatformOperationsDbContext.Create(options), time);

    public Task<TenantRecoveryKeyInfo?> GetAsync(TenantId tenantId, CancellationToken cancellationToken) => core.GetAsync(tenantId, cancellationToken);
}
