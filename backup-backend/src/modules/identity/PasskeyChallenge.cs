using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public sealed class PasskeyChallenge
{
    public PasskeyChallenge(
        Guid id,
        UserId? userId,
        Guid? sessionId,
        string purpose,
        string optionsJson,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("A challenge identity must not be empty.", nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionsJson);

        Id = id;
        UserId = userId;
        SessionId = sessionId;
        Purpose = purpose.Trim();
        OptionsJson = optionsJson;
        CreatedAt = createdAt.ToUniversalTime();
        ExpiresAt = expiresAt.ToUniversalTime();
    }

    public Guid Id { get; }
    public UserId? UserId { get; }
    public Guid? SessionId { get; }
    public string Purpose { get; }
    public string OptionsJson { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}
