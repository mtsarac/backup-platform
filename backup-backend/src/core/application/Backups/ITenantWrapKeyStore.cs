using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

/// <summary>The public half of a tenant wrap key. Not secret: it travels to agents in a backup task.</summary>
public sealed record TenantWrapPublicKey(int Version, string KeyId, byte[] PublicKeySpki);

/// <summary>A tenant wrap key as an operator sees it: no key material at all. <paramref name="ArtifactCount"/> is how many of the tenant's artifacts are sealed to it.</summary>
public sealed record TenantWrapKeyView(int Version, string KeyId, TenantWrapKeyState State, DateTimeOffset CreatedAt, int ArtifactCount);

public sealed record TenantWrapKeyRotation(TenantWrapKeyView Key, TenantWrapKeyView? PreviousKey);

public enum TenantWrapKeyRetireOutcome { Retired, NotFound, ActiveKey, AlreadyRetired }

public sealed record TenantWrapKeyRetirement(TenantWrapKeyRetireOutcome Outcome, TenantWrapKeyView? Key = null);

/// <summary>The key is not one of the tenant's, is retired, or does not open the wrapped DEK. Carries no key material.</summary>
public sealed class TenantWrapKeyUnavailableException() : InvalidOperationException("The tenant wrap key is unavailable.");

/// <summary>Tenant wrap keys (TWK). The private half never leaves this boundary except as the unwrapped DEK of a restore or a decrypted download.</summary>
public interface ITenantWrapKeyStore
{
    /// <summary>The tenant's Active key, created on first use.</summary>
    Task<TenantWrapPublicKey> EnsureActiveAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// True when <paramref name="keyId"/> and <paramref name="version"/> are one and the same key of this tenant AND it can still seal or open artifacts (not Retired):
    /// a recipient naming a retired key would produce an artifact nobody can open through the backend.
    /// </summary>
    Task<bool> OwnsKeyAsync(TenantId tenantId, string keyId, int version, CancellationToken cancellationToken);

    /// <summary>
    /// Opens <paramref name="wrappedDek"/> with the tenant's private key and audits it against the operation that asked (ids only;
    /// <paramref name="targetType"/> is <c>restore_attempt</c> or <c>download</c>).
    /// Throws <see cref="TenantWrapKeyUnavailableException"/> for another tenant's key id, a Retired key, or a DEK the key does not open.
    /// </summary>
    Task<byte[]> UnwrapDekAsync(TenantId tenantId, string targetType, Guid targetId, string keyId, byte[] wrappedDek, CancellationToken cancellationToken);

    Task<IReadOnlyList<TenantWrapKeyView>> ListAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>The new key becomes Active and the previous Active one DecryptOnly, in one transaction with the audit (<c>tenant_wrap_key.rotated</c>).</summary>
    Task<TenantWrapKeyRotation> RotateAsync(TenantId tenantId, UserId actor, CancellationToken cancellationToken);

    /// <summary>
    /// Retires a DecryptOnly key and destroys its private half. The Active key is refused (rotate first): the tenant must always keep one key that seals new artifacts.
    /// Audited as <c>tenant_wrap_key.retired</c>.
    /// </summary>
    Task<TenantWrapKeyRetirement> RetireAsync(TenantId tenantId, int version, UserId actor, CancellationToken cancellationToken);
}

/// <summary>The platform recovery recipient from configuration: the PUBLIC key only (the private half is offline, Shamir-shared, and never on the backend).</summary>
public sealed record PlatformRecoveryKey(int Version, string KeyId, byte[] PublicKeySpki);

/// <summary>The validated <c>Encryption</c> configuration. <see cref="PlatformRecovery"/> is null when the platform has no recovery key configured.</summary>
public sealed class ArtifactRecoveryOptions(PlatformRecoveryKey? platformRecovery)
{
    public PlatformRecoveryKey? PlatformRecovery { get; } = platformRecovery;
}

public sealed record TenantRecoveryKeyInfo(int Version, string KeyId, byte[] PublicKeySpki, DateTimeOffset CreatedAt);

/// <summary>A submitted recovery key is not usable. <see cref="Code"/> is one of not_a_public_key, private_key_rejected, not_rsa, too_weak; never the key.</summary>
public sealed class InvalidRecoveryKeyException(string code) : ArgumentException("The recovery key is invalid.")
{
    public string Code { get; } = code;
}

/// <summary>A tenant's optional recovery recipient (public key). Setting and removing are audited with the key id only.</summary>
public interface ITenantRecoveryKeyStore
{
    Task<TenantRecoveryKeyInfo?> GetAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Validates and stores <paramref name="publicKeyPem"/> (replacing any earlier key, version + 1). <exception cref="InvalidRecoveryKeyException">The PEM is not a usable public key.</exception></summary>
    Task<TenantRecoveryKeyInfo> SetAsync(TenantId tenantId, UserId actor, string publicKeyPem, CancellationToken cancellationToken);

    /// <summary>False when there was none.</summary>
    Task<bool> RemoveAsync(TenantId tenantId, UserId actor, CancellationToken cancellationToken);
}
