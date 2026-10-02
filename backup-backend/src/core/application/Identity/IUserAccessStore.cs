using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Identity;

/// <summary>Narrow global reads used by authenticated identity/tenant boundaries, never an unfiltered DbContext.</summary>
public interface IUserAccessStore
{
    Task<UserProfile?> GetUserAsync(UserId userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantAccess>> ListTenantsAsync(UserId userId, CancellationToken cancellationToken);
    Task<TenantAccess?> GetMembershipAsync(UserId userId, TenantId tenantId, CancellationToken cancellationToken);
    Task<bool> IsPlatformAdminAsync(UserId userId, CancellationToken cancellationToken);
}

public sealed record UserProfile(UserId UserId, string DisplayName);
