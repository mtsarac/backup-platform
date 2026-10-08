namespace BackupPlatform.ManagedService.Administration;

/// <summary>Audit action names of this module. Plan events belong to no tenant; subscription events carry the tenant.</summary>
public static class ManagedServiceAuditActions
{
    public const string PlanCreated = "plan.created";
    public const string PlanChanged = "plan.changed";
    public const string PlanEnabled = "plan.enabled";
    public const string PlanDisabled = "plan.disabled";
    public const string SubscriptionAssigned = "subscription.assigned";
    public const string SubscriptionChanged = "subscription.changed";
    public const string SubscriptionSuspended = "subscription.suspended";
    public const string SubscriptionResumed = "subscription.resumed";
    public const string StorageLimitOverrideChanged = "subscription.storage_limit_override_changed";

    public const string PlanTargetType = "plan";
    public const string SubscriptionTargetType = "subscription";
}
