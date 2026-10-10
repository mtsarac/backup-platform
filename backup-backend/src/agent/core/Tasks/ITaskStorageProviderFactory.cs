using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>Creates a lease-bound HTTP provider, never a provider carrying storage credentials.</summary>
public interface ITaskStorageProviderFactory
{
    /// <summary>A provider bound to this attempt's lease AND to one snapshot destination; the backend authorizes the destination on every call.</summary>
    IStorageProvider CreateBackup(BackupTaskSnapshot task, BackupDestinationContract destination);
    IStorageProvider CreateRestore(RestoreTaskSnapshot task);
}
