using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Domain.Tenancy;

public sealed class Tenant
{
    public Tenant(TenantId id, string name)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name.Trim();
    }

    public TenantId Id { get; }
    public string Name { get; private set; }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }
}
