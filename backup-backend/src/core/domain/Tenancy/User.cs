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
    public bool IsDisabled => IsManuallyDisabled || IsSecurityDisabled || IsNoMembershipDisabled;
    public bool IsManuallyDisabled { get; private set; }
    public bool IsSecurityDisabled { get; private set; }
    public bool IsNoMembershipDisabled { get; private set; }
    public string? ManualDisableReason { get; private set; }

    public void DisableManual(string? reason = null)
    {
        IsManuallyDisabled = true;
        ManualDisableReason = reason?.Trim();
    }

    public void EnableManual()
    {
        IsManuallyDisabled = false;
        ManualDisableReason = null;
    }

    public void SetSecurityDisabled(bool disabled) => IsSecurityDisabled = disabled;
    public void SetNoMembershipDisabled(bool disabled) => IsNoMembershipDisabled = disabled;

    public void Disable() => DisableManual(null);
    public void Enable()
    {
        EnableManual();
        IsSecurityDisabled = false;
        IsNoMembershipDisabled = false;
    }
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
