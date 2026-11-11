using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public sealed class PasswordCredential
{
    public PasswordCredential(UserId userId, string loginIdentifier, string passwordHash, string? email = null)
    {
        ArgumentNullException.ThrowIfNull(userId);
        NormalizedLoginIdentifier = LoginIdentifierNormalizer.Normalize(loginIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        UserId = userId;
        LoginIdentifier = loginIdentifier.Trim();
        PasswordHash = passwordHash;
        Email = email?.Trim();
        NormalizedEmail = email is null ? null : LoginIdentifierNormalizer.NormalizeEmail(email);
    }

    public UserId UserId { get; }
    public string LoginIdentifier { get; }
    public string NormalizedLoginIdentifier { get; }
    public string? Email { get; private set; }
    public string? NormalizedEmail { get; private set; }
    public string PasswordHash { get; private set; }

    public void ReplacePasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }
}
