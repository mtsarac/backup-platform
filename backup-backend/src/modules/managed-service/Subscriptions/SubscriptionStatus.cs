namespace BackupPlatform.ManagedService.Subscriptions;

/// <summary>
/// Persisted as a number so a later slice can add a state without a data migration of existing rows. At most one row per
/// tenant is open (<see cref="Active"/> or <see cref="Suspended"/>); the other states are history.
/// </summary>
public enum SubscriptionStatus
{
    Active = 1,
    Suspended = 2,
    /// <summary>The subscription ran past its end. Derived at read time for an open row (see <see cref="TenantSubscription.EffectiveStatusAt"/>); terminal once stored.</summary>
    Expired = 3,
    /// <summary>Replaced by a plan change. History only; never the current subscription.</summary>
    Superseded = 4
}
