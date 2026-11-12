using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Identity;

/// <summary>A device (hash of its user agent) and IP address an account has signed in from; a sign-in from an unseen pair is reported as suspicious.</summary>
public sealed class KnownSignIn(UserId userId, string deviceHash, string ipAddress, DateTimeOffset firstSeenAt)
{
    public UserId UserId { get; } = userId;
    public string DeviceHash { get; } = deviceHash;
    public string IpAddress { get; } = ipAddress;
    public DateTimeOffset FirstSeenAt { get; } = firstSeenAt.ToUniversalTime();
    public DateTimeOffset LastSeenAt { get; private set; } = firstSeenAt.ToUniversalTime();
}
