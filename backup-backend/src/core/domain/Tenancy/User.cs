using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Domain.Tenancy;

/// <summary>A global identity; tenant permissions belong exclusively to memberships.</summary>
public sealed class User
{
    public User(UserId id, string displayName)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Id = id;
        DisplayName = displayName.Trim();
    }

    public UserId Id { get; }
    public string DisplayName { get; private set; }

    public void Rename(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName.Trim();
    }
}
