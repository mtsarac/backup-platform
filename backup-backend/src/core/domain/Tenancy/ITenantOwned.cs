using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Domain.Tenancy;

/// <summary>Marks tenant ownership; query/application boundaries must still enforce isolation.</summary>
public interface ITenantOwned
{
    TenantId TenantId { get; }
}
