using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Agents.Enrollment;

/// <summary>
/// Whether a tenant may enroll agents. Owned by the Agents module so the Tenant entity stays unburdened;
/// no row means disabled. Only a platform administrator may change it.
/// </summary>
public sealed class TenantAgentEnrollmentPolicy : ITenantOwned
{
    private TenantAgentEnrollmentPolicy()
    {
        TenantId = null!;
        ChangedBy = null!;
    }

    public TenantAgentEnrollmentPolicy(TenantId tenantId, bool isEnabled, UserId changedBy, DateTimeOffset changedAt)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(changedBy);
        TenantId = tenantId;
        IsEnabled = isEnabled;
        ChangedBy = changedBy;
        ChangedAt = changedAt.ToUniversalTime();
    }

    public TenantId TenantId { get; private set; }
    public bool IsEnabled { get; private set; }
    public UserId ChangedBy { get; private set; }
    public DateTimeOffset ChangedAt { get; private set; }

    /// <summary>Returns false, leaving the audit stamp untouched, when the value is already current.</summary>
    public bool Set(bool isEnabled, UserId changedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(changedBy);
        if (IsEnabled == isEnabled)
        {
            return false;
        }
        IsEnabled = isEnabled;
        ChangedBy = changedBy;
        ChangedAt = now.ToUniversalTime();
        return true;
    }
}

/// <summary>Read model; <see cref="ChangedAt"/> is null while the tenant has never been configured.</summary>
public sealed record EnrollmentPolicyState(TenantId TenantId, bool IsEnabled, DateTimeOffset? ChangedAt);
