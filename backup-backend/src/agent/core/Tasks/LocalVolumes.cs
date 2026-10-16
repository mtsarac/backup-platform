using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>
/// The local disks this agent's operator declared, by name. A task names a volume, never a path, so the operator's configuration is the only
/// thing that decides where the agent may write; an unknown name resolves to nothing. The empty name and <see cref="DefaultName"/> are the default local storage.
/// </summary>
public sealed class LocalVolumes(IStorageProvider? defaultStorage, IReadOnlyDictionary<string, IStorageProvider> volumes)
{
    public const string DefaultName = "local";

    public static LocalVolumes None { get; } = new(null, new Dictionary<string, IStorageProvider>());

    public IStorageProvider? Resolve(string? volume) =>
        string.IsNullOrEmpty(volume) || volume == DefaultName ? defaultStorage : volumes.GetValueOrDefault(volume);

    /// <summary>A volume name: one lowercase key segment (letters, digits, '-'), not the reserved default name.</summary>
    public static bool IsValidName(string? name) =>
        name is { Length: >= 1 and <= 32 } && name != DefaultName && name[0] != '-' && name[^1] != '-'
        && name.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
