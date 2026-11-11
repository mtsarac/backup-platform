using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Tenancy;

public interface ICentralAuthorizationService
{
    Task<TenantAccess?> EvaluateTenantAccessAsync(UserId userId, TenantId tenantId, CancellationToken cancellationToken);
    Task<bool> EvaluateContentAccessAsync(UserId userId, TenantId tenantId, CancellationToken cancellationToken);
    Task<bool> IsPlatformAdminAsync(UserId userId, CancellationToken cancellationToken);
}
