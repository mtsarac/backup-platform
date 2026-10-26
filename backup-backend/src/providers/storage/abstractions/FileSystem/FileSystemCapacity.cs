namespace BackupPlatform.StorageProviders.Abstractions.FileSystem;

public static class FileSystemCapacity
{
    public static (long TotalBytes, long UsedBytes, long FreeBytes) Query(string directory)
    {
        var full = FileSystemStorageRoot.Normalize(directory);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("The configured capacity target is unavailable.");
        var root = Path.GetPathRoot(full)!;
        var resolved = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, segment);
            resolved = new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? resolved;
        }
        full = Path.TrimEndingDirectorySeparator(resolved);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var drive = DriveInfo.GetDrives()
            .Where(d => full.Equals(Path.TrimEndingDirectorySeparator(d.RootDirectory.FullName), comparison)
                || full.StartsWith(Path.EndsInDirectorySeparator(d.RootDirectory.FullName)
                    ? d.RootDirectory.FullName : d.RootDirectory.FullName + Path.DirectorySeparatorChar, comparison))
            .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
        if (drive is null || !drive.IsReady) throw new IOException("The configured capacity filesystem is unavailable.");
        var total = drive.TotalSize;
        var free = drive.AvailableFreeSpace;
        return (total, total - free, free);
    }
}
