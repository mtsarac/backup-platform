using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

/// <summary>Active seals new artifacts; DecryptOnly only opens existing ones; Retired no longer opens anything.</summary>
public enum TenantWrapKeyState
{
    Active = 1,
    DecryptOnly = 2,
    Retired = 3
}

/// <summary>
/// Metadata and PUBLIC half of one tenant wrap key (TWK). The private half lives only in the secret store under <see cref="SecretReference"/>;
/// nothing in this row can decrypt anything.
/// </summary>
public sealed class TenantWrapKey : ITenantOwned
{
    private TenantWrapKey() { TenantId = null!; KeyId = null!; SecretReference = null!; PublicKeySpki = null!; }

    public TenantWrapKey(Guid id, TenantId tenantId, int version, string keyId, byte[] publicKeySpki, string secretReference, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(publicKeySpki);
        if (id == Guid.Empty || version < 1 || keyId.Length != 32 || !keyId.All(c => char.IsAsciiHexDigitLower(c)) || publicKeySpki.Length == 0 ||
            string.IsNullOrWhiteSpace(secretReference) || secretReference.Length > 128)
            throw new ArgumentException("Invalid tenant wrap key.");
        Id = id;
        TenantId = tenantId;
        Version = version;
        KeyId = keyId;
        PublicKeySpki = publicKeySpki;
        SecretReference = secretReference;
        State = TenantWrapKeyState.Active;
        CreatedAt = BackupGuard.Timestamp(createdAt);
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public int Version { get; private set; }
    /// <summary>Hex of the first 16 bytes of SHA-256 over <see cref="PublicKeySpki"/>; the id written into every artifact header.</summary>
    public string KeyId { get; private set; }
    public byte[] PublicKeySpki { get; private set; }
    /// <summary>Name of the private key (PKCS#8 PEM) in the secret store. Not secret material.</summary>
    public string SecretReference { get; private set; }
    public TenantWrapKeyState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool CanDecrypt => State != TenantWrapKeyState.Retired;
}
