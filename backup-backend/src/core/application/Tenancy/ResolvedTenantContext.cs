using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Tenancy;

/// <summary>Immutable tenant context constructed only after the application boundary validates membership.</summary>
public sealed class ResolvedTenantContext : ITenantContext
{
    public ResolvedTenantContext(TenantAccess membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        Membership = membership;
    }

    public TenantAccess Membership { get; }
    public TenantId TenantId => Membership.TenantId;
}
