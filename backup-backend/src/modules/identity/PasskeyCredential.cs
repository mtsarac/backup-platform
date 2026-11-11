using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public sealed class PasskeyCredential
{
    public PasskeyCredential(
        Guid id,
        UserId userId,
        string name,
        byte[] credentialId,
        byte[] publicKey,
        uint signatureCounter,
        Guid? aaGuid,
        DateTimeOffset createdAt,
        DateTimeOffset? lastUsedAt = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("A credential identity must not be empty.", nameof(id));
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(credentialId);
        if (credentialId.Length == 0) throw new ArgumentException("A credential id must not be empty.", nameof(credentialId));
        ArgumentNullException.ThrowIfNull(publicKey);
        if (publicKey.Length == 0) throw new ArgumentException("A public key must not be empty.", nameof(publicKey));

        Id = id;
        UserId = userId;
        Name = name.Trim();
        CredentialId = credentialId;
        PublicKey = publicKey;
        SignatureCounter = signatureCounter;
        AaGuid = aaGuid;
        CreatedAt = createdAt.ToUniversalTime();
        LastUsedAt = lastUsedAt?.ToUniversalTime();
    }

    public Guid Id { get; }
    public UserId UserId { get; }
    public string Name { get; private set; }
    public byte[] CredentialId { get; }
    public byte[] PublicKey { get; }
    public uint SignatureCounter { get; private set; }
    public Guid? AaGuid { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? LastUsedAt { get; private set; }

    public void RecordUsage(uint newCounter, DateTimeOffset usedAt)
    {
        if (newCounter > SignatureCounter)
        {
            SignatureCounter = newCounter;
        }
        LastUsedAt = usedAt.ToUniversalTime();
    }
}
