using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions.FileSystem;

namespace BackupPlatform.StorageProviders.Local;

/// <summary>Where the local provider stores objects.</summary>
public sealed record LocalStorageOptions
{
    /// <exception cref="ArgumentException">The directory is not an absolute path.</exception>
    public LocalStorageOptions(string rootDirectory)
    {
        RootDirectory = FileSystemStorageRoot.Normalize(rootDirectory);
    }

    /// <summary>Absolute, normalised path of the directory that receives the objects. It must already exist when the provider is used.</summary>
    public string RootDirectory { get; }
}

/// <summary>
/// Storage on the agent host's own disk, or on a disk or share the operator mounted there: the data never leaves the agent's reach
/// and no other machine takes part in the write. All file handling is the shared <see cref="FileSystemStorageProvider"/> engine.
/// </summary>
public sealed class LocalStorageProvider : FileSystemStorageProvider
{
    public LocalStorageProvider(LocalStorageOptions options)
        : base(StorageProviderId.Local, StorageTopology.AgentDirect, (options ?? throw new ArgumentNullException(nameof(options))).RootDirectory)
    {
    }
}
