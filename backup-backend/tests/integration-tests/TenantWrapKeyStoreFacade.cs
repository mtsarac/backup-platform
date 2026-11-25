using Microsoft.Extensions.DependencyInjection;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.SecretProviders.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

/// <summary>Test seam over both wrap-key stores: key use runs bound to the tenant the call names, rotation and retirement on the platform-operations store.</summary>
internal sealed class TenantWrapKeyStoreFacade(DbContextOptions<PlatformDbContext> options, ISecretStore secrets, TimeProvider time) : ITenantWrapKeyStore, ITenantWrapKeyPlatformStore
{
    public static TenantWrapKeyStoreFacade From(IServiceProvider services) => new(services.GetRequiredService<DbContextOptions<PlatformDbContext>>(), services.GetRequiredService<ISecretStore>(), TimeProvider.System);

    private readonly PostgreSqlTenantWrapKeyPlatformStore _platform = new(options, secrets, time);
    private ITenantWrapKeyStore For(TenantId tenantId) => new PostgreSqlTenantWrapKeyStore(options, new MutableTenantContext(tenantId), secrets, time);

    public Task<TenantWrapPublicKey> EnsureActiveAsync(TenantId tenantId, CancellationToken cancellationToken) => For(tenantId).EnsureActiveAsync(tenantId, cancellationToken);
    public Task<bool> OwnsKeyAsync(TenantId tenantId, string keyId, int version, CancellationToken cancellationToken) => For(tenantId).OwnsKeyAsync(tenantId, keyId, version, cancellationToken);
    public Task<byte[]> UnwrapDekAsync(TenantId tenantId, string targetType, Guid targetId, string keyId, byte[] wrappedDek, CancellationToken cancellationToken) => For(tenantId).UnwrapDekAsync(tenantId, targetType, targetId, keyId, wrappedDek, cancellationToken);
    public Task<IReadOnlyList<TenantWrapKeyView>> ListAsync(TenantId tenantId, CancellationToken cancellationToken) => For(tenantId).ListAsync(tenantId, cancellationToken);
    public Task<TenantWrapKeyRotation> RotateAsync(TenantId tenantId, UserId actor, CancellationToken cancellationToken) => _platform.RotateAsync(tenantId, actor, cancellationToken);
    public Task<TenantWrapKeyRetirement> RetireAsync(TenantId tenantId, int version, UserId actor, CancellationToken cancellationToken) => _platform.RetireAsync(tenantId, version, actor, cancellationToken);
}
