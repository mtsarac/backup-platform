namespace BackupPlatform.StorageProviders.Abstractions.FileSystem;

/// <summary>
/// The one definition of a valid storage root directory, shared by the file-system providers and their options so that a bad
/// configuration fails the same way everywhere and as early as possible.
/// </summary>
public static class FileSystemStorageRoot
{
    /// <summary>
    /// Returns the normalised absolute form of <paramref name="rootDirectory"/> (no trailing separator, no <c>.</c>/<c>..</c> segments).
    /// A relative root is refused because it would silently depend on the process working directory. Whether the directory exists is
    /// deliberately not checked here: that is checked on every operation, because a share can disappear after start-up.
    /// </summary>
    /// <exception cref="ArgumentException">The value is empty, contains a NUL character or is not an absolute path. The message never echoes the value.</exception>
    public static string Normalize(string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        if (string.IsNullOrWhiteSpace(rootDirectory)
            || rootDirectory.Contains('\0')
            || !Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The storage root directory must be an absolute path.", nameof(rootDirectory));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
    }
}
