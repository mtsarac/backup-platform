using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>Creates a lease-bound HTTP provider, never a provider carrying storage credentials.</summary>
public interface ITaskStorageProviderFactory
{
    IStorageProvider CreateBackup(BackupTaskSnapshot task);
    IStorageProvider CreateRestore(RestoreTaskSnapshot task);
}
