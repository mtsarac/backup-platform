using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

public enum SessionAuthenticationState { FullyAuthenticated, PasswordChangeRequired, SecondFactorPending }

public sealed class UserSession
{
    public UserSession(Guid id, UserId userId, DateTimeOffset createdAt, bool rememberMe, string device, string ipAddress)
    {
        if (id == Guid.Empty) throw new ArgumentException("A session identity must not be empty.", nameof(id));
        ArgumentNullException.ThrowIfNull(userId);
        Id = id;
        UserId = userId;
        CreatedAt = LastActivityAt = createdAt.ToUniversalTime();
        RememberMe = rememberMe;
        Device = device;
        IpAddress = ipAddress;
    }

    public Guid Id { get; }
    public UserId UserId { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastActivityAt { get; private set; }
    public string Device { get; }
    public string IpAddress { get; }
    public bool RememberMe { get; }
    public SessionAuthenticationState AuthenticationState { get; private set; }

    public bool IsActive(DateTimeOffset now) =>
        now < CreatedAt.AddDays(RememberMe ? 30 : 7)
        && (RememberMe || now < LastActivityAt.AddHours(12));
}
