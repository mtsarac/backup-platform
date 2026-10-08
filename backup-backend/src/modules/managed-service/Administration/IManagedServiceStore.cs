using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Subscriptions;

namespace BackupPlatform.ManagedService.Administration;

/// <summary>
/// Plan and subscription persistence for platform administrators. Every write records its audit event in the same
/// transaction as the change and serializes writers per tenant, so a tenant can never hold two open subscriptions.
/// The caller must already have authorized a PlatformAdmin; implementations use explicit platform operations.
/// </summary>
public interface IManagedServiceStore
{
    Task<PagedResult<PlanView>> ListPlansAsync(PlanFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    Task<ManagedServiceChange<PlanView>> CreatePlanAsync(Plan plan, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    Task<ManagedServiceChange<PlanView>> UpdatePlanAsync(
        PlanId planId, string name, long? storageLimitBytes, string? quotaPolicyReference, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    Task<ManagedServiceChange<PlanView>> SetPlanEnabledAsync(PlanId planId, bool enabled, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The newest subscription row; <c>Current</c> is null while an existing tenant has none, <c>TenantExists</c> is false for an unknown tenant.</summary>
    Task<(bool TenantExists, SubscriptionRecord? Current)> GetCurrentSubscriptionAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Newest first; null when the tenant does not exist.</summary>
    Task<PagedResult<SubscriptionRecord>?> ListSubscriptionsAsync(TenantId tenantId, int page, int pageSize, CancellationToken cancellationToken);

    Task<ManagedServiceChange<SubscriptionAssignment>> AssignPlanAsync(
        TenantId tenantId, PlanId planId, StorageLimitOverride limitOverride, DateTimeOffset? endsAt, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    Task<ManagedServiceChange<SubscriptionRecord>> SuspendAsync(TenantId tenantId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    Task<ManagedServiceChange<SubscriptionRecord>> ResumeAsync(TenantId tenantId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    Task<ManagedServiceChange<SubscriptionRecord>> SetStorageLimitOverrideAsync(
        TenantId tenantId, StorageLimitOverride limitOverride, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);
}
