using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Domain.Tenancy;

/// <summary>A global identity with an account role; tenant permissions belong exclusively to memberships.</summary>
public sealed class User
{
    public User(UserId id, string displayName, UserRole role = UserRole.UserAdmin)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ValidateRole(role);
        Id = id;
        DisplayName = displayName.Trim();
        Role = role;
    }

    public UserId Id { get; }
    public string DisplayName { get; private set; }
    public UserRole Role { get; private set; }

    public void Rename(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName.Trim();
    }

    public void ChangeRole(UserRole role)
    {
        ValidateRole(role);
        Role = role;
    }

    private static void ValidateRole(UserRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
    }
}
