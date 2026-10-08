using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BackupPlatform.Infrastructure.ManagedService;

/// <summary>
/// Quota policy administration under explicit platform operations; the PlatformAdmin-only endpoints are the authorization boundary. A policy
/// change and its audit event commit together. The usage projections computed under the policy are then recomputed one tenant at a time (each in
/// its own short transaction); they are already recognised as stale by their recorded version, so a tenant read before its turn is recomputed
/// by that read rather than served an old figure.
/// </summary>
public sealed class PostgreSqlQuotaPolicyStore(
    DbContextOptions<PlatformDbContext> options, IStorageUsageService usage, TimeProvider time, ILogger<PostgreSqlQuotaPolicyStore> logger) : IQuotaPolicyStore
{
    private const int RecalculationLimit = 5_000;

    public async Task<IReadOnlyList<QuotaPolicyView>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var policies = await context.QuotaPolicies.AsNoTracking().ToListAsync(cancellationToken);
        return policies.OrderByDescending(policy => policy.IsDefault).ThenBy(policy => policy.NormalizedName, StringComparer.Ordinal).Select(QuotaPolicyView.From).ToList();
    }

    public async Task<ManagedServiceChange<QuotaPolicyChange>> CreateAsync(QuotaPolicy policy, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (await context.QuotaPolicies.AnyAsync(row => row.Key == policy.Key || row.NormalizedName == policy.NormalizedName, cancellationToken))
        {
            return new(ManagedServiceOutcome.QuotaPolicyKeyTaken);
        }
        context.QuotaPolicies.Add(policy);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, ManagedServiceAuditActions.QuotaPolicyCreated, null,
            ManagedServiceAuditActions.QuotaPolicyTargetType, policy.Id.Value.ToString("D"), Details(policy)));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new(ManagedServiceOutcome.QuotaPolicyKeyTaken);
        }
        return new(ManagedServiceOutcome.Changed, new QuotaPolicyChange(QuotaPolicyView.From(policy), 0));
    }

    public async Task<ManagedServiceChange<QuotaPolicyChange>> UpdateAsync(
        QuotaPolicyId id, QuotaPolicyValues values, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        QuotaPolicy policy;
        await using (var context = PlatformDbContext.CreateForPlatformOperations(options))
        {
            var loaded = await context.QuotaPolicies.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
            if (loaded is null)
            {
                return new(ManagedServiceOutcome.QuotaPolicyNotFound);
            }
            policy = loaded;
            var previous = Details(policy);
            bool changed;
            try
            {
                changed = policy.Update(values.Name, values.AccountingMode, values.IncludedStorageScopes, values.EnforcementMode,
                    values.WarningThresholdPercent, values.CriticalThresholdPercent, values.Enabled, now);
            }
            catch (InvalidManagedServiceRequestException exception) when (exception.Code == "default_policy_required")
            {
                return new(ManagedServiceOutcome.InvalidTransition, null, exception.Code);
            }
            if (!changed)
            {
                return new(ManagedServiceOutcome.Unchanged, new QuotaPolicyChange(QuotaPolicyView.From(policy), 0));
            }
            var details = Details(policy);
            foreach (var (key, value) in previous)
            {
                details["previous" + char.ToUpperInvariant(key[0]) + key[1..]] = value;
            }
            context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, ManagedServiceAuditActions.QuotaPolicyChanged, null,
                ManagedServiceAuditActions.QuotaPolicyTargetType, policy.Id.Value.ToString("D"), details));
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new(ManagedServiceOutcome.ConcurrentUpdate);
            }
        }
        var recalculated = await RecalculateAsync(policy, now, cancellationToken);
        return new(ManagedServiceOutcome.Changed, new QuotaPolicyChange(QuotaPolicyView.From(policy), recalculated));
    }

    /// <summary>Tenants that hold a projection under the policy, or whose current plan names it: the ones whose figure may now be wrong.</summary>
    private async Task<int> RecalculateAsync(QuotaPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<TenantId> tenants;
        await using (var context = PlatformDbContext.CreateForPlatformOperations(options))
        {
            var holding = context.TenantStorageUsages.AsNoTracking().Where(row => row.PolicyId == policy.Id).Select(row => row.TenantId);
            var naming = from subscription in context.TenantSubscriptions.AsNoTracking()
                         where subscription.Status == SubscriptionStatus.Active || subscription.Status == SubscriptionStatus.Suspended
                         join plan in context.Plans.AsNoTracking() on subscription.PlanId equals plan.Id
                         where plan.QuotaPolicyReference == policy.Key
                         select subscription.TenantId;
            tenants = await holding.Union(naming).Take(RecalculationLimit).ToListAsync(cancellationToken);
        }
        var done = 0;
        foreach (var tenantId in tenants)
        {
            try
            {
                await usage.ReconcileAsync(tenantId, null, time.GetUtcNow(), cancellationToken);
                done++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Recalculating storage usage for tenant {TenantId} after a change of quota policy {PolicyKey} failed; the next read or maintenance pass repeats it.", tenantId, policy.Key);
            }
        }
        logger.LogInformation("Quota policy {PolicyKey} changed to v{PolicyVersion}: recalculated {Count} tenant projections.", policy.Key, policy.Version, done);
        return done;
    }

    private static Dictionary<string, object?> Details(QuotaPolicy policy) => new()
    {
        ["key"] = policy.Key,
        ["name"] = policy.Name,
        ["version"] = policy.Version,
        ["accountingMode"] = policy.AccountingMode.ToString(),
        ["includedStorageScopes"] = policy.IncludedStorageScopes.ToJson(),
        ["enforcementMode"] = policy.EnforcementMode.ToString(),
        ["warningThresholdPercent"] = policy.WarningThresholdPercent,
        ["criticalThresholdPercent"] = policy.CriticalThresholdPercent,
        ["enabled"] = policy.Enabled
    };
}
