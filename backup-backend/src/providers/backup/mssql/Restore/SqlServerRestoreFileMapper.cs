using System.Text;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Discovery;

namespace BackupPlatform.BackupProviders.MSSQL.Restore;

/// <summary>Everything the file mapping needs; the generator reads nothing else and performs no I/O.</summary>
internal sealed record SqlServerRestoreMappingInput(
    string DatabaseName,
    IReadOnlyList<RestoreBackupFile> Files,
    SqlServerTargetDirectories Directories,
    SqlServerHostPlatform HostPlatform,
    bool ReplaceExisting,
    IReadOnlyList<SqlServerMasterFile> TargetFiles,
    IReadOnlyList<SqlServerMasterFile> AllServerFiles);

/// <summary>The mapping, or a fixed user-safe sentence saying why there is none. <see cref="ReusedFiles"/> counts the files restored in place over the target's own files.</summary>
internal sealed record SqlServerRestoreMappingResult(IReadOnlyList<RestoreFileMapping>? Mappings, string? FailureMessage, int ReusedFiles)
{
    public bool Succeeded => Mappings is not null;
}

/// <summary>
/// Generates the destination of every logical file of a backup on the TARGET server. This is the safety-critical part of a restore:
/// <c>RESTORE</c> puts a file at its SOURCE path unless told otherwise, and the source server's directories mean nothing (or something
/// else) on the target. So every destination is built here and nowhere else, from three rules:
/// <list type="bullet">
/// <item>The directory is the target's own default data or log directory, in the path style of the target (<see cref="DatabaseServerPath.Combine"/>
/// uses the style of the directory itself, which is the server's).</item>
/// <item>The file name is <c>{database}_{logical}_{n}.{mdf|ndf|ldf}</c> with both names reduced to <c>[A-Za-z0-9_-]</c> and bounded, so nothing a
/// backup or a caller names can contain a separator, a dot-segment, a stream marker or a reserved device name. <c>n</c> is the first number for
/// which the path is not yet taken: not by another mapped file (sanitising can make different names equal), not by any file of any database
/// on the target (the engine refuses to overwrite those, errors 1834 / 3142), and never one of the backup's own SOURCE paths.</item>
/// <item>The one exception is replacing an existing database: a backup file with the same logical name and file type as a file the target
/// database already has is restored in place, over that file, so the replaced database ends up where it was. That path belongs to the
/// target database, not to the source.</item>
/// </list>
/// Only data and log files are restorable; FILESTREAM / memory-optimized containers and full-text catalogs are refused. Pure.
/// </summary>
internal static class SqlServerRestoreFileMapper
{
    public const int MaxNamePartLength = 40;

    // The destination must stay inside what every supported file system and SQL Server itself accept (Windows MAX_PATH is 260).
    private const int MaxPathLength = 255;

    private const int MaxCollisionAttempts = 1000;

