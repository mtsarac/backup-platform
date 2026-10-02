using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Domain.Tenancy;

/// <summary>The tenant/user pair identifies membership. Cross-membership uniqueness is enforced by its owner.</summary>
public sealed class TenantMembership : ITenantOwned
{
    public TenantMembership(TenantId tenantId, UserId userId, TenantRole role)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(userId);
        ValidateRole(role);
        TenantId = tenantId;
        UserId = userId;
        Role = role;
    }

    public TenantId TenantId { get; }
    public UserId UserId { get; }
    public TenantRole Role { get; private set; }

    public void ChangeRole(TenantRole role)
    {
        ValidateRole(role);
        Role = role;
    }

    private static void ValidateRole(TenantRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
    }
}
