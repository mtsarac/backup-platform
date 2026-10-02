using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Application.Tenancy;

/// <summary>Membership data read from the authoritative store, not supplied by a client.</summary>
public sealed record TenantAccess(TenantId TenantId, string Name, TenantRole Role);
