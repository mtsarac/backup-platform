using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.Infrastructure.ManagedService;

/// <summary>
/// Plan and subscription administration under explicit platform operations; the PlatformAdmin-only endpoints are the
/// authorization boundary. Every write adds its <see cref="AuditEvent"/> to the same transaction as the change.
/// </summary>
/// <remarks>
/// Concurrency: every subscription write takes a row lock on the tenant (<c>FOR NO KEY UPDATE</c>, which does not block the
/// foreign-key checks of unrelated inserts) before it reads, so writers for one tenant run one at a time on any node. Assignment
/// also takes <c>FOR SHARE</c> on the plan row, so a concurrent disable either completes first (and the assignment is refused)
/// or waits. The partial unique index on open subscriptions is the independent backstop: should the lock ever be bypassed, the
/// second writer fails with a unique violation, which is reported as a concurrent update and never leaves two open rows.
/// The previous row is closed and flushed before the new one is inserted, because a unique index is checked per statement.
/// </remarks>
public sealed class PostgreSqlManagedServiceStore(DbContextOptions<PlatformDbContext> options, IStorageUsageService usage, TimeProvider time) : IManagedServiceStore
{
    public async Task<PagedResult<PlanView>> ListPlansAsync(PlanFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var plans = context.Plans.AsNoTracking().AsQueryable();
        if (filter.Enabled is { } enabled)
        {
            plans = plans.Where(plan => plan.Enabled == enabled);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLowerInvariant();
            plans = plans.Where(plan => plan.NormalizedName.Contains(term));
        }
        var total = await plans.LongCountAsync(cancellationToken);
        var rows = await plans
            .OrderBy(plan => plan.IsSystem).ThenBy(plan => plan.StorageLimitBytes == null).ThenBy(plan => plan.StorageLimitBytes)
            .ThenBy(plan => plan.NormalizedName).ThenBy(plan => plan.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize)
            .Select(plan => new
            {
                Plan = plan,
                TenantCount = context.TenantSubscriptions.Count(subscription =>
                    subscription.PlanId == plan.Id && (subscription.Status == SubscriptionStatus.Active || subscription.Status == SubscriptionStatus.Suspended))
            }).ToListAsync(cancellationToken);
        return new PagedResult<PlanView>(rows.Select(row => ToView(row.Plan, row.TenantCount)).ToList(), total, page, pageSize);
    }

