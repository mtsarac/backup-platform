using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

/// <summary>
/// A tenant's optional recovery recipient (the PUBLIC key only; the tenant keeps the private half offline). At most one per tenant: replacing it bumps
/// <see cref="Version"/>, and artifacts written earlier keep naming the version they were sealed to in their header.
/// </summary>
public sealed class TenantRecoveryKey : ITenantOwned
{
    private TenantRecoveryKey() { TenantId = null!; KeyId = null!; PublicKeySpki = null!; CreatedBy = null!; }

    public TenantRecoveryKey(Guid id, TenantId tenantId, int version, string keyId, byte[] publicKeySpki, UserId createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(publicKeySpki);
        ArgumentNullException.ThrowIfNull(createdBy);
        if (id == Guid.Empty || version < 1 || keyId.Length != 32 || !keyId.All(c => char.IsAsciiHexDigitLower(c)) || publicKeySpki.Length == 0)
            throw new ArgumentException("Invalid tenant recovery key.");
        Id = id;
        TenantId = tenantId;
        Version = version;
        KeyId = keyId;
        PublicKeySpki = publicKeySpki;
        CreatedBy = createdBy;
        CreatedAt = BackupGuard.Timestamp(createdAt);
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public int Version { get; private set; }
    /// <summary>Hex of the first 16 bytes of SHA-256 over <see cref="PublicKeySpki"/>.</summary>
    public string KeyId { get; private set; }
    public byte[] PublicKeySpki { get; private set; }
    public UserId CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Swaps in a new public key under the next version. Earlier artifacts are untouched: their headers name the key they were sealed to.</summary>
    public void Replace(string keyId, byte[] publicKeySpki, UserId by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(publicKeySpki);
        ArgumentNullException.ThrowIfNull(by);
        if (keyId.Length != 32 || !keyId.All(c => char.IsAsciiHexDigitLower(c)) || publicKeySpki.Length == 0) throw new ArgumentException("Invalid tenant recovery key.");
        Version++;
        KeyId = keyId;
        PublicKeySpki = publicKeySpki;
        CreatedBy = by;
        CreatedAt = BackupGuard.Timestamp(at);
    }
}
