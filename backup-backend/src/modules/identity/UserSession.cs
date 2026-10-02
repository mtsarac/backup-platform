using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public sealed class UserSession
{
    public UserSession(Guid id, UserId userId, DateTimeOffset expiresAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A session identity must not be empty.", nameof(id));
        }
        ArgumentNullException.ThrowIfNull(userId);
        Id = id;
        UserId = userId;
        ExpiresAt = expiresAt.ToUniversalTime();
    }

    public Guid Id { get; }
    public UserId UserId { get; }
    public DateTimeOffset ExpiresAt { get; }
}
