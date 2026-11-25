using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Application.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

/// <summary>Test seam over both recovery-key stores: calls run bound to the tenant they name.</summary>
internal sealed class TenantRecoveryKeyStoreFacade(DbContextOptions<PlatformDbContext> options, TimeProvider time) : ITenantRecoveryKeyStore, ITenantRecoveryKeyPlatformStore
{
    private readonly PostgreSqlTenantRecoveryKeyPlatformStore _platform = new(options, time);
    private ITenantRecoveryKeyStore For(TenantId tenantId) => new PostgreSqlTenantRecoveryKeyStore(options, new MutableTenantContext(tenantId), time);

    public Task<TenantRecoveryKeyInfo?> GetAsync(TenantId tenantId, CancellationToken cancellationToken) => For(tenantId).GetAsync(tenantId, cancellationToken);
    public Task<TenantRecoveryKeyInfo> SetAsync(TenantId tenantId, UserId actor, string publicKeyPem, CancellationToken cancellationToken) => For(tenantId).SetAsync(tenantId, actor, publicKeyPem, cancellationToken);
    public Task<bool> RemoveAsync(TenantId tenantId, UserId actor, CancellationToken cancellationToken) => For(tenantId).RemoveAsync(tenantId, actor, cancellationToken);
}
