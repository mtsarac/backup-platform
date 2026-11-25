using System.Security.Cryptography;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Encryption;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.SecretProviders.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Wrap-key rotation and retirement by the platform, and agent-driven key use, on the unfiltered platform-operations context.</summary>
public sealed class PostgreSqlTenantWrapKeyPlatformStore(DbContextOptions<PlatformDbContext> options, ISecretStore secrets, TimeProvider time) : ITenantWrapKeyPlatformStore
{
    private readonly TenantWrapKeyStoreCore core = new(_ => PlatformOperationsDbContext.Create(options), secrets, time);

    public Task<TenantWrapPublicKey> EnsureActiveAsync(TenantId tenantId, CancellationToken cancellationToken) => core.EnsureActiveAsync(tenantId, cancellationToken);
    public Task<bool> OwnsKeyAsync(TenantId tenantId, string keyId, int version, CancellationToken cancellationToken) => core.OwnsKeyAsync(tenantId, keyId, version, cancellationToken);
    public Task<byte[]> UnwrapDekAsync(TenantId tenantId, string targetType, Guid targetId, string keyId, byte[] wrappedDek, CancellationToken cancellationToken) => core.UnwrapDekAsync(tenantId, targetType, targetId, keyId, wrappedDek, cancellationToken);
    public Task<IReadOnlyList<TenantWrapKeyView>> ListAsync(TenantId tenantId, CancellationToken cancellationToken) => core.ListAsync(tenantId, cancellationToken);
    public Task<TenantWrapKeyRotation> RotateAsync(TenantId tenantId, UserId actor, CancellationToken cancellationToken) => core.RotateAsync(tenantId, actor, cancellationToken);
    public Task<TenantWrapKeyRetirement> RetireAsync(TenantId tenantId, int version, UserId actor, CancellationToken cancellationToken) => core.RetireAsync(tenantId, version, actor, cancellationToken);
}
