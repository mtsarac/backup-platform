using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Subscriptions;

namespace BackupPlatform.ManagedService.Entitlement;

public enum EntitlementStatus
{
    NoSubscription = 0,
    Active = 1,
    Suspended = 2,
    Expired = 3
}

/// <summary>Operations evaluated by subscription entitlement.</summary>
public enum EntitlementOperation
{
    /// <summary>Creating a new BackupRun (manual or scheduled).</summary>
    StartNewBackupRun,
    /// <summary>A retry attempt of a BackupRun that was already admitted.</summary>
    RetryAdmittedBackupRun,
    ViewExistingBackups,
    DownloadExistingBackups,
    Restore,
    ManualDelete,
    RetentionCleanup,
    AgentConnectivity
}

/// <summary>
/// Subscription and plan state used by backup admission. Subscription eligibility does not replace quota or RBAC checks.
/// </summary>
/// <remarks>
/// Subscription eligibility:
/// <list type="bullet">
/// <item>Active: the subscription permits every operation.</item>
/// <item>Suspended, Expired and NoSubscription: a <b>new</b> BackupRun is denied with <see cref="SubscriptionRequiredCode"/>.</item>
/// <item>In every state, customer data stays reachable: viewing, downloading (subject to normal RBAC), restore, manual delete and retention cleanup,
/// as do agent connectivity and the retry attempts of a run that was already admitted. A subscription change must never cut those off.</item>
/// </list>
/// </remarks>
public sealed record TenantEntitlement(
    TenantId TenantId, EntitlementStatus Status, SubscriptionId? SubscriptionId, PlanId? PlanId, EffectiveStorageLimit? StorageLimit)
{
    public const string SubscriptionRequiredCode = "subscription_required";

    public static TenantEntitlement Resolve(TenantId tenantId, TenantSubscription? current, Plan? plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (current is null)
        {
            return new TenantEntitlement(tenantId, EntitlementStatus.NoSubscription, null, null, null);
        }
        if (plan is null || plan.Id != current.PlanId)
        {
            throw new ArgumentException("The plan must be the current subscription's plan.", nameof(plan));
        }
        var status = current.EffectiveStatusAt(now) switch
        {
            SubscriptionStatus.Active => EntitlementStatus.Active,
            SubscriptionStatus.Suspended => EntitlementStatus.Suspended,
            _ => EntitlementStatus.Expired
        };
        return new TenantEntitlement(
            tenantId, status, current.Id, current.PlanId, EffectiveStorageLimit.Resolve(plan.StorageLimitBytes, current.Override));
    }

    public bool Permits(EntitlementOperation operation) =>
        operation != EntitlementOperation.StartNewBackupRun || Status == EntitlementStatus.Active;

    /// <summary>Null when permitted; otherwise the stable denial code.</summary>
    public string? DenialCode(EntitlementOperation operation) => Permits(operation) ? null : SubscriptionRequiredCode;
}
