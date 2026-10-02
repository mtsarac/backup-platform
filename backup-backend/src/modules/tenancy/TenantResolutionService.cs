using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Tenancy;

public sealed class TenantResolutionService(ICurrentUser currentUser, IUserAccessStore userAccessStore)
{
    public Task<TenantAccess?> ResolveAsync(TenantId requestedTenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedTenantId);
        cancellationToken.ThrowIfCancellationRequested();
        var userId = currentUser.UserId;
        return currentUser.IsAuthenticated && userId is not null
            ? userAccessStore.GetMembershipAsync(userId, requestedTenantId, cancellationToken)
            : Task.FromResult<TenantAccess?>(null);
    }
}
