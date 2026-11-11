using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public sealed class PasswordCredential
{
    public PasswordCredential(UserId userId, string loginIdentifier, string passwordHash, string? email = null)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        UserId = userId;
        UpdateIdentity(loginIdentifier, email);
        PasswordHash = passwordHash;
    }

    public UserId UserId { get; }
    public string LoginIdentifier { get; private set; } = null!;
    public string NormalizedLoginIdentifier { get; private set; } = null!;
    public string? Email { get; private set; }
    public string? NormalizedEmail { get; private set; }
    public string PasswordHash { get; private set; }

    public void UpdateIdentity(string loginIdentifier, string? email)
    {
        NormalizedLoginIdentifier = LoginIdentifierNormalizer.Normalize(loginIdentifier);
        LoginIdentifier = loginIdentifier.Trim();
        Email = email?.Trim();
        NormalizedEmail = email is null ? null : LoginIdentifierNormalizer.NormalizeEmail(email);
    }

    public void ReplacePasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }
}
