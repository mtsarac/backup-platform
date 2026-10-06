using BackupPlatform.Domain.Identity;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Application.Backups;

/// <summary>A storage the backend itself can reach and is allowed to sweep.</summary>
/// <param name="Name">A stable label for logs and reports (no path).</param>
/// <param name="TenantScope">The one tenant the storage serves, or null for a storage shared by all tenants. A swept file whose key names another tenant is not ours.</param>
public sealed record BackupManagedStorage(string Name, StorageProviderId ProviderId, TenantId? TenantScope, IStorageProvider Provider);

/// <summary>
/// The backend's own reach into storage. A replica's location is a PERSISTED reference (provider id, target, key); this maps it to a provider
/// the backend operator configured. A location the backend cannot reach resolves to nothing, and the delete then fails with a stable reason
/// instead of guessing a path. Only the host composes provider implementations.
/// </summary>
public interface IBackupStorageResolver
{
    /// <summary>The provider that serves the replica's location for that tenant, or null when the backend has no configured storage that can reach it.</summary>
    IStorageProvider? Resolve(TenantId tenantId, string storageProvider, string storageTarget);

    /// <summary>Every storage the sweeps may enumerate. Never includes a storage the operator did not configure for the backend.</summary>
    IReadOnlyList<BackupManagedStorage> ManagedStorages { get; }
}
