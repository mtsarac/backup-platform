using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public sealed class RecoveryCode
{
    public RecoveryCode(Guid id, UserId userId, string codeHash, DateTimeOffset createdAt, DateTimeOffset? redeemedAt = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id must not be empty.", nameof(id));
        ArgumentNullException.ThrowIfNull(userId);
        if (string.IsNullOrWhiteSpace(codeHash)) throw new ArgumentException("Code hash must not be empty.", nameof(codeHash));
        Id = id;
        UserId = userId;
        CodeHash = codeHash;
        CreatedAt = createdAt.ToUniversalTime();
        RedeemedAt = redeemedAt?.ToUniversalTime();
    }

    public Guid Id { get; }
    public UserId UserId { get; }
    public string CodeHash { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? RedeemedAt { get; private set; }

    public void Redeem(DateTimeOffset now) => RedeemedAt = now.ToUniversalTime();
}
