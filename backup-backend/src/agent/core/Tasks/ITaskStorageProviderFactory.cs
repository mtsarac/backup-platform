using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>Creates a task-bound storage provider; direct providers obtain credentials separately under the task's live lease.</summary>
public interface ITaskStorageProviderFactory
{
    /// <summary>A provider bound to this attempt's lease AND to one snapshot destination; the backend authorizes the destination on every call.</summary>
    IStorageProvider CreateBackup(BackupTaskSnapshot task, BackupDestinationContract destination);
    IStorageProvider CreateRestore(RestoreTaskSnapshot task);
    /// <summary>A provider for one connection whose access the backend releases only to the live lease of a delete or repair operation.</summary>
    IStorageProvider CreateScoped(DirectStorageScope scope);
}

/// <param name="AccessPath">Backend route (relative) that answers a lease-fenced POST with the connection's access; the lease travels in the lease header.</param>
public sealed record DirectStorageScope(string AccessPath, Guid LeaseToken, Guid TenantId, Guid ConnectionId, string ProviderType);

/// <summary>Implemented by a scoped Google Drive provider: the object record it holds in memory after a put, which the backend saves as the replica's catalog entry.</summary>
public interface IScopedDriveRecordSource
{
    Task<TaskStorageCatalogRecord?> FindRecordAsync(StorageObjectKey key, CancellationToken cancellationToken);
}
