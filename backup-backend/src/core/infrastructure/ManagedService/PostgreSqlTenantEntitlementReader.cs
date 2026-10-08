using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService.Entitlement;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.ManagedService;

/// <summary>
/// Resolves one tenant's entitlement by explicit id under platform operations. The caller authorizes the question; this class
/// never widens a tenant-scoped context. Every rule (effective status, override over plan) is the domain's, not copied here.
/// </summary>
public sealed class PostgreSqlTenantEntitlementReader(DbContextOptions<PlatformDbContext> options) : ITenantEntitlementReader
{
    public async Task<TenantEntitlement> GetAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await ResolveAsync(context, tenantId, now, cancellationToken);
    }

    /// <summary>The same resolution on a context the caller already holds (inside its transaction), so quota code never repeats the M1 rules.</summary>
    internal static async Task<TenantEntitlement> ResolveAsync(PlatformDbContext context, TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = await context.TenantSubscriptions.AsNoTracking()
            .Where(row => row.TenantId == tenantId)
            .OrderBy(row => row.Status == SubscriptionStatus.Active || row.Status == SubscriptionStatus.Suspended ? 0 : 1)
            .ThenByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var plan = current is null ? null : await context.Plans.AsNoTracking().SingleAsync(row => row.Id == current.PlanId, cancellationToken);
        return TenantEntitlement.Resolve(tenantId, current, plan, now);
    }
}
