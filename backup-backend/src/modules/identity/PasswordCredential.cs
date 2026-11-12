using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public sealed class PasswordCredential
{
    public PasswordCredential(UserId userId, string loginIdentifier, string passwordHash, string? email = null, bool mustChangePassword = false)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        UserId = userId;
        UpdateIdentity(loginIdentifier, email);
        PasswordHash = passwordHash;
        MustChangePassword = mustChangePassword;
    }
    public UserId UserId { get; }
    public string LoginIdentifier { get; private set; } = null!;
    public string NormalizedLoginIdentifier { get; private set; } = null!;
    public string? Email { get; private set; }
    public string? NormalizedEmail { get; private set; }
    public string PasswordHash { get; private set; }
    public bool MustChangePassword { get; private set; }
    public DateTimeOffset? EmailVerifiedAt { get; private set; }
    public void UpdateIdentity(string loginIdentifier, string? email)
    {
        NormalizedLoginIdentifier = LoginIdentifierNormalizer.Normalize(loginIdentifier);
        LoginIdentifier = loginIdentifier.Trim();
        var normalizedEmail = email is null ? null : LoginIdentifierNormalizer.NormalizeEmail(email);
        if (normalizedEmail != NormalizedEmail) EmailVerifiedAt = null;
        Email = email?.Trim();
        NormalizedEmail = normalizedEmail;
    }

    public void ReplacePasswordHash(string passwordHash, bool mustChangePassword = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        MustChangePassword = mustChangePassword;
    }

    public void MarkEmailVerified(DateTimeOffset now) => EmailVerifiedAt = now.ToUniversalTime();

    public void MarkPasswordChanged(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        MustChangePassword = false;
    }
}
