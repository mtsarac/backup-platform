using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Domain.Tenancy;

/// <summary>The tenant/user pair identifies membership. Cross-membership uniqueness is enforced by its owner.</summary>
public sealed class TenantMembership : ITenantOwned
{
    public TenantMembership(TenantId tenantId, UserId userId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(userId);
        TenantId = tenantId;
        UserId = userId;
    }

    public TenantId TenantId { get; }
    public UserId UserId { get; }
}
