using BackupPlatform.StorageProviders.Central;
using BackupPlatform.StorageProviders.Local;

namespace BackupPlatform.Api.Backups;

/// <summary>
/// The storages the BACKEND itself can reach (configuration section <c>Backups:Storage</c>): the only places retention may delete from and sweep.
/// Nothing is derived from a persisted replica or from a request; a replica whose storage is not listed here cannot be deleted by the backend.
/// Both parts are optional and a missing part simply means "not reachable from the backend".
/// </summary>
public sealed class BackupStorageOptions
{
    /// <summary>The platform's central storage root and which storage scope each tenant owns inside it.</summary>
    public BackupCentralStorageSettings? Central { get; set; }

    /// <summary>A directory the backend can see as the "local" storage (typically a mounted agent volume). Absent when the backend cannot reach agent-local disks.</summary>
    public BackupLocalStorageSettings? Local { get; set; }

    /// <summary>Every problem with the settings. Messages name the setting only; they never repeat a configured path, scope or key.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Local?.RootDirectory is { } localRoot && !Accepts(() => new LocalStorageOptions(localRoot)))
            problems.Add("Backups:Storage:Local:RootDirectory must be an absolute path.");
        if (Central is { } central)
        {
            var root = central.RootDirectory;
            var hasRoot = !string.IsNullOrWhiteSpace(root);
            if (central.TenantScopes.Count != 0 && !hasRoot)
                problems.Add("Backups:Storage:Central:RootDirectory is required when Backups:Storage:Central:TenantScopes is configured.");
            var tenants = new HashSet<Guid>();
            var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (tenantKey, scope) in central.TenantScopes)
            {
                if (!Guid.TryParse(tenantKey, out var tenant) || tenant == Guid.Empty)
                {
                    problems.Add("Backups:Storage:Central:TenantScopes has a key that is not a tenant id.");
                    continue;
                }
                if (!tenants.Add(tenant)) problems.Add("Backups:Storage:Central:TenantScopes names the same tenant more than once.");
                if (!hasRoot) continue;
                var text = scope ?? string.Empty;
                if (!Accepts(() => new CentralStorageOptions(root!, text)))
                    problems.Add("Backups:Storage:Central:TenantScopes has a scope that is not one lowercase storage key segment, or the root directory is not absolute.");
                // Two tenants on one scope would share a directory: the tenant binding of the central provider would be void.
                else if (!scopes.Add(text)) problems.Add("Backups:Storage:Central:TenantScopes gives the same scope to more than one tenant.");
            }
            if (central.TenantScopes.Count == 0 && hasRoot && !Accepts(() => new CentralStorageOptions(root!, "scope")))
                problems.Add("Backups:Storage:Central:RootDirectory must be an absolute path.");
        }
        return problems;
    }

    public void ThrowIfInvalid()
    {
        var problems = Validate();
        if (problems.Count != 0) throw new InvalidOperationException("Invalid backup storage options: " + string.Join(" ", problems));
    }

    private static bool Accepts(Action construct)
    {
        try { construct(); return true; }
        catch (ArgumentException) { return false; }
    }
}

public sealed class BackupCentralStorageSettings
{
    public string? RootDirectory { get; set; }

    /// <summary>Tenant id (GUID text) to the storage scope that tenant's backups live under. A tenant without an entry has no central storage the backend may touch.</summary>
    public Dictionary<string, string> TenantScopes { get; set; } = new();
}

public sealed class BackupLocalStorageSettings
{
    public string? RootDirectory { get; set; }
}
