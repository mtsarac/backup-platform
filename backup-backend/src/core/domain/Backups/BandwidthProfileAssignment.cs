using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

/// <summary>
/// Associates a BandwidthProfile with a scope (Tenant, Agent, or Job).
/// Profile resolution priority is Job > Agent > Tenant.
/// </summary>
public sealed class BandwidthProfileAssignment : ITenantOwned
{
    private BandwidthProfileAssignment()
    {
        TenantId = null!;
    }

    public BandwidthProfileAssignment(TenantId tenantId, UploadBandwidthScope scope, Guid scopeId, Guid profileId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (scopeId == Guid.Empty || (scope == UploadBandwidthScope.Tenant && scopeId != tenantId.Value))
            throw new ArgumentException("The scope identity is invalid.", nameof(scopeId));
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));

        TenantId = tenantId;
        Scope = scope;
        ScopeId = scopeId;
        ProfileId = profileId;
        UpdatedAt = now;
    }

    public TenantId TenantId { get; private set; }
    public UploadBandwidthScope Scope { get; private set; }
    public Guid ScopeId { get; private set; }
    public Guid ProfileId { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void ChangeProfile(Guid profileId, DateTimeOffset now)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        ProfileId = profileId;
        UpdatedAt = now;
    }
}
