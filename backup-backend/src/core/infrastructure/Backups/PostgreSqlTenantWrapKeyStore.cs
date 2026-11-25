using System.Security.Cryptography;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Encryption;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.SecretProviders.Abstractions;
using Microsoft.EntityFrameworkCore;
using BackupPlatform.Application.Tenancy;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Wrap keys of the request's tenant. Every context it opens is the filtered one of that tenant.</summary>
public sealed class PostgreSqlTenantWrapKeyStore(DbContextOptions<PlatformDbContext> options, ITenantContext tenant, ISecretStore secrets, TimeProvider time) : ITenantWrapKeyStore
{
    private readonly TenantWrapKeyStoreCore core = new(tenantId => PlatformDbContext.ForTenant(options, tenant, tenantId), secrets, time);

    public Task<TenantWrapPublicKey> EnsureActiveAsync(TenantId tenantId, CancellationToken cancellationToken) => core.EnsureActiveAsync(tenantId, cancellationToken);
    public Task<bool> OwnsKeyAsync(TenantId tenantId, string keyId, int version, CancellationToken cancellationToken) => core.OwnsKeyAsync(tenantId, keyId, version, cancellationToken);
    public Task<byte[]> UnwrapDekAsync(TenantId tenantId, string targetType, Guid targetId, string keyId, byte[] wrappedDek, CancellationToken cancellationToken) => core.UnwrapDekAsync(tenantId, targetType, targetId, keyId, wrappedDek, cancellationToken);
    public Task<IReadOnlyList<TenantWrapKeyView>> ListAsync(TenantId tenantId, CancellationToken cancellationToken) => core.ListAsync(tenantId, cancellationToken);
}
