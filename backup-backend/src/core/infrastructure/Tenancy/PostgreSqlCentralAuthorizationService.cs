using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Tenancy;

public sealed class PostgreSqlCentralAuthorizationService(DbContextOptions<PlatformDbContext> options, TimeProvider? time = null) : ICentralAuthorizationService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    public async Task<TenantAccess?> EvaluateTenantAccessAsync(UserId userId, TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || user.IsDisabled)
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
        if (user is null || user.IsDisabled || user.Role == UserRole.PlatformAdmin)
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
        return await context.Users.AsNoTracking().AnyAsync(u => u.Id == userId && !u.IsManuallyDisabled && !u.IsSecurityDisabled && !u.IsNoMembershipDisabled && u.Role == UserRole.PlatformAdmin, cancellationToken);
    }
    public async Task<DownloadAuthorizationResult> EvaluateDownloadAccessAsync(UserId userId, TenantId tenantId, Guid artifactId, Guid? shareId, CancellationToken cancellationToken, bool allowExpiredShare = false)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || user.IsDisabled)
        {
            return new DownloadAuthorizationResult(false, "user_disabled");
        }

        if (user.Role == UserRole.UserAdmin)
        {
            var isMember = await (from membership in context.TenantMemberships.AsNoTracking()
                                  where membership.UserId == userId && membership.TenantId == tenantId
                                  select membership).AnyAsync(cancellationToken);
            if (!isMember)
            {
                return new DownloadAuthorizationResult(false, "forbidden");
            }
            return new DownloadAuthorizationResult(true, AuthorizedShareId: null, IsPlatformAdmin: false);
        }

        if (user.Role == UserRole.PlatformAdmin)
        {
            if (shareId is null || shareId == Guid.Empty)
            {
                return new DownloadAuthorizationResult(false, "share_missing", IsPlatformAdmin: true);
            }

            var share = await context.BackupShares.AsNoTracking().SingleOrDefaultAsync(s => s.Id == shareId.Value, cancellationToken);
            if (share is null)
            {
                return new DownloadAuthorizationResult(false, "share_not_found", IsPlatformAdmin: true);
            }

            if (share.TenantId != tenantId)
            {
                return new DownloadAuthorizationResult(false, "share_tenant_mismatch", IsPlatformAdmin: true);
            }

            if (share.ArtifactId != artifactId)
            {
                return new DownloadAuthorizationResult(false, "share_artifact_mismatch", IsPlatformAdmin: true);
            }

            if (share.RevokedAtUtc is not null)
            {
                return new DownloadAuthorizationResult(false, "share_revoked", IsPlatformAdmin: true);
            }

            var now = _time.GetUtcNow();
            if (!allowExpiredShare && share.ExpiresAtUtc <= now)
            {
                return new DownloadAuthorizationResult(false, "share_expired", IsPlatformAdmin: true);
            }

            return new DownloadAuthorizationResult(true, AuthorizedShareId: share.Id, IsPlatformAdmin: true);
        }

        return new DownloadAuthorizationResult(false, "unauthorized_role");
    }

    public async Task<bool> EvaluateAgentSessionAccessAsync(TenantId tenantId, Guid artifactId, Guid? shareId, UserId sessionUserId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (shareId is { } sid)
        {
            var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == sessionUserId, cancellationToken);
            if (user is null || user.Role != UserRole.PlatformAdmin)
            {
                return false;
            }
            var share = await context.BackupShares.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sid, cancellationToken);
            if (share is null || share.TenantId != tenantId || share.ArtifactId != artifactId || share.RevokedAtUtc is not null || share.ExpiresAtUtc <= _time.GetUtcNow())
            {
                return false;
            }
            return true;
        }

        return await EvaluateContentAccessAsync(sessionUserId, tenantId, cancellationToken);
    }
}
