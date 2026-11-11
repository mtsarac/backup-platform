using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Tenancy;

public sealed class PostgreSqlCentralAuthorizationService(DbContextOptions<PlatformDbContext> options) : ICentralAuthorizationService
{
    public async Task<TenantAccess?> EvaluateTenantAccessAsync(UserId userId, TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        if (user.Role == UserRole.PlatformAdmin)
        {
            var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
            return tenant is null ? null : new TenantAccess(tenant.Id, tenant.Name);
        }

        return await (from membership in context.TenantMemberships.AsNoTracking()
                      join tenant in context.Tenants.AsNoTracking() on membership.TenantId equals tenant.Id
                      where membership.UserId == userId && membership.TenantId == tenantId
                      select new TenantAccess(tenant.Id, tenant.Name)).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> EvaluateContentAccessAsync(UserId userId, TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || user.Role == UserRole.PlatformAdmin)
        {
            return false;
        }

        return await (from membership in context.TenantMemberships.AsNoTracking()
                      join tenant in context.Tenants.AsNoTracking() on membership.TenantId equals tenant.Id
                      where membership.UserId == userId && membership.TenantId == tenantId
                      select membership).AnyAsync(cancellationToken);
    }

    public async Task<bool> IsPlatformAdminAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.Role == UserRole.PlatformAdmin, cancellationToken);
    }
}
