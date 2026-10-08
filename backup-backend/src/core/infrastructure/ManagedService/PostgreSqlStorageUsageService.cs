using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Entitlement;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Infrastructure.ManagedService;

/// <summary>
/// Reads the usage projection and recomputes it from the source tables. Every method works by explicit tenant id under platform operations;
/// the endpoints that call it are the authorization boundary. Anything that writes holds the tenant lock for its whole transaction, so a
/// recomputation sees a consistent snapshot of what lifecycle writers have committed and they wait for it, never the other way round.
/// </summary>
public sealed class PostgreSqlStorageUsageService(
    DbContextOptions<PlatformDbContext> options, QuotaOptions quota, ILogger<PostgreSqlStorageUsageService> logger) : IStorageUsageService
{
    public async Task<TenantStorageUsageView?> GetUsageAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
        {
            return null;
        }
        var current = await ReadCurrentAsync(context, tenantId, cancellationToken);
        return current ?? await InitializeAsync(context, tenantId, cancellationToken);
    }

    public async Task<TenantQuotaStatus?> GetQuotaStatusAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
        {
            return null;
        }
        var view = await ReadCurrentAsync(context, tenantId, cancellationToken) ?? await InitializeAsync(context, tenantId, cancellationToken);
        var entitlement = await PostgreSqlTenantEntitlementReader.ResolveAsync(context, tenantId, now, cancellationToken);
        var policy = await TenantUsageEngine.ResolvePolicyAsync(context, tenantId, cancellationToken);
        // The projection was verified current against this very policy a moment ago; a concurrent change shows on the next read.
        return TenantQuotaStatus.Resolve(entitlement, policy, view.UsedBytes, view.ReservedBytes, view.CalculatedAt) with
        {
            Plan = await ReadPlanSummaryAsync(context, entitlement, cancellationToken)
        };
    }

    public async Task<PagedResult<TenantStorageUsageListItem>> ListStorageUsageAsync(
        StorageUsageFilter filter, int page, int pageSize, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var tenants = context.Tenants.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLowerInvariant();
            tenants = tenants.Where(tenant => tenant.Name.ToLower().Contains(term));
        }
        var ordered = tenants.OrderBy(tenant => tenant.Name).ThenBy(tenant => tenant.Id).Select(tenant => new TenantName(tenant.Id, tenant.Name));
        var policies = await context.QuotaPolicies.AsNoTracking().Where(policy => policy.Enabled).ToListAsync(cancellationToken);
        if (filter.State is null && filter.SubscriptionStatus is null)
        {
            // Nothing derived is filtered on, so the database pages and only that page is resolved.
            var total = await tenants.LongCountAsync(cancellationToken);
            var slice = await ordered.Skip(checked((page - 1) * pageSize)).Take(pageSize).ToListAsync(cancellationToken);
            return new PagedResult<TenantStorageUsageListItem>(await ResolveStandingsAsync(context, slice, policies, now, cancellationToken), total, page, pageSize);
        }
        // State and subscription status are derived (by the one resolver each), never stored, so they cannot be filtered in SQL without
        // restating those rules there. The rows involved are narrow: one tenant, its current subscription, its plan and its projection.
        var all = await ResolveStandingsAsync(context, await ordered.ToListAsync(cancellationToken), policies, now, cancellationToken);
        var matching = all.Where(item =>
            (filter.SubscriptionStatus is null || item.Quota.EntitlementStatus == filter.SubscriptionStatus)
            && (filter.State is null || (item.HasUsage && item.Quota.State == filter.State))).ToList();
        var pageItems = matching.Skip(checked((page - 1) * pageSize)).Take(pageSize).ToList();
        return new PagedResult<TenantStorageUsageListItem>(pageItems, matching.Count, page, pageSize);
    }

    private sealed record TenantName(TenantId Id, string Name);

    /// <summary>Resolves the tenants with three set-based reads (subscriptions, plans, projections; the policies are passed in), however many tenants there are.</summary>
    private static async Task<List<TenantStorageUsageListItem>> ResolveStandingsAsync(
        PlatformDbContext context, List<TenantName> tenants, List<QuotaPolicy> enabledPolicies, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (tenants.Count == 0)
        {
            return [];
        }
        var ids = tenants.Select(tenant => tenant.Id).ToList();
        var subscriptions = await context.TenantSubscriptions.AsNoTracking().Where(row => ids.Contains(row.TenantId)).ToListAsync(cancellationToken);
        var planIds = subscriptions.Select(row => row.PlanId).Distinct().ToList();
        var plans = (await context.Plans.AsNoTracking().Where(plan => planIds.Contains(plan.Id)).ToListAsync(cancellationToken)).ToDictionary(plan => plan.Id);
        var usages = (await context.TenantStorageUsages.AsNoTracking().Where(row => ids.Contains(row.TenantId)).ToListAsync(cancellationToken)).ToDictionary(row => row.TenantId);
        var openFirst = PostgreSqlTenantEntitlementReader.OpenFirst.Compile();
        var currentByTenant = subscriptions.GroupBy(row => row.TenantId).ToDictionary(
            group => group.Key,
            group => group.OrderBy(openFirst).ThenByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id.Value).First());
        var items = new List<TenantStorageUsageListItem>(tenants.Count);
        foreach (var tenant in tenants)
        {
            currentByTenant.TryGetValue(tenant.Id, out var current);
            var plan = current is null ? null : plans[current.PlanId];
            var entitlement = TenantEntitlement.Resolve(tenant.Id, current, plan, now);
            var policy = TenantUsageEngine.ChoosePolicy(enabledPolicies, plan?.QuotaPolicyReference);
            var hasUsage = usages.TryGetValue(tenant.Id, out var stored);
            var usageCurrent = hasUsage && new UsageRow(stored!.UsedBytes, stored.ReservedBytes, stored.PolicyId, stored.PolicyVersion, stored.AccountingMode, stored.CalculatedAt, stored.Revision).IsComputedUnder(policy);
            var quota = TenantQuotaStatus.Resolve(entitlement, policy, stored?.UsedBytes ?? 0, stored?.ReservedBytes ?? 0, stored?.CalculatedAt ?? default) with
            {
                Plan = plan is null ? null : new TenantPlanSummary(plan.Name, plan.IsSystem, plan.SystemKey, current!.EndsAt)
            };
            items.Add(new TenantStorageUsageListItem(tenant.Id, tenant.Name, quota, hasUsage, usageCurrent, stored?.CalculatedAt));
        }
        return items;
    }

    private static async Task<TenantPlanSummary?> ReadPlanSummaryAsync(PlatformDbContext context, TenantEntitlement entitlement, CancellationToken cancellationToken)
    {
        if (entitlement.SubscriptionId is null)
        {
            return null;
        }
        return await (from subscription in context.TenantSubscriptions.AsNoTracking()
                      where subscription.Id == entitlement.SubscriptionId
                      join plan in context.Plans.AsNoTracking() on subscription.PlanId equals plan.Id
                      select new TenantPlanSummary(plan.Name, plan.IsSystem, plan.SystemKey, subscription.EndsAt)).SingleOrDefaultAsync(cancellationToken);
    }

    public Task<UsageReconciliation?> ReconcileAsync(TenantId tenantId, UserId? actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        RecomputeAsync(tenantId, actor, now, rebuild: false, cancellationToken);

    public Task<UsageReconciliation?> RebuildAsync(TenantId tenantId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return RecomputeAsync(tenantId, actor, now, rebuild: true, cancellationToken);
    }

    public async Task<StorageUsageMaintenanceReport> RunMaintenanceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var errors = new List<Exception>();
        int expired = 0, initialized = 0, reconciled = 0, corrected = 0;
        var batch = quota.MaintenanceBatchSize;
        List<TenantId> withExpired = [], withoutProjection = [], oldest = [];
        try
        {
            await using var context = PlatformDbContext.CreateForPlatformOperations(options);
            var at = now.ToUniversalTime();
            // A reservation past its expiry whose run still holds a live lease is kept by the release itself; leaving it out here keeps such tenants
            // from taking batch slots every tick.
            withExpired = await context.QuotaReservations.AsNoTracking()
                .Where(row => row.ReleasedAt == null && row.ExpiresAt <= at
                    && !context.BackupRunAttempts.Any(attempt => attempt.RunId == row.RunId && attempt.TenantId == row.TenantId
                        && (attempt.Status == BackupStatus.Leased || attempt.Status == BackupStatus.Running) && attempt.LeaseExpiresAt > at))
                .Select(row => row.TenantId).Distinct().Take(batch).ToListAsync(cancellationToken);
            withoutProjection = await context.Tenants.AsNoTracking().Where(tenant => !context.TenantStorageUsages.Any(usage => usage.TenantId == tenant.Id))
                .OrderBy(tenant => tenant.Id).Select(tenant => tenant.Id).Take(batch).ToListAsync(cancellationToken);
            var staleBefore = at - quota.ReconcileAfter;
            oldest = await context.TenantStorageUsages.AsNoTracking().Where(row => row.CalculatedAt <= staleBefore)
                .OrderBy(row => row.CalculatedAt).Select(row => row.TenantId).Take(batch).ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add(exception);
        }
        foreach (var tenantId in withExpired)
        {
            await GuardedAsync(async () => expired += await ExpireReservationsAsync(tenantId, now, cancellationToken));
        }
        foreach (var tenantId in withoutProjection)
        {
            await GuardedAsync(async () =>
            {
                if (await GetUsageAsync(tenantId, cancellationToken) is not null)
                {
                    initialized++;
                }
            });
        }
        foreach (var tenantId in oldest)
        {
            await GuardedAsync(async () =>
            {
                var result = await ReconcileAsync(tenantId, null, now, cancellationToken);
                if (result is not null)
                {
                    reconciled++;
                    if (result.Drifted)
                    {
                        corrected++;
                    }
                }
            });
        }
        return new StorageUsageMaintenanceReport(expired, initialized, reconciled, corrected, errors);

        async Task GuardedAsync(Func<Task> step)
        {
            try
            {
                await step();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add(exception);
            }
        }
    }

    /// <summary>The stored row when it exists and was computed under the tenant's current policy; null otherwise. No lock: it is a plain read.</summary>
    private static async Task<TenantStorageUsageView?> ReadCurrentAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken)
    {
        var policy = await TenantUsageEngine.ResolvePolicyAsync(context, tenantId, cancellationToken);
        var row = await TenantUsageEngine.ReadAsync(context, tenantId, cancellationToken);
        return row is not null && row.IsComputedUnder(policy) ? ToView(tenantId, row, policy) : null;
    }

    private async Task<TenantStorageUsageView> InitializeAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await TenantUsageEngine.LockTenantAsync(context, tenantId, cancellationToken);
        var policy = await TenantUsageEngine.ResolvePolicyAsync(context, tenantId, cancellationToken);
        var (row, recomputed) = await TenantUsageEngine.EnsureCurrentAsync(context, tenantId, policy, DateTimeOffset.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (recomputed)
        {
            logger.LogInformation("Storage usage computed for tenant {TenantId}: {UsedBytes} bytes used, {ReservedBytes} reserved, policy {PolicyKey} v{PolicyVersion}.",
                tenantId, row.UsedBytes, row.ReservedBytes, policy.Key, policy.Version);
        }
        return ToView(tenantId, row, policy);
    }

    private async Task<int> ExpireReservationsAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await TenantUsageEngine.LockTenantAsync(context, tenantId, cancellationToken))
        {
            return 0;
        }
        var released = await TenantUsageEngine.ReleaseStaleReservationsAsync(context, tenantId, now, cancellationToken);
        if (released > 0)
        {
            await TenantUsageEngine.RefreshReservedAsync(context, tenantId, cancellationToken);
            logger.LogInformation("Quota reservations released for tenant {TenantId}: {Count} expired or orphaned.", tenantId, released);
        }
        await transaction.CommitAsync(cancellationToken);
        return released;
    }

    private async Task<UsageReconciliation?> RecomputeAsync(TenantId tenantId, UserId? actor, DateTimeOffset now, bool rebuild, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await TenantUsageEngine.LockTenantAsync(context, tenantId, cancellationToken))
        {
            return null;
        }
        var released = await TenantUsageEngine.ReleaseStaleReservationsAsync(context, tenantId, now, cancellationToken);
        var policy = await TenantUsageEngine.ResolvePolicyAsync(context, tenantId, cancellationToken);
        var previous = await TenantUsageEngine.ReadAsync(context, tenantId, cancellationToken);
        var used = await TenantUsageEngine.ComputeUsedAsync(context, tenantId, policy, cancellationToken);
        var reserved = await TenantUsageEngine.ComputeReservedAsync(context, tenantId, cancellationToken);
        var policyChanged = previous is not null && !previous.IsComputedUnder(policy);
        var drifted = previous is null || policyChanged || previous.UsedBytes != used || previous.ReservedBytes != reserved;
        if (drifted || rebuild)
        {
            await TenantUsageEngine.WriteAsync(context, tenantId, policy, used, reserved, now, cancellationToken);
        }
        else
        {
            await TenantUsageEngine.TouchAsync(context, tenantId, now, cancellationToken);
        }
        if (actor is not null && (rebuild || (drifted && previous is not null)))
        {
            context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin,
                rebuild ? ManagedServiceAuditActions.StorageUsageRebuilt : ManagedServiceAuditActions.StorageUsageCorrected, tenantId,
                ManagedServiceAuditActions.StorageUsageTargetType, tenantId.Value.ToString("D"), new Dictionary<string, object?>
                {
                    ["previousUsedBytes"] = previous?.UsedBytes,
                    ["previousReservedBytes"] = previous?.ReservedBytes,
                    ["previousPolicyVersion"] = previous?.PolicyVersion,
                    ["usedBytes"] = used,
                    ["reservedBytes"] = reserved,
                    ["policyKey"] = policy.Key,
                    ["policyVersion"] = policy.Version,
                    ["accountingMode"] = policy.AccountingMode.ToString()
                }));
            await context.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        if (policyChanged)
        {
            logger.LogInformation("Storage usage recalculated for tenant {TenantId} under policy {PolicyKey} v{PolicyVersion}: {UsedBytes} bytes (was {PreviousUsedBytes}).",
                tenantId, policy.Key, policy.Version, used, previous!.UsedBytes);
        }
        else if (drifted && previous is not null)
        {
            logger.LogWarning("Storage usage drift detected for tenant {TenantId} and corrected: used {PreviousUsedBytes} -> {UsedBytes}, reserved {PreviousReservedBytes} -> {ReservedBytes}.",
                tenantId, previous.UsedBytes, used, previous.ReservedBytes, reserved);
        }
        else if (previous is null)
        {
            logger.LogInformation("Storage usage computed for tenant {TenantId}: {UsedBytes} bytes used, {ReservedBytes} reserved.", tenantId, used, reserved);
        }
        else
        {
            logger.Log(actor is null ? LogLevel.Debug : LogLevel.Information,
                "Storage usage reconciled for tenant {TenantId}: {UsedBytes} bytes used, {ReservedBytes} reserved, no drift.", tenantId, used, reserved);
        }
        if (rebuild)
        {
            logger.LogInformation("Storage usage rebuilt for tenant {TenantId}: {UsedBytes} bytes used, {ReservedBytes} reserved, policy {PolicyKey} v{PolicyVersion}.",
                tenantId, used, reserved, policy.Key, policy.Version);
        }
        return new UsageReconciliation(
            tenantId, drifted, previous?.UsedBytes, previous?.ReservedBytes, previous?.PolicyVersion, used, reserved, policy.Id, policy.Version, released);
    }

    private static TenantStorageUsageView ToView(TenantId tenantId, UsageRow row, QuotaPolicy policy) =>
        new(tenantId, row.UsedBytes, row.ReservedBytes, row.CalculatedAt, row.AccountingMode, row.PolicyId, policy.Key, row.PolicyVersion, row.Revision);
}
