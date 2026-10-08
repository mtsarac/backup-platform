using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Subscriptions;

namespace BackupPlatform.ManagedService.Administration;

public sealed record PlanView(
    PlanId PlanId, string Name, bool Enabled, long? StorageLimitBytes, string? QuotaPolicyReference, int PolicyConfigurationVersion,
    bool IsSystem, string? SystemKey, int TenantCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A subscription row together with the facts the store read alongside it.</summary>
public sealed record SubscriptionRecord(TenantSubscription Subscription, Plan Plan, string? AssignedByDisplayName);

public sealed record SubscriptionView(
    SubscriptionId SubscriptionId, TenantId TenantId, PlanId PlanId, string PlanName, long? PlanStorageLimitBytes,
    SubscriptionStatus Status, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, StorageLimitOverride StorageLimitOverride,
    EffectiveStorageLimit EffectiveStorageLimit, UserId? AssignedByUserId, string? AssignedByDisplayName,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The current subscription of an existing tenant; <see cref="Subscription"/> is null while the tenant has none.</summary>
public sealed record TenantSubscriptionState(TenantId TenantId, SubscriptionView? Subscription);

/// <summary>
/// The new subscription. <see cref="SupersededSubscriptionId"/> is set when a live subscription was replaced (a plan change);
/// it is null for a first assignment and when the previous row had already run past its end (that row is closed as Expired).
/// </summary>
public sealed record SubscriptionAssignment(SubscriptionRecord Subscription, SubscriptionId? SupersededSubscriptionId);

public sealed record SubscriptionAssignmentView(SubscriptionView Subscription, SubscriptionId? SupersededSubscriptionId);

public enum ManagedServiceOutcome
{
    Changed = 1,
    /// <summary>The request matched the stored state; nothing was written and no audit event was recorded.</summary>
    Unchanged = 2,
    TenantNotFound = 3,
    PlanNotFound = 4,
    SubscriptionNotFound = 5,
    PlanNameTaken = 6,
    PlanDisabled = 7,
    SystemPlanImmutable = 8,
    PlanAlreadyAssigned = 9,
    /// <summary>The subscription's state does not allow the change; <see cref="ManagedServiceChange{T}.Code"/> says why.</summary>
    InvalidTransition = 10,
    /// <summary>A concurrent writer got there first and the change was not applied.</summary>
    ConcurrentUpdate = 11
}

public sealed record ManagedServiceChange<T>(ManagedServiceOutcome Outcome, T? Value = default, string? Code = null) where T : class;

public sealed record PlanFilter(bool? Enabled, string? Search);
