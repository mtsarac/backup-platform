using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Subscriptions;

namespace BackupPlatform.ManagedService.Administration;

/// <summary>
/// Validated plan and subscription administration. Authorization is the caller's responsibility: only a PlatformAdmin may
/// reach these operations. State rules live in <see cref="TenantSubscription"/> and <see cref="Plan"/>; this class parses
/// and validates input, and is the single place a stored row becomes a view with its effective status and limit.
/// </summary>
public sealed class ManagedServiceAdministrationService(IManagedServiceStore store, TimeProvider timeProvider)
{
    public Task<PagedResult<PlanView>> ListPlansAsync(bool? enabled, string? search, int page, int pageSize, CancellationToken cancellationToken) =>
        store.ListPlansAsync(new PlanFilter(enabled, string.IsNullOrWhiteSpace(search) ? null : search.Trim()), page, pageSize, cancellationToken);

    public Task<ManagedServiceChange<PlanView>> CreatePlanAsync(
        string? name, long? storageLimitBytes, bool? unlimited, string? quotaPolicyReference, bool? enabled, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var now = timeProvider.GetUtcNow();
        var plan = new Plan(PlanId.New(), name!, ParseStorageLimit(storageLimitBytes, unlimited), quotaPolicyReference, enabled ?? true, now);
        return store.CreatePlanAsync(plan, actor, now, cancellationToken);
    }

