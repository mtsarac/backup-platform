using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Central;
using BackupPlatform.StorageProviders.Local;

namespace BackupPlatform.Api.Backups;

/// <summary>
/// Maps a persisted replica location to a provider the operator configured in <see cref="BackupStorageOptions"/>, and nothing else. The
/// providers are built once from configuration; a persisted provider id, target or key is only ever compared against them, never turned into
/// a path, so a tampered row cannot make the backend touch anything it was not configured to reach.
/// <para>
/// A "local" storage lives on the agent's host. The backend can only delete from it when the operator mounted that disk and configured
/// <c>Backups:Storage:Local</c>; otherwise it is simply not configured, deleting such a replica fails with <c>storage_not_configured</c>
/// and is retried with backoff. Agent-mediated deletion of agent-local storage is a later capability.
/// </para>
/// </summary>
public sealed class ConfiguredBackupStorageResolver : IBackupStorageResolver
{
    private const string DefaultTarget = "default";

    private readonly IStorageProvider? _local;
    private readonly Dictionary<Guid, IStorageProvider> _central = [];

    public ConfiguredBackupStorageResolver(BackupStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ThrowIfInvalid();
        var managed = new List<BackupManagedStorage>();
        if (options.Local?.RootDirectory is { } localRoot && !string.IsNullOrWhiteSpace(localRoot))
        {
            _local = new LocalStorageProvider(new LocalStorageOptions(localRoot));
            managed.Add(new BackupManagedStorage("local", StorageProviderId.Local, null, _local));
        }
        if (options.Central is { RootDirectory: { } centralRoot } central && !string.IsNullOrWhiteSpace(centralRoot))
        {
            foreach (var (tenantKey, scope) in central.TenantScopes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var tenant = Guid.Parse(tenantKey);
                var provider = new CentralStorageProvider(new CentralStorageOptions(centralRoot, scope));
                _central[tenant] = provider;
                managed.Add(new BackupManagedStorage($"central:{tenant:N}", StorageProviderId.Central, new TenantId(tenant), provider));
            }
        }
        ManagedStorages = managed;
    }

    public IReadOnlyList<BackupManagedStorage> ManagedStorages { get; }

    public IStorageProvider? Resolve(TenantId tenantId, string storageProvider, string storageTarget)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (storageTarget != storageProvider && storageTarget != DefaultTarget) return null;
        return storageProvider switch
        {
            "local" => _local,
            "central" => _central.GetValueOrDefault(tenantId.Value),
            _ => null
        };
    }
}
