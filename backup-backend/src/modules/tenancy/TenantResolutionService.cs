using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Tenancy;

public sealed class TenantResolutionService(ICurrentUser currentUser, ICentralAuthorizationService authorizationService)
{
    public Task<TenantAccess?> ResolveAsync(TenantId requestedTenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedTenantId);
        cancellationToken.ThrowIfCancellationRequested();
        var userId = currentUser.UserId;
        return currentUser.IsAuthenticated && userId is not null
            ? authorizationService.EvaluateTenantAccessAsync(userId, requestedTenantId, cancellationToken)
            : Task.FromResult<TenantAccess?>(null);
    }
}
