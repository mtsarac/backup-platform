using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Domain.Tenancy;

/// <summary>A platform-level grant, independent of any tenant membership or role.</summary>
public sealed record PlatformAdmin
{
    public PlatformAdmin(UserId userId)
    {
        ArgumentNullException.ThrowIfNull(userId);
        UserId = userId;
    }

    public UserId UserId { get; }
}
