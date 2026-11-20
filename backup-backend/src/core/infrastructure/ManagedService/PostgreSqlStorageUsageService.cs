using BackupPlatform.Application.Lists;
using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Lists;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Entitlement;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;
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

    /// <summary>
    /// One tenant's derived standing as plain columns, so the database can filter, sort and page on it. <see cref="Used"/> and <see cref="State"/> are null
    /// without a usage record (sorted last); <see cref="Limit"/> is null when unlimited or without a subscription. Only used to choose and order the rows;
    /// the values shown come from the one resolver in <see cref="ResolveStandingsAsync"/>, and a test holds the two in agreement.
    /// </summary>
    private sealed class Standing
    {
        public required TenantId TenantId { get; init; }
        public required string Name { get; init; }
        public long? Used { get; init; }
        public long? Limit { get; init; }
        public QuotaState? State { get; init; }
        public EntitlementStatus Status { get; init; }
    }

    private static readonly SortMap<Standing> StandingSorts = SortMap<Standing>.Create()
        .Text("tenant", row => row.Name)
        .Value("used", row => row.Used)
        .Value("quota", row => row.Limit)
        // The quota state sorts by its Turkish label: Kota aşıldı, Kritik, Normal, Sınırsız, Uyarı.
        .Ordinal("state", row => row.State, QuotaState.OverQuota, QuotaState.Critical, QuotaState.Normal, QuotaState.Unlimited, QuotaState.Warning);

    /// <summary>
    /// The M1 and quota rules of <see cref="TenantEntitlement.Resolve"/> and <see cref="QuotaStateResolver.Resolve"/> restated as one SQL query (current subscription
    /// by <see cref="PostgreSqlTenantEntitlementReader.OpenFirst"/>, override over plan limit, policy by the plan's reference else the default, thresholds in integers).
    /// </summary>
    private static IQueryable<Standing> Standings(PlatformDbContext context, IQueryable<Tenant> tenants, DateTimeOffset now) =>
        from tenant in tenants
        let sub = context.TenantSubscriptions.Where(row => row.TenantId == tenant.Id)
            .OrderBy(PostgreSqlTenantEntitlementReader.OpenFirst).ThenByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id).FirstOrDefault()
        let plan = context.Plans.Where(row => sub != null && row.Id == sub.PlanId).FirstOrDefault()
        let usage = context.TenantStorageUsages.Where(row => row.TenantId == tenant.Id).FirstOrDefault()
        let policy = context.QuotaPolicies.Where(row => row.Enabled && (row.IsDefault || (plan != null && row.Key == plan.QuotaPolicyReference)))
            .OrderBy(row => plan != null && row.Key == plan.QuotaPolicyReference ? 0 : 1).First()
        let limit = sub == null ? null
            : sub.StorageLimitOverrideKind == StorageLimitOverrideKind.Limited ? sub.StorageLimitOverrideBytes
            : sub.StorageLimitOverrideKind == StorageLimitOverrideKind.Unlimited ? null
            : plan!.StorageLimitBytes
        let committed = usage == null ? 0L : usage.UsedBytes + usage.ReservedBytes
        select new Standing
        {
            TenantId = tenant.Id,
            Name = tenant.Name,
            Used = usage == null ? null : committed,
            Limit = limit,
            State = usage == null ? null
                : limit == null || policy.EnforcementMode == QuotaEnforcementMode.None ? QuotaState.Unlimited
                : limit <= 0 || committed >= limit ? QuotaState.OverQuota
                : committed * 100 >= limit * policy.CriticalThresholdPercent ? QuotaState.Critical
                : committed * 100 >= limit * policy.WarningThresholdPercent ? QuotaState.Warning
                : QuotaState.Normal,
            Status = sub == null ? EntitlementStatus.NoSubscription
                : sub.Status == SubscriptionStatus.Active ? (sub.EndsAt != null && sub.EndsAt <= now ? EntitlementStatus.Expired : EntitlementStatus.Active)
                : sub.Status == SubscriptionStatus.Suspended ? (sub.EndsAt != null && sub.EndsAt <= now ? EntitlementStatus.Expired : EntitlementStatus.Suspended)
                : EntitlementStatus.Expired
        };

    public async Task<PagedResult<TenantStorageUsageListItem>> ListStorageUsageAsync(
        StorageUsageFilter filter, ListQuery query, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var rows = Standings(context, context.Tenants.AsNoTracking().ApplySearch(query, tenant => tenant.Name), now);
        if (filter.SubscriptionStatus is { } status) rows = rows.Where(row => row.Status == status);
        if (filter.State is { } state) rows = rows.Where(row => row.State == state);
        var slice = await rows.ApplySort(StandingSorts, query, "tenant", row => row.TenantId).ToPagedResultAsync(query, cancellationToken);
        var policies = await context.QuotaPolicies.AsNoTracking().Where(policy => policy.Enabled).ToListAsync(cancellationToken);
        var standings = await ResolveStandingsAsync(context, [.. slice.Items.Select(row => new TenantName(row.TenantId, row.Name))], policies, now, cancellationToken);
        return new PagedResult<TenantStorageUsageListItem>(standings, slice.TotalCount, query.Page, query.PageSize);
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