    public async Task<ManagedServiceChange<PlanView>> CreatePlanAsync(Plan plan, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (await context.Plans.AnyAsync(row => row.NormalizedName == plan.NormalizedName, cancellationToken))
        {
            return new(ManagedServiceOutcome.PlanNameTaken);
        }
        if (await CheckPolicyReferenceAsync(context, plan.QuotaPolicyReference, cancellationToken) is { } referenceProblem)
        {
            return new(referenceProblem);
        }
        context.Plans.Add(plan);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, ManagedServiceAuditActions.PlanCreated, null,
            ManagedServiceAuditActions.PlanTargetType, Id(plan.Id), new Dictionary<string, object?>
            {
                ["name"] = plan.Name,
                ["enabled"] = plan.Enabled,
                ["storageLimitBytes"] = plan.StorageLimitBytes,
                ["quotaPolicyReference"] = plan.QuotaPolicyReference
            }));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return new(ManagedServiceOutcome.PlanNameTaken);
        }
        return new(ManagedServiceOutcome.Changed, ToView(plan, 0));
    }

    public async Task<ManagedServiceChange<PlanView>> UpdatePlanAsync(
        PlanId planId, string name, long? storageLimitBytes, string? quotaPolicyReference, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var plan = await context.Plans.SingleOrDefaultAsync(row => row.Id == planId, cancellationToken);
        if (plan is null)
        {
            return new(ManagedServiceOutcome.PlanNotFound);
        }
        if (plan.IsSystem)
        {
            return new(ManagedServiceOutcome.SystemPlanImmutable);
        }
        var normalized = ManagedServiceValidation.PlanName(name).Normalized;
        if (await context.Plans.AnyAsync(row => row.NormalizedName == normalized && row.Id != planId, cancellationToken))
        {
            return new(ManagedServiceOutcome.PlanNameTaken);
        }
        var requestedReference = ManagedServiceValidation.QuotaPolicyReference(quotaPolicyReference);
        // A plan that keeps its (since disabled) policy may still be renamed or re-priced; only pointing at a different policy is checked.
        if (!string.Equals(plan.QuotaPolicyReference, requestedReference, StringComparison.Ordinal)
            && await CheckPolicyReferenceAsync(context, requestedReference, cancellationToken) is { } referenceProblem)
        {
            return new(referenceProblem);
        }
        var (previousName, previousLimit, previousReference) = (plan.Name, plan.StorageLimitBytes, plan.QuotaPolicyReference);
        var outcome = ManagedServiceOutcome.Unchanged;
        if (plan.Update(name, storageLimitBytes, quotaPolicyReference, now))
        {
            var details = new Dictionary<string, object?>();
            if (!string.Equals(previousName, plan.Name, StringComparison.Ordinal))
            {
                details["previousName"] = previousName;
                details["name"] = plan.Name;
            }
            if (previousLimit != plan.StorageLimitBytes)
            {
                details["previousStorageLimitBytes"] = previousLimit;
                details["storageLimitBytes"] = plan.StorageLimitBytes;
            }
            if (!string.Equals(previousReference, plan.QuotaPolicyReference, StringComparison.Ordinal))
            {
                details["previousQuotaPolicyReference"] = previousReference;
                details["quotaPolicyReference"] = plan.QuotaPolicyReference;
            }
            context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, ManagedServiceAuditActions.PlanChanged, null,
                ManagedServiceAuditActions.PlanTargetType, Id(plan.Id), details));
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new(ManagedServiceOutcome.ConcurrentUpdate);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                return new(ManagedServiceOutcome.PlanNameTaken);
            }
            outcome = ManagedServiceOutcome.Changed;
            if (!string.Equals(previousReference, plan.QuotaPolicyReference, StringComparison.Ordinal))
            {
                // The plan's tenants now count usage by another policy. Their projections are recognised as stale by their recorded policy
                // anyway; this makes the new figures available now instead of at each tenant's next read.
                await RecalculateTenantsOfPlanAsync(context, plan.Id, now, cancellationToken);
            }
        }
        return new(outcome, ToView(plan, await CountOpenAsync(context, plan.Id, cancellationToken)));
    }

    public async Task<ManagedServiceChange<PlanView>> SetPlanEnabledAsync(
        PlanId planId, bool enabled, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var plan = await context.Plans.SingleOrDefaultAsync(row => row.Id == planId, cancellationToken);
        if (plan is null)
        {
            return new(ManagedServiceOutcome.PlanNotFound);
        }
        if (plan.IsSystem)
        {
            return new(ManagedServiceOutcome.SystemPlanImmutable);
        }
        var outcome = ManagedServiceOutcome.Unchanged;
        if (plan.SetEnabled(enabled, now))
        {
            context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin,
                enabled ? ManagedServiceAuditActions.PlanEnabled : ManagedServiceAuditActions.PlanDisabled, null,
                ManagedServiceAuditActions.PlanTargetType, Id(plan.Id), new Dictionary<string, object?> { ["name"] = plan.Name }));
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new(ManagedServiceOutcome.ConcurrentUpdate);
            }
            outcome = ManagedServiceOutcome.Changed;
        }
        return new(outcome, ToView(plan, await CountOpenAsync(context, plan.Id, cancellationToken)));
    }

    public async Task<(bool TenantExists, SubscriptionRecord? Current)> GetCurrentSubscriptionAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
        {
            return (false, null);
        }
        var row = await Rows(context, tenantId)
            .OrderBy(item => item.Subscription.Status == SubscriptionStatus.Active || item.Subscription.Status == SubscriptionStatus.Suspended ? 0 : 1)
            .ThenByDescending(item => item.Subscription.CreatedAt).ThenByDescending(item => item.Subscription.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return (true, row is null ? null : (await ToRecordsAsync(context, [row], cancellationToken))[0]);
    }

    public async Task<PagedResult<SubscriptionRecord>?> ListSubscriptionsAsync(TenantId tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
        {
            return null;
        }
        var query = Rows(context, tenantId);
        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(item => item.Subscription.CreatedAt)
            .ThenBy(item => item.Subscription.Status == SubscriptionStatus.Active || item.Subscription.Status == SubscriptionStatus.Suspended ? 0 : 1)
            .ThenByDescending(item => item.Subscription.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<SubscriptionRecord>(await ToRecordsAsync(context, rows, cancellationToken), total, page, pageSize);
    }

    public async Task<ManagedServiceChange<SubscriptionAssignment>> AssignPlanAsync(
        TenantId tenantId, PlanId planId, StorageLimitOverride limitOverride, DateTimeOffset? endsAt, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockTenantAsync(context, tenantId, cancellationToken))
        {
            return new(ManagedServiceOutcome.TenantNotFound);
        }
        var planLocked = await context.Database.SqlQuery<Guid>(
            $"SELECT \"Id\" AS \"Value\" FROM plans WHERE \"Id\" = {planId.Value} FOR SHARE").ToListAsync(cancellationToken);
        var plan = planLocked.Count == 0 ? null : await context.Plans.AsNoTracking().SingleAsync(row => row.Id == planId, cancellationToken);
        if (plan is null)
        {
            return new(ManagedServiceOutcome.PlanNotFound);
        }
        if (!plan.Enabled)
        {
            return new(ManagedServiceOutcome.PlanDisabled);
        }
        var previous = await LoadOpenAsync(context, tenantId, cancellationToken);
        Dictionary<string, object?>? previousDetails = null;
        SubscriptionId? superseded = null;
        if (previous is not null)
        {
            var previousPlan = await context.Plans.AsNoTracking().SingleAsync(row => row.Id == previous.PlanId, cancellationToken);
            var live = !previous.IsPastEnd(now);
            if (live && previous.PlanId == planId)
            {
                return new(ManagedServiceOutcome.PlanAlreadyAssigned);
            }
            previousDetails = new Dictionary<string, object?>
            {
                [live ? "previousSubscriptionId" : "expiredSubscriptionId"] = Id(previous.Id),
                ["previousPlanId"] = Id(previous.PlanId),
                ["previousPlanName"] = previousPlan.Name,
                ["previousStatus"] = previous.Status.ToString()
            };
            if (live)
            {
                previous.Supersede(now);
                superseded = previous.Id;
            }
            else
            {
                previous.Expire(now);
            }
            // Flushed alone: the unique index on open rows is checked per statement, so the old row must be closed first.
            await context.SaveChangesAsync(cancellationToken);
        }
        var subscription = TenantSubscription.Start(tenantId, planId, limitOverride, now, endsAt, actor);
        context.TenantSubscriptions.Add(subscription);
        var details = new Dictionary<string, object?>
        {
            ["planId"] = Id(plan.Id),
            ["planName"] = plan.Name,
            ["planStorageLimitBytes"] = plan.StorageLimitBytes,
            ["storageLimitOverrideKind"] = limitOverride.Kind.ToString(),
            ["storageLimitOverrideBytes"] = limitOverride.Bytes,
            ["startsAt"] = subscription.StartsAt,
            ["endsAt"] = subscription.EndsAt
        };
        foreach (var (key, value) in previousDetails ?? [])
        {
            details[key] = value;
        }
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin,
            superseded is null ? ManagedServiceAuditActions.SubscriptionAssigned : ManagedServiceAuditActions.SubscriptionChanged, tenantId,
            ManagedServiceAuditActions.SubscriptionTargetType, Id(subscription.Id), details));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            // The new plan may count usage by another policy. Brought up to date here, under the tenant lock this transaction already holds,
            // so there is no instant at which the subscription has changed but the projection still describes the old policy.
            var effectivePolicy = await TenantUsageEngine.ResolvePolicyAsync(context, tenantId, cancellationToken);
            await TenantUsageEngine.EnsureCurrentAsync(context, tenantId, effectivePolicy, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception) || exception is DbUpdateConcurrencyException)
        {
            return new(ManagedServiceOutcome.ConcurrentUpdate);
        }
        var record = (await ToRecordsAsync(context, [new Row { Subscription = subscription, Plan = plan }], cancellationToken))[0];
        return new(ManagedServiceOutcome.Changed, new SubscriptionAssignment(record, superseded));
    }

    public Task<ManagedServiceChange<SubscriptionRecord>> SuspendAsync(TenantId tenantId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        TransitionAsync(tenantId, actor, now, ManagedServiceAuditActions.SubscriptionSuspended,
            subscription => { subscription.Suspend(now); return null; }, cancellationToken);

    public Task<ManagedServiceChange<SubscriptionRecord>> ResumeAsync(TenantId tenantId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        TransitionAsync(tenantId, actor, now, ManagedServiceAuditActions.SubscriptionResumed,
            subscription => { subscription.Resume(now); return null; }, cancellationToken);

    public Task<ManagedServiceChange<SubscriptionRecord>> SetStorageLimitOverrideAsync(
        TenantId tenantId, StorageLimitOverride limitOverride, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        TransitionAsync(tenantId, actor, now, ManagedServiceAuditActions.StorageLimitOverrideChanged, subscription =>
        {
            var previous = subscription.Override;
            return subscription.ChangeOverride(limitOverride, now)
                ? new Dictionary<string, object?>
                {
                    ["previousKind"] = previous.Kind.ToString(),
                    ["previousBytes"] = previous.Bytes,
                    ["kind"] = limitOverride.Kind.ToString(),
                    ["bytes"] = limitOverride.Bytes
                }
                : null;
        }, cancellationToken);

    /// <summary>
    /// Runs one state change on the tenant's current subscription under the tenant lock. The change callback applies the
    /// domain transition and returns extra audit details, or null when it changed nothing beyond the status. A call that changes
    /// neither (an override identical to the stored one) writes nothing and records no audit event.
    /// </summary>
    private async Task<ManagedServiceChange<SubscriptionRecord>> TransitionAsync(
        TenantId tenantId, UserId actor, DateTimeOffset now, string action, Func<TenantSubscription, Dictionary<string, object?>?> change, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockTenantAsync(context, tenantId, cancellationToken))
        {
            return new(ManagedServiceOutcome.TenantNotFound);
        }
        var subscription = await context.TenantSubscriptions
            .OrderBy(row => row.Status == SubscriptionStatus.Active || row.Status == SubscriptionStatus.Suspended ? 0 : 1)
            .ThenByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id)
            .FirstOrDefaultAsync(row => row.TenantId == tenantId, cancellationToken);
        if (subscription is null)
        {
            return new(ManagedServiceOutcome.SubscriptionNotFound);
        }
        var plan = await context.Plans.AsNoTracking().SingleAsync(row => row.Id == subscription.PlanId, cancellationToken);
        var previousStatus = subscription.Status;
        Dictionary<string, object?>? extra;
        try
        {
            extra = change(subscription);
        }
        catch (SubscriptionTransitionException exception)
        {
            return new(ManagedServiceOutcome.InvalidTransition, null, exception.Code);
        }
        var outcome = ManagedServiceOutcome.Unchanged;
        if (extra is not null || subscription.Status != previousStatus)
        {
            var details = new Dictionary<string, object?>
            {
                ["planId"] = Id(plan.Id),
                ["planName"] = plan.Name,
                ["previousStatus"] = previousStatus.ToString(),
                ["status"] = subscription.Status.ToString()
            };
            foreach (var (key, value) in extra ?? [])
            {
                details[key] = value;
            }
            context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, action, tenantId,
                ManagedServiceAuditActions.SubscriptionTargetType, Id(subscription.Id), details));
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception) || exception is DbUpdateConcurrencyException)
            {
                return new(ManagedServiceOutcome.ConcurrentUpdate);
            }
            outcome = ManagedServiceOutcome.Changed;
        }
        var record = (await ToRecordsAsync(context, [new Row { Subscription = subscription, Plan = plan }], cancellationToken))[0];
        return new(outcome, record);
    }

    private static Task<bool> LockTenantAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken) =>
        TenantUsageEngine.LockTenantAsync(context, tenantId, cancellationToken);

    /// <summary>Null when the reference is absent or names an enabled policy; otherwise why the plan cannot use it.</summary>
    private static async Task<ManagedServiceOutcome?> CheckPolicyReferenceAsync(PlatformDbContext context, string? reference, CancellationToken cancellationToken)
    {
        if (reference is null)
        {
            return null;
        }
        var enabled = await context.QuotaPolicies.AsNoTracking().Where(policy => policy.Key == reference).Select(policy => (bool?)policy.Enabled).SingleOrDefaultAsync(cancellationToken);
        return enabled switch
        {
            null => ManagedServiceOutcome.QuotaPolicyNotFound,
            false => ManagedServiceOutcome.QuotaPolicyDisabled,
            _ => null
        };
    }

    private async Task RecalculateTenantsOfPlanAsync(PlatformDbContext context, PlanId planId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenants = await context.TenantSubscriptions.AsNoTracking()
            .Where(row => row.PlanId == planId && (row.Status == SubscriptionStatus.Active || row.Status == SubscriptionStatus.Suspended))
            .Select(row => row.TenantId).ToListAsync(cancellationToken);
        foreach (var tenantId in tenants)
        {
            await usage.ReconcileAsync(tenantId, null, time.GetUtcNow(), cancellationToken);
        }
    }

    private static Task<TenantSubscription?> LoadOpenAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken) =>
        context.TenantSubscriptions.SingleOrDefaultAsync(
            row => row.TenantId == tenantId && (row.Status == SubscriptionStatus.Active || row.Status == SubscriptionStatus.Suspended), cancellationToken);

    private static Task<int> CountOpenAsync(PlatformDbContext context, PlanId planId, CancellationToken cancellationToken) =>
        context.TenantSubscriptions.CountAsync(
            row => row.PlanId == planId && (row.Status == SubscriptionStatus.Active || row.Status == SubscriptionStatus.Suspended), cancellationToken);

    /// <summary>Member-initialized (not a constructor projection) so ordering over its members translates to SQL.</summary>
    private sealed class Row
    {
        public required TenantSubscription Subscription { get; init; }
        public required Plan Plan { get; init; }
    }

    private static IQueryable<Row> Rows(PlatformDbContext context, TenantId tenantId) =>
        from subscription in context.TenantSubscriptions.AsNoTracking()
        where subscription.TenantId == tenantId
        join plan in context.Plans.AsNoTracking() on subscription.PlanId equals plan.Id
        select new Row { Subscription = subscription, Plan = plan };

    private static async Task<List<SubscriptionRecord>> ToRecordsAsync(PlatformDbContext context, IReadOnlyList<Row> rows, CancellationToken cancellationToken)
    {
        var userIds = rows.Where(row => row.Subscription.AssignedByUserId is not null).Select(row => row.Subscription.AssignedByUserId!).Distinct().ToArray();
        var names = userIds.Length == 0 ? [] : await context.Users.AsNoTracking().Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken);
        return rows.Select(row => new SubscriptionRecord(
            row.Subscription, row.Plan, row.Subscription.AssignedByUserId is null ? null : names.GetValueOrDefault(row.Subscription.AssignedByUserId))).ToList();
    }

    private static PlanView ToView(Plan plan, int tenantCount) => new(
        plan.Id, plan.Name, plan.Enabled, plan.StorageLimitBytes, plan.QuotaPolicyReference, plan.PolicyConfigurationVersion,
        plan.IsSystem, plan.SystemKey, tenantCount, plan.CreatedAt, plan.UpdatedAt);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static string Id(PlanId id) => id.Value.ToString("D");

    private static string Id(SubscriptionId id) => id.Value.ToString("D");
}
