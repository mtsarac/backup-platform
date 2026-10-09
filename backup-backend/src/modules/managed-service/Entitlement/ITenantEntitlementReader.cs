using BackupPlatform.Domain.Identity;

namespace BackupPlatform.ManagedService.Entitlement;

/// <summary>
/// The narrow read the quota/admission slice will use. Implementations read the tenant explicitly by id; deciding that the
/// caller may ask is the caller's responsibility. Not consulted by backup orchestration in M1.
/// </summary>
public interface ITenantEntitlementReader
{
    Task<TenantEntitlement> GetAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken);
}
