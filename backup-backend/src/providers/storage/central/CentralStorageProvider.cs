using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions.FileSystem;

namespace BackupPlatform.StorageProviders.Central;

/// <summary>Where the central provider stores objects, and for which tenant.</summary>
public sealed record CentralStorageOptions
{
    /// <exception cref="ArgumentException">The directory is not an absolute path, or the tenant scope is not a single valid lowercase key segment.</exception>
    public CentralStorageOptions(string rootDirectory, string tenantScope)
    {
        RootDirectory = FileSystemStorageRoot.Normalize(rootDirectory);
        ArgumentNullException.ThrowIfNull(tenantScope);
        if (tenantScope.Contains('/'))
        {
            throw new ArgumentException("The tenant scope must be a single storage key segment.", nameof(tenantScope));
        }

        if (tenantScope.Any(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException("The tenant scope must be lowercase so scopes stay distinct on case-insensitive file systems.", nameof(tenantScope));
        }

        TenantScope = new StorageObjectKey(tenantScope).Value;
    }

    /// <summary>Absolute, normalised path of the platform's central storage root. It must already exist when the provider is used.</summary>
    public string RootDirectory { get; }

    /// <summary>The one tenant this provider instance serves; one <see cref="StorageObjectKey"/> segment.</summary>
    public string TenantScope { get; }
}

/// <summary>
/// "Central" storage: the platform's central storage root, a directory on a file system the backend operator provides (the backend
/// host's disk, or a mounted or shared volume) which the agent can reach directly. This is a deliberate MVP decision: the MVP does
/// not stream Agent → Backend → Storage over HTTP. Such a relay would be a separate <see cref="IStorageProvider"/> with
/// <see cref="StorageTopology.BackendRelayed"/> behind the same contract, without changing callers.
/// <para>
/// What distinguishes it from the local provider (the agent host's own disk) is tenant binding: an instance serves exactly one
/// <see cref="CentralStorageOptions.TenantScope"/> and stores every key under <c>&lt;root&gt;/tenants/&lt;scope&gt;/</c>, so it cannot address another
/// tenant's objects by construction. Scopes are lowercase only, so two scopes can never share a directory on a case-insensitive file system.
/// </para>
/// </summary>
public sealed class CentralStorageProvider : FileSystemStorageProvider
{
    public const string TenantsDirectory = "tenants";

    public CentralStorageProvider(CentralStorageOptions options)
        : base(
            StorageProviderId.Central,
            StorageTopology.AgentDirect,
            (options ?? throw new ArgumentNullException(nameof(options))).RootDirectory,
            [TenantsDirectory, options.TenantScope])
    {
    }
}
