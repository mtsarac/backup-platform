using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public enum AccountEmailPurpose { Verification, PasswordReset }

/// <summary>A single-use, short-lived email token. Only the SHA-256 of the secret is stored; it is bound to the address it was sent to.</summary>
public sealed class AccountEmailToken(Guid id, UserId userId, AccountEmailPurpose purpose, string tokenHash, string normalizedEmail, DateTimeOffset createdAt, DateTimeOffset expiresAt)
{
    public Guid Id { get; } = id;
    public UserId UserId { get; } = userId;
    public AccountEmailPurpose Purpose { get; } = purpose;
    public string TokenHash { get; } = tokenHash;
    public string NormalizedEmail { get; } = normalizedEmail;
    public DateTimeOffset CreatedAt { get; } = createdAt.ToUniversalTime();
    public DateTimeOffset ExpiresAt { get; } = expiresAt.ToUniversalTime();
    public DateTimeOffset? ConsumedAt { get; private set; }
}
