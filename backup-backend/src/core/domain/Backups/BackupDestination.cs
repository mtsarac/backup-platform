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
    /// <summary>The agent volume a local destination writes to (null: the default local storage). Resolved from the connection when a run is admitted.</summary>
    public string? StorageTarget { get; init; }

    public string? MediaClass { get; init; }
    public string? Site { get; init; }
    public bool OffsiteVerified { get; init; }

    public BackupStorageExecutionLocation StorageExecutionLocation { get; init; } =
        StorageProvider == StorageProviderTypes.Local ? BackupStorageExecutionLocation.Agent : BackupStorageExecutionLocation.Backend;
    /// <summary>True when the agent transfers directly to/from the storage; false when bytes are relayed through the platform backend.</summary>
    public bool Direct { get; init; } = StorageProvider == StorageProviderTypes.Local;
    public BackupDestination Validate()
    {
        BackupGuard.Reference(StorageProvider);
        if (StorageConnectionId == Guid.Empty)
            throw new ArgumentException("A storage connection identifier must not be empty.");
        if (StorageTarget is not null && (StorageProvider != StorageProviderTypes.Local || !StorageProviderTypes.IsValid(StorageTarget)))
            throw new ArgumentException("Invalid storage target for destination.");
        if (StorageExecutionLocation is not (BackupStorageExecutionLocation.Backend or BackupStorageExecutionLocation.Agent) ||
            StorageExecutionLocation == BackupStorageExecutionLocation.Agent && StorageProvider != StorageProviderTypes.Local)
            throw new ArgumentException("Invalid storage execution location for destination.");
        return this;
    }
}