    public static SqlServerRestoreMappingResult Map(SqlServerRestoreMappingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (RejectUnsupportedFiles(input.Files) is { } rejection)
        {
            return Fail(rejection);
        }

        if (StyleMismatch(input.Directories, input.HostPlatform))
        {
            return Fail("The default directories the server reports do not match the server's operating system, so no safe destination can be derived.");
        }

        // Taken: every file any database on the target owns (this includes the source database's files when the source and the target are the same
        // server) and every path the backup came from. Generated names avoid both; reuse (below) is the only way to land on an existing path.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in input.AllServerFiles)
        {
            taken.Add(file.PhysicalName);
        }

        foreach (var file in input.Files)
        {
            taken.Add(file.SourcePhysicalName);
        }

        var destinations = new string?[input.Files.Count];
        var reused = 0;
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (input.ReplaceExisting)
        {
            for (var index = 0; index < input.Files.Count; index++)
            {
                var file = input.Files[index];
                var existing = input.TargetFiles.FirstOrDefault(target =>
                    string.Equals(target.LogicalName, file.LogicalName, StringComparison.OrdinalIgnoreCase)
                    && target.Kind == file.Kind
                    && IsUsablePath(target.PhysicalName)
                    && !claimed.Contains(target.PhysicalName));
                if (existing is not null)
                {
                    destinations[index] = existing.PhysicalName;
                    claimed.Add(existing.PhysicalName);
                    reused++;
                }
            }
        }

        var databasePart = Sanitize(input.DatabaseName);
        var firstDataAssigned = false;
        for (var index = 0; index < input.Files.Count; index++)
        {
            var file = input.Files[index];
            var isFirstData = file.Kind == RestoreFileKind.Data && !firstDataAssigned;
            firstDataAssigned |= file.Kind == RestoreFileKind.Data;
            if (destinations[index] is not null)
            {
                continue;
            }

            var directory = file.Kind == RestoreFileKind.Log ? input.Directories.Log : input.Directories.Data;
            var extension = file.Kind == RestoreFileKind.Log ? "ldf" : isFirstData ? "mdf" : "ndf";
            var basePart = $"{databasePart}_{Sanitize(file.LogicalName)}";

            string? chosen = null;
            for (var attempt = 1; attempt <= MaxCollisionAttempts; attempt++)
            {
                var candidate = directory.Combine($"{basePart}_{attempt}.{extension}").Value;
                if (candidate.Length > MaxPathLength)
                {
                    return Fail("The server's default directory path is too long to place the restored files in it.");
                }

                if (!taken.Contains(candidate) && !claimed.Contains(candidate))
                {
                    chosen = candidate;
                    break;
                }
            }

            if (chosen is null)
            {
                return Fail("No unused file name could be found in the server's default directory.");
            }

            destinations[index] = chosen;
            claimed.Add(chosen);
        }

        var mappings = new RestoreFileMapping[input.Files.Count];
        for (var index = 0; index < mappings.Length; index++)
        {
            var file = input.Files[index];
            mappings[index] = new RestoreFileMapping(
                file.LogicalName, file.Kind, file.SourcePhysicalName, new DatabaseServerPath(destinations[index]!), file.SizeBytes, file.FileGroupName);
        }

        return new SqlServerRestoreMappingResult(mappings, null, reused);
    }

    /// <summary>Reduces a name to <c>[A-Za-z0-9_-]</c> (every other character, including every non-ASCII letter, becomes <c>_</c>) and bounds it to <see cref="MaxNamePartLength"/>.</summary>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var builder = new StringBuilder(Math.Min(name.Length, MaxNamePartLength));
        foreach (var character in name)
        {
            if (builder.Length == MaxNamePartLength)
            {
                break;
            }

            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_');
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }

    private static bool IsUsablePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Any(char.IsControl) && (new DatabaseServerPath(path).IsWindowsStyle || path.StartsWith('/'));

    // A Windows server must hand out drive/UNC directories and a Linux server POSIX ones; a host that is not known accepts either.
    private static bool StyleMismatch(SqlServerTargetDirectories directories, SqlServerHostPlatform platform) => platform switch
    {
        SqlServerHostPlatform.Windows => !directories.Data.IsWindowsStyle || !directories.Log.IsWindowsStyle,
        SqlServerHostPlatform.Linux => directories.Data.IsWindowsStyle || directories.Log.IsWindowsStyle,
        _ => false
    };

    private static string Describe(RestoreFileKind kind) => kind switch
    {
        RestoreFileKind.FileStream => "FILESTREAM or memory-optimized data",
        RestoreFileKind.FullText => "full-text catalogs",
        _ => "files of an unknown type"
    };

    private static SqlServerRestoreMappingResult Fail(string message) => new(null, message, 0);

    /// <summary>
    /// Why a backup's file list cannot be restored at all (empty, FILESTREAM / memory-optimized / full-text / unknown file types, duplicate logical names,
    /// no data file), as a fixed user-safe sentence; null when every file is a data or log file.
    /// </summary>
    public static string? RejectUnsupportedFiles(IReadOnlyList<RestoreBackupFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
        {
            return "The backup does not list any database file.";
        }

        var unsupported = files.Where(file => file.Kind is not (RestoreFileKind.Data or RestoreFileKind.Log)).Select(file => file.Kind).Distinct().ToArray();
        if (unsupported.Length > 0)
        {
            return "The backup contains files this version cannot restore: "
                + string.Join(", ", unsupported.Select(Describe))
                + ". Only data and log files are supported.";
        }

        if (files.Select(file => file.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
        {
            return "The backup lists the same logical file name more than once.";
        }

        return files.Any(file => file.Kind == RestoreFileKind.Data) ? null : "The backup contains no data file.";
    }
}
