using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public sealed class PasswordCredential
{
    public PasswordCredential(UserId userId, string loginIdentifier, string passwordHash)
    {
        ArgumentNullException.ThrowIfNull(userId);
        NormalizedLoginIdentifier = LoginIdentifierNormalizer.Normalize(loginIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        UserId = userId;
        LoginIdentifier = loginIdentifier.Trim();
        PasswordHash = passwordHash;
    }

    public UserId UserId { get; }
    public string LoginIdentifier { get; }
    public string NormalizedLoginIdentifier { get; }
    public string PasswordHash { get; private set; }

    public void ReplacePasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }
}
