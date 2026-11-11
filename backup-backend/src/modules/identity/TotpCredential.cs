using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public sealed class TotpCredential
{
    public TotpCredential(UserId userId, string encryptedSecret, DateTimeOffset enrolledAt, long? lastUsedEpochStep = null)
    {
        ArgumentNullException.ThrowIfNull(userId);
        if (string.IsNullOrWhiteSpace(encryptedSecret))
            throw new ArgumentException("Encrypted secret must not be empty.", nameof(encryptedSecret));
        UserId = userId;
        EncryptedSecret = encryptedSecret;
        EnrolledAt = enrolledAt.ToUniversalTime();
        LastUsedEpochStep = lastUsedEpochStep;
    }

    public UserId UserId { get; }
    public string EncryptedSecret { get; private set; }
    public DateTimeOffset EnrolledAt { get; }
    public long? LastUsedEpochStep { get; private set; }

    public void RecordUsedStep(long step) => LastUsedEpochStep = step;
}
