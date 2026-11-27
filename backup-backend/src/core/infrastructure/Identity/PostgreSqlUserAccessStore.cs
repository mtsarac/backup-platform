using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Identity;

/// <summary>Global reads constrained to the supplied authenticated user and requested membership.</summary>
public sealed class PostgreSqlUserAccessStore(DbContextOptions<PlatformDbContext> options) : IUserAccessStore
{
    public async Task<UserProfile?> GetUserAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
        return await context.Users.Where(user => user.Id == userId)
            .Select(user => new UserProfile(user.Id, user.DisplayName, user.Role)).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TenantAccess>> ListTenantsAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || user.Role == UserRole.PlatformAdmin) return [];
        return await (from membership in context.TenantMemberships
                      join tenant in context.Tenants on membership.TenantId equals tenant.Id
                      where membership.UserId == userId
                      orderby tenant.Name, tenant.Id
                      select new TenantAccess(tenant.Id, tenant.Name)).ToListAsync(cancellationToken);
    }

    public async Task<TenantAccess?> GetMembershipAsync(UserId userId, TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || user.Role == UserRole.PlatformAdmin) return null;
        return await (from membership in context.TenantMemberships
                      join tenant in context.Tenants on membership.TenantId equals tenant.Id
                      where membership.UserId == userId && membership.TenantId == tenantId
                      select new TenantAccess(tenant.Id, tenant.Name)).SingleOrDefaultAsync(cancellationToken);
    }
    public async Task<bool> IsPlatformAdminAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
        return await context.Users.AnyAsync(admin => admin.Id == userId && admin.Role == UserRole.PlatformAdmin, cancellationToken);
    }
}
