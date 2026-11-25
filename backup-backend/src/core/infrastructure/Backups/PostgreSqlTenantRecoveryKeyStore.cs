using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using BackupPlatform.Application.Tenancy;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>The recovery key of the request's tenant on the filtered context.</summary>
public sealed class PostgreSqlTenantRecoveryKeyStore(DbContextOptions<PlatformDbContext> options, ITenantContext tenant, TimeProvider time) : ITenantRecoveryKeyStore
{
    private readonly TenantRecoveryKeyStoreCore core = new(tenantId => PlatformDbContext.ForTenant(options, tenant, tenantId), time);

    public Task<TenantRecoveryKeyInfo?> GetAsync(TenantId tenantId, CancellationToken cancellationToken) => core.GetAsync(tenantId, cancellationToken);
    public Task<TenantRecoveryKeyInfo> SetAsync(TenantId tenantId, UserId actor, string publicKeyPem, CancellationToken cancellationToken) => core.SetAsync(tenantId, actor, publicKeyPem, cancellationToken);
    public Task<bool> RemoveAsync(TenantId tenantId, UserId actor, CancellationToken cancellationToken) => core.RemoveAsync(tenantId, actor, cancellationToken);
}
