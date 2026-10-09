using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.BackupProviders.MSSQL.Restore;

/// <summary>
/// Decides which directories restored files go to on the TARGET server: the instance's default data and log directories
/// (<c>SERVERPROPERTY('InstanceDefaultDataPath' / 'InstanceDefaultLogPath')</c>, SQL Server 2012+), and where a server does not report them
/// the directory of master's own files, which every instance has. A directory is only accepted when it is an absolute path without control
/// characters or parent-directory segments; anything else counts as "not reported". Pure.
/// </summary>
internal static class SqlServerTargetDirectoryResolver
{
    /// <summary>The directories, or null when not even master's files give a usable one.</summary>
    public static SqlServerTargetDirectories? Resolve(SqlServerRawDirectories raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var dataFallback = false;
        var data = Usable(raw.DefaultData);
        if (data is null)
        {
            dataFallback = true;
            data = Usable(DirectoryOf(raw.MasterData));
        }

        if (data is null)
        {
            return null;
        }

        var logFallback = false;
        var log = Usable(raw.DefaultLog);
        if (log is null)
        {
            logFallback = true;
            log = Usable(DirectoryOf(raw.MasterLog)) ?? data;
        }

        return new SqlServerTargetDirectories(new DatabaseServerPath(data), new DatabaseServerPath(log), dataFallback, logFallback);
    }

    /// <summary>The directory part of a file path in either style; null when the path has no directory.</summary>
    public static string? DirectoryOf(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var index = filePath.LastIndexOfAny(['/', '\\']);
        if (index < 0)
        {
            return null;
        }

        // "/file" and "C:\file" are in the root: the root keeps its separator.
        return index == 0 || (index == 2 && filePath[1] == ':') ? filePath[..(index + 1)] : filePath[..index];
    }

    /// <summary>The directory as a normalized absolute path (no trailing separator except for a root), or null when it is not acceptable.</summary>
    public static string? Usable(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var value = directory.Trim();
        if (value.Any(char.IsControl) || value.Split('/', '\\').Contains(".."))
        {
            return null;
        }

        var absolute = new DatabaseServerPath(value).IsWindowsStyle || value.StartsWith('/');
        if (!absolute)
        {
            return null;
        }

        var trimmed = value.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? value[..1]
            : trimmed.Length == 2 && trimmed[1] == ':' ? value[..Math.Min(3, value.Length)]
            : trimmed;
    }
}
