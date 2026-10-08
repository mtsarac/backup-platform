using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.ManagedService.Plans;

namespace BackupPlatform.ManagedService.Subscriptions;

/// <summary>
/// One tenant's entitlement to one plan for a period. Rows are history: a plan change closes the old row
/// (<see cref="SubscriptionStatus.Superseded"/>) and starts a new one, it never rewrites which plan a row was for.
/// A tenant has at most one open row (Active or Suspended); the database enforces that independently of this class.
/// </summary>
/// <remarks>
/// State machine (persisted status):
/// Active -> Suspended | Expired | Superseded; Suspended -> Active | Expired | Superseded; Expired and Superseded are terminal.
/// An open row whose <see cref="EndsAt"/> has passed is <see cref="SubscriptionStatus.Expired"/> for every reader
/// (<see cref="EffectiveStatusAt"/>) even before any writer stores that fact, so nothing depends on a sweeper.
/// </remarks>
public sealed class TenantSubscription : ITenantOwned
{
    private TenantSubscription()
    {
        Id = null!;
        TenantId = null!;
        PlanId = null!;
    }

    private TenantSubscription(
        SubscriptionId id, TenantId tenantId, PlanId planId, StorageLimitOverride limitOverride,
        DateTimeOffset startsAt, DateTimeOffset? endsAt, UserId? assignedBy, DateTimeOffset now)
    {
        Id = id;
        TenantId = tenantId;
        PlanId = planId;
        Status = SubscriptionStatus.Active;
        StartsAt = startsAt.ToUniversalTime();
        EndsAt = endsAt?.ToUniversalTime();
        StorageLimitOverrideKind = limitOverride.Kind;
        StorageLimitOverrideBytes = limitOverride.Bytes;
        AssignedByUserId = assignedBy;
        CreatedAt = now.ToUniversalTime();
        UpdatedAt = CreatedAt;
    }

    public SubscriptionId Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public PlanId PlanId { get; private set; }
    public SubscriptionStatus Status { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    /// <summary>Planned end of an open row; the moment a Superseded or Expired row was closed.</summary>
    public DateTimeOffset? EndsAt { get; private set; }
    public StorageLimitOverrideKind StorageLimitOverrideKind { get; private set; }
    /// <summary>Decimal bytes, set only when the override kind is Limited.</summary>
    public long? StorageLimitOverrideBytes { get; private set; }
    /// <summary>The PlatformAdmin who assigned the plan; null when the system did (the legacy migration).</summary>
    public UserId? AssignedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public StorageLimitOverride Override => StorageLimitOverride.From(StorageLimitOverrideKind, StorageLimitOverrideBytes);

    public bool IsOpen => Status is SubscriptionStatus.Active or SubscriptionStatus.Suspended;

    public static TenantSubscription Start(
        TenantId tenantId, PlanId planId, StorageLimitOverride limitOverride, DateTimeOffset now, DateTimeOffset? endsAt, UserId assignedBy)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(planId);
        ArgumentNullException.ThrowIfNull(assignedBy);
        if (endsAt is { } end && end <= now)
        {
            throw new InvalidManagedServiceRequestException("endsAt", "invalid_value");
        }
        return new TenantSubscription(SubscriptionId.New(), tenantId, planId, limitOverride, now, endsAt, assignedBy, now);
    }

    public bool IsPastEnd(DateTimeOffset now) => IsOpen && EndsAt is { } end && end <= now;

    /// <summary>The state every reader must use: an open row past its end is Expired.</summary>
    public SubscriptionStatus EffectiveStatusAt(DateTimeOffset now) => IsPastEnd(now) ? SubscriptionStatus.Expired : Status;

    public void Suspend(DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Expired || IsPastEnd(now))
        {
            throw new SubscriptionTransitionException(SubscriptionTransitionCodes.Expired);
        }
        if (Status != SubscriptionStatus.Active)
        {
            throw new SubscriptionTransitionException(SubscriptionTransitionCodes.NotActive);
        }
        Status = SubscriptionStatus.Suspended;
        UpdatedAt = now.ToUniversalTime();
    }

    public void Resume(DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Expired || IsPastEnd(now))
        {
            throw new SubscriptionTransitionException(SubscriptionTransitionCodes.Expired);
        }
        if (Status != SubscriptionStatus.Suspended)
        {
            throw new SubscriptionTransitionException(SubscriptionTransitionCodes.NotSuspended);
        }
        Status = SubscriptionStatus.Active;
        UpdatedAt = now.ToUniversalTime();
    }

    /// <summary>Stores the expiry of an open row. An unset end becomes <paramref name="now"/>; a set end is kept.</summary>
    public void Expire(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw new SubscriptionTransitionException(SubscriptionTransitionCodes.NotOpen);
        }
        Status = SubscriptionStatus.Expired;
        EndsAt ??= Later(now, StartsAt);
        UpdatedAt = now.ToUniversalTime();
    }

    /// <summary>Closes an open, unexpired row because a plan change replaces it.</summary>
    public void Supersede(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw new SubscriptionTransitionException(SubscriptionTransitionCodes.NotOpen);
        }
        if (IsPastEnd(now))
        {
            throw new SubscriptionTransitionException(SubscriptionTransitionCodes.Expired);
        }
        Status = SubscriptionStatus.Superseded;
        EndsAt = Later(now, StartsAt);
        UpdatedAt = now.ToUniversalTime();
    }

    /// <summary>Returns false, leaving the row untouched, when the override is already current.</summary>
    public bool ChangeOverride(StorageLimitOverride limitOverride, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw new SubscriptionTransitionException(SubscriptionTransitionCodes.NotOpen);
        }
        if (IsPastEnd(now))
        {
            throw new SubscriptionTransitionException(SubscriptionTransitionCodes.Expired);
        }
        if (limitOverride == Override)
        {
            return false;
        }
        StorageLimitOverrideKind = limitOverride.Kind;
        StorageLimitOverrideBytes = limitOverride.Bytes;
        UpdatedAt = now.ToUniversalTime();
        return true;
    }

    private static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second) =>
        (first >= second ? first : second).ToUniversalTime();
}
