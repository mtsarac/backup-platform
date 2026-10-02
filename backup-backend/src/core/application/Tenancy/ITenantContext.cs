using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Tenancy;

/// <summary>Tenant selected by a trusted application boundary, not by unvalidated client input.</summary>
/// <remarks>
/// Authentication and tenant resolution are not implemented here. A future resolver must verify
/// tenant access before supplying this context. Background jobs may supply an explicitly bound
/// tenant using the same contract. Null means unresolved, never platform-wide permission.
/// A persistence context captures this value for its entire lifetime; do not reuse it across tenants.
/// </remarks>
public interface ITenantContext
{
    TenantId? TenantId { get; }
}
