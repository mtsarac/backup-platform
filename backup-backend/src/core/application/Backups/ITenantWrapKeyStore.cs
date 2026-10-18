using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

/// <summary>The public half of a tenant wrap key. Not secret: it travels to agents in a backup task.</summary>
public sealed record TenantWrapPublicKey(int Version, string KeyId, byte[] PublicKeySpki);

/// <summary>The key is not one of the tenant's, is retired, or does not open the wrapped DEK. Carries no key material.</summary>
public sealed class TenantWrapKeyUnavailableException() : InvalidOperationException("The tenant wrap key is unavailable.");

/// <summary>Tenant wrap keys (TWK). The private half never leaves this boundary except as the unwrapped DEK of a restore.</summary>
public interface ITenantWrapKeyStore
{
    /// <summary>The tenant's Active key, created on first use.</summary>
    Task<TenantWrapPublicKey> EnsureActiveAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>True when <paramref name="keyId"/> and <paramref name="version"/> are one and the same key of this tenant.</summary>
    Task<bool> OwnsKeyAsync(TenantId tenantId, string keyId, int version, CancellationToken cancellationToken);

    /// <summary>
    /// Opens <paramref name="wrappedDek"/> with the tenant's private key and audits it against the restore attempt (ids only).
    /// Throws <see cref="TenantWrapKeyUnavailableException"/> for another tenant's key id, a Retired key, or a DEK the key does not open.
    /// </summary>
    Task<byte[]> UnwrapDekAsync(TenantId tenantId, Guid restoreAttemptId, string keyId, byte[] wrappedDek, CancellationToken cancellationToken);
}
