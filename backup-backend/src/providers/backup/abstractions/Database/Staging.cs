namespace BackupPlatform.BackupProviders.Abstractions.Database;

/// <summary>
/// A path as seen by the agent process' own file system. It is a different type from <see cref="DatabaseServerPath"/> on purpose:
/// the database service and the agent are separate processes, possibly on separate hosts or in separate mount namespaces, and the
/// same directory is generally spelled differently on each side.
/// </summary>
public sealed record AgentPath
{
    public AgentPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Agent paths must not contain control characters.", nameof(value));
        }

        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("Agent paths must be absolute.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>How the staging directory seen by the database server relates to the one seen by the agent. Declared by the operator, never inferred.</summary>
public enum StagingMappingKind
{
    /// <summary>Agent and database server run on the same host and name the directory identically.</summary>
    SameHost = 1,

    /// <summary>
    /// The two sides spell the directory differently (a container volume, a mount point, a drive letter versus a UNC share, a
    /// share exported by a remote database host). The operator asserts both name the same storage.
    /// </summary>
    Mapped
}

/// <summary>
/// The staging directory expressed from both sides: where the database service writes the backup file and where the agent finds
/// the same file. The pair is an assertion; the pre-check proves it with a probe where the database login allows it and says so
/// explicitly where it cannot. When the agent cannot see the server's staging directory at all there is no valid
/// <see cref="StagingArea"/>: that topology (a remote SQL Server with no shared file system) is not supported by this model.
/// </summary>
public sealed record StagingArea
{
    private StagingArea(StagingMappingKind kind, DatabaseServerPath serverDirectory, AgentPath agentDirectory)
    {
        Kind = kind;
        ServerDirectory = serverDirectory;
        AgentDirectory = agentDirectory;
    }

    public StagingMappingKind Kind { get; }

    /// <summary>The directory in the database server's file system; the server's service account writes here.</summary>
    public DatabaseServerPath ServerDirectory { get; }

    /// <summary>The same directory in the agent's file system; the agent process reads, hashes and deletes here.</summary>
    public AgentPath AgentDirectory { get; }

    /// <summary>Agent and database server share the host and spell the directory the same way.</summary>
    public static StagingArea SameHost(string directory) =>
        new(StagingMappingKind.SameHost, new DatabaseServerPath(directory), new AgentPath(directory));

    /// <summary>The database server and the agent spell the same directory differently.</summary>
    public static StagingArea Mapped(DatabaseServerPath serverDirectory, AgentPath agentDirectory)
    {
        ArgumentNullException.ThrowIfNull(serverDirectory);
        ArgumentNullException.ThrowIfNull(agentDirectory);
        return new(StagingMappingKind.Mapped, serverDirectory, agentDirectory);
    }
}

/// <summary>What the agent process itself can do in the staging directory. Says nothing about the database service account.</summary>
public sealed record StagingAgentAccess(bool DirectoryExists, bool CanList, bool CanWrite, long? FreeBytes);

/// <summary>A regular file directly inside the staging directory, as listed by <see cref="AgentStagingDirectory.ListFiles"/>.</summary>
public sealed record StagingFileEntry(string Name, long SizeBytes, DateTimeOffset LastWriteUtc);

/// <summary>What <see cref="AgentStagingDirectory.TryDeleteIdleFile"/> did. Anything but <see cref="Deleted"/> and <see cref="NotFound"/> means the file is still there.</summary>
public enum StagingDeleteOutcome
{
    /// <summary>The file was idle and is gone.</summary>
    Deleted = 1,

    /// <summary>There was no such file (it may have been removed concurrently).</summary>
    NotFound,

    /// <summary>Another process holds the file open (the database service may still be writing it, or something is reading it). Left untouched.</summary>
    InUse,

    /// <summary>The operating system refused the agent access to the file or the directory. Left untouched.</summary>
    AccessDenied,

    /// <summary>The entry is not a plain file (a link or a directory) or the file system failed. Left untouched or in an unknown state.</summary>
    Failed
}

/// <summary>
/// The agent process' access to its side of a <see cref="StagingArea"/>: inspection, probing and file handling by simple file
/// names. Every operation is agent-side; none of them proves anything about the database server's access.
/// </summary>
public sealed class AgentStagingDirectory
{
    private readonly string _root;

    public AgentStagingDirectory(AgentPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = path;
        _root = System.IO.Path.GetFullPath(path.Value);
    }

    public AgentPath Path { get; }

    /// <summary>Whether the directory exists, can be listed, accepts a created-and-removed probe file, and how much space is free. Never throws.</summary>
    public StagingAgentAccess Inspect()
    {
        if (!Directory.Exists(_root))
        {
            return new StagingAgentAccess(false, false, false, null);
        }

        var canList = false;
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(_root).GetEnumerator();
            entries.MoveNext();
            canList = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        var canWrite = false;
        var probe = System.IO.Path.Combine(_root, $".bp-agent-probe-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            canWrite = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                canWrite = false;
            }
        }

        long? free = null;
        try
        {
            free = new DriveInfo(_root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return new StagingAgentAccess(true, canList, canWrite, free);
    }

    /// <summary>The full agent-side path of a file directly inside the staging directory. <paramref name="fileName"/> must be a simple name.</summary>
    public string FilePath(string fileName) => System.IO.Path.Combine(_root, RequireSimpleName(fileName));

    public bool FileExists(string fileName) => File.Exists(FilePath(fileName));

    /// <summary>The length of the file, or null when it does not exist or cannot be inspected.</summary>
    public long? FileLength(string fileName)
    {
        try
        {
            var info = new FileInfo(FilePath(fileName));
            return info.Exists ? info.Length : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when a file or directory of that simple name exists directly inside the staging directory.</summary>
    public bool EntryExists(string name)
    {
        var path = System.IO.Path.Combine(_root, RequireSimpleName(name));
        return File.Exists(path) || Directory.Exists(path);
    }

    /// <summary>Creates a small marker file with that simple name. Fails if it exists.</summary>
    public void CreateMarkerFile(string fileName)
    {
        using var stream = new FileStream(FilePath(fileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.WriteByte(0x42);
    }

    /// <summary>Deletes the file if present. True when it is gone afterwards; false when it could not be removed. Never throws.</summary>
    public bool TryDeleteFile(string fileName)
    {
        try
        {
            File.Delete(FilePath(fileName));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes an empty directory if present. True when it is gone afterwards. Never throws.</summary>
    public bool TryDeleteEmptyDirectory(string name)
    {
        try
        {
            var path = System.IO.Path.Combine(_root, RequireSimpleName(name));
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The regular files directly inside the staging directory. Directories, symbolic links and reparse points are skipped: they are
    /// never the platform's own staged backup files, and following a link out of the directory is exactly what a cleanup must not do.
    /// Throws the <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> of a directory that cannot be listed; the caller
    /// decides what that means.
    /// </summary>
    public IReadOnlyList<StagingFileEntry> ListFiles()
    {
        var files = new List<StagingFileEntry>();
        foreach (var info in new DirectoryInfo(_root).EnumerateFiles())
        {
            if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                continue;
            }

            files.Add(new StagingFileEntry(info.Name, info.Length, new DateTimeOffset(info.LastWriteTimeUtc)));
        }

        return files;
    }

    /// <summary>
    /// Deletes a file nobody has open. The file is first opened exclusively (no sharing) and released again, so a file another process
    /// still holds, such as one the database service is writing, is reported as <see cref="StagingDeleteOutcome.InUse"/> and never
    /// removed. Links and directories are refused. Never throws, except <see cref="ArgumentException"/> for a name that is not a simple name.
    /// </summary>
    public StagingDeleteOutcome TryDeleteIdleFile(string name)
    {
        var path = FilePath(name);
        try
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return StagingDeleteOutcome.NotFound;
            }

            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                return StagingDeleteOutcome.Failed;
            }

            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return StagingDeleteOutcome.NotFound;
            }
            catch (UnauthorizedAccessException)
            {
                return StagingDeleteOutcome.AccessDenied;
            }
            catch (IOException exception)
            {
                return IsSharingViolation(exception) ? StagingDeleteOutcome.InUse : StagingDeleteOutcome.Failed;
            }

            File.Delete(path);
            return StagingDeleteOutcome.Deleted;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return StagingDeleteOutcome.NotFound;
        }
        catch (UnauthorizedAccessException)
        {
            return StagingDeleteOutcome.AccessDenied;
        }
        catch (IOException exception)
        {
            return IsSharingViolation(exception) ? StagingDeleteOutcome.InUse : StagingDeleteOutcome.Failed;
        }
    }

    /// <summary>
    /// Windows reports a refused share as ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION. Elsewhere the only IOException that opening an
    /// existing regular file normally produces is the refusal of the advisory lock <see cref="FileShare.None"/> takes, and an
    /// unexplained refusal is safest treated the same way: the file stays.
    /// </summary>
    private static bool IsSharingViolation(IOException exception) =>
        !OperatingSystem.IsWindows() || (exception.HResult & 0xFFFF) is 32 or 33;

    private static string RequireSimpleName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".."
            || name.Any(char.IsControl)
            || name.IndexOfAny(['/', '\\', ':']) >= 0
            || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Staging file names are simple names without directory components.", nameof(name));
        }

        return name;
    }
}
