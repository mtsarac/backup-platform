using BackupPlatform.Domain.Storage;

namespace BackupPlatform.Domain.Backups;

/// <summary>
/// A single storage destination for multi-storage replication.
/// Exactly one destination in a configuration is marked Primary.
/// </summary>
public sealed record BackupDestination(
    string StorageProvider,
    Guid? StorageConnectionId = null,
    bool IsPrimary = false)
{
    public BackupStorageExecutionLocation StorageExecutionLocation { get; init; } =
        StorageProvider == "local" ? BackupStorageExecutionLocation.Agent : BackupStorageExecutionLocation.Backend;

    public BackupDestination Validate()
    {
        BackupGuard.Reference(StorageProvider);
        if (StorageConnectionId == Guid.Empty)
            throw new ArgumentException("A storage connection identifier must not be empty.");
        if (StorageExecutionLocation is not (BackupStorageExecutionLocation.Backend or BackupStorageExecutionLocation.Agent) ||
            StorageExecutionLocation == BackupStorageExecutionLocation.Agent && StorageProvider != "local")
            throw new ArgumentException("Invalid storage execution location for destination.");
        return this;
    }
}