    public Task<ManagedServiceChange<PlanView>> UpdatePlanAsync(
        PlanId planId, string? name, long? storageLimitBytes, bool? unlimited, string? quotaPolicyReference, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planId);
        ArgumentNullException.ThrowIfNull(actor);
        var (display, _) = ManagedServiceValidation.PlanName(name);
        var limit = ParseStorageLimit(storageLimitBytes, unlimited);
        var reference = ManagedServiceValidation.QuotaPolicyReference(quotaPolicyReference);
        return store.UpdatePlanAsync(planId, display, limit, reference, actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<ManagedServiceChange<PlanView>> SetPlanEnabledAsync(PlanId planId, bool? enabled, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planId);
        ArgumentNullException.ThrowIfNull(actor);
        return enabled is null
            ? throw new InvalidManagedServiceRequestException("enabled", "required")
            : store.SetPlanEnabledAsync(planId, enabled.Value, actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task<TenantSubscriptionState?> GetSubscriptionAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var (exists, current) = await store.GetCurrentSubscriptionAsync(tenantId, cancellationToken);
        return exists ? new TenantSubscriptionState(tenantId, current is null ? null : ToView(current, timeProvider.GetUtcNow())) : null;
    }

    public async Task<PagedResult<SubscriptionView>?> ListSubscriptionsAsync(TenantId tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var result = await store.ListSubscriptionsAsync(tenantId, page, pageSize, cancellationToken);
        if (result is null)
        {
            return null;
        }
        var now = timeProvider.GetUtcNow();
        return new PagedResult<SubscriptionView>(result.Items.Select(record => ToView(record, now)).ToList(), result.TotalCount, result.Page, result.PageSize);
    }

    public async Task<ManagedServiceChange<SubscriptionAssignmentView>> AssignPlanAsync(
        TenantId tenantId, Guid? planId, string? overrideKind, long? overrideBytes, DateTimeOffset? endsAt, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        if (planId is null || planId == Guid.Empty)
        {
            throw new InvalidManagedServiceRequestException("planId", "required");
        }
        var limitOverride = ParseOverride(overrideKind, overrideBytes, optional: true);
        var now = timeProvider.GetUtcNow();
        if (endsAt is { } end && end <= now)
        {
            throw new InvalidManagedServiceRequestException("endsAt", "invalid_value");
        }
        var change = await store.AssignPlanAsync(tenantId, new PlanId(planId.Value), limitOverride, endsAt, actor, now, cancellationToken);
        return change.Value is null
            ? new(change.Outcome, null, change.Code)
            : new(change.Outcome, new SubscriptionAssignmentView(ToView(change.Value.Subscription, now), change.Value.SupersededSubscriptionId), change.Code);
    }

    public Task<ManagedServiceChange<SubscriptionView>> SuspendAsync(TenantId tenantId, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        var now = timeProvider.GetUtcNow();
        return MapAsync(store.SuspendAsync(tenantId, actor, now, cancellationToken), now);
    }

    public Task<ManagedServiceChange<SubscriptionView>> ResumeAsync(TenantId tenantId, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        var now = timeProvider.GetUtcNow();
        return MapAsync(store.ResumeAsync(tenantId, actor, now, cancellationToken), now);
    }

    public Task<ManagedServiceChange<SubscriptionView>> SetStorageLimitOverrideAsync(
        TenantId tenantId, string? kind, long? bytes, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        var limitOverride = ParseOverride(kind, bytes, optional: false);
        var now = timeProvider.GetUtcNow();
        return MapAsync(store.SetStorageLimitOverrideAsync(tenantId, limitOverride, actor, now, cancellationToken), now);
    }

    /// <summary>The one conversion from a stored row to what callers see; the override-versus-plan rule is applied by <see cref="EffectiveStorageLimit.Resolve"/>.</summary>
    public static SubscriptionView ToView(SubscriptionRecord record, DateTimeOffset now)
    {
        var subscription = record.Subscription;
        return new SubscriptionView(
            subscription.Id, subscription.TenantId, subscription.PlanId, record.Plan.Name, record.Plan.StorageLimitBytes,
            subscription.EffectiveStatusAt(now), subscription.StartsAt, subscription.EndsAt, subscription.Override,
            EffectiveStorageLimit.Resolve(record.Plan.StorageLimitBytes, subscription.Override),
            subscription.AssignedByUserId, record.AssignedByDisplayName, subscription.CreatedAt, subscription.UpdatedAt);
    }

    private static async Task<ManagedServiceChange<SubscriptionView>> MapAsync(Task<ManagedServiceChange<SubscriptionRecord>> pending, DateTimeOffset now)
    {
        var change = await pending;
        return new(change.Outcome, change.Value is null ? null : ToView(change.Value, now), change.Code);
    }

    /// <summary>Exactly one of an explicit byte count or <c>unlimited: true</c> is required, so a missing field can never silently create an unlimited plan.</summary>
    private static long? ParseStorageLimit(long? bytes, bool? unlimited)
    {
        var wantsUnlimited = unlimited == true;
        if (wantsUnlimited && bytes is not null)
        {
            throw new InvalidManagedServiceRequestException("storageLimit", "ambiguous");
        }
        if (wantsUnlimited)
        {
            return null;
        }
        if (bytes is null)
        {
            throw new InvalidManagedServiceRequestException("storageLimit", "required");
        }
        return ManagedServiceValidation.PlanStorageLimit(bytes);
    }

    private static StorageLimitOverride ParseOverride(string? kind, long? bytes, bool optional)
    {
        const string field = "storageLimitOverride";
        if (string.IsNullOrWhiteSpace(kind))
        {
            return optional && bytes is null ? StorageLimitOverride.None : throw new InvalidManagedServiceRequestException(field, optional ? "unexpected" : "invalid_kind");
        }
        if (!kind.All(char.IsLetter) || !Enum.TryParse<StorageLimitOverrideKind>(kind, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new InvalidManagedServiceRequestException(field, "invalid_kind");
        }
        switch (parsed)
        {
            case StorageLimitOverrideKind.Limited:
                return bytes is null ? throw new InvalidManagedServiceRequestException(field, "required") : StorageLimitOverride.Limited(bytes.Value);
            case StorageLimitOverrideKind.Unlimited:
                return bytes is null ? StorageLimitOverride.Unlimited : throw new InvalidManagedServiceRequestException(field, "unexpected");
            default:
                return bytes is null ? StorageLimitOverride.None : throw new InvalidManagedServiceRequestException(field, "unexpected");
        }
    }
}
