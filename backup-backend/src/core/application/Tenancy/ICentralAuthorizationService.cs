using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Tenancy;
public sealed record DownloadAuthorizationResult(
    bool Authorized,
    string? RefusalReason = null,
    Guid? AuthorizedShareId = null,
    bool IsPlatformAdmin = false);


public interface ICentralAuthorizationService
{
    Task<TenantAccess?> EvaluateTenantAccessAsync(UserId userId, TenantId tenantId, CancellationToken cancellationToken);
    Task<bool> EvaluateContentAccessAsync(UserId userId, TenantId tenantId, CancellationToken cancellationToken);
    Task<bool> IsPlatformAdminAsync(UserId userId, CancellationToken cancellationToken);
    Task<DownloadAuthorizationResult> EvaluateDownloadAccessAsync(UserId userId, TenantId tenantId, Guid artifactId, Guid? shareId, CancellationToken cancellationToken, bool allowExpiredShare = false);
    Task<bool> EvaluateAgentSessionAccessAsync(TenantId tenantId, Guid artifactId, Guid? shareId, UserId sessionUserId, CancellationToken cancellationToken);
}
