using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.PostgreSql.Tools;

/// <summary>Logical database dump, physical cluster copy with WAL fetch, or physical cluster copy with concurrent WAL streaming.</summary>
public enum PostgreSqlBackupMode { Logical, Physical, Streaming }

/// <summary>Agent-local native PostgreSQL tool configuration; null directory searches PATH.</summary>
public sealed record PostgreSqlProviderOptions(string? ClientToolsDirectory = null, PostgreSqlBackupMode BackupMode = PostgreSqlBackupMode.Logical);

internal enum PostgreSqlToolStatus
{
    Available,
    Missing,

    /// <summary>A file with the tool's name exists but did not run or did not report a PostgreSQL version.</summary>
    Unusable
}

/// <summary>A resolved client tool: its absolute path and the PostgreSQL version it reports (<c>pg_dump (PostgreSQL) 18.6 ...</c>).</summary>
internal sealed record PostgreSqlToolInfo(string Name, string Path, int Major, int Minor, string VersionText);

internal sealed record PostgreSqlToolResolution(PostgreSqlToolStatus Status, PostgreSqlToolInfo? Tool)
{
    public static PostgreSqlToolResolution Missing { get; } = new(PostgreSqlToolStatus.Missing, null);
    public static PostgreSqlToolResolution Unusable { get; } = new(PostgreSqlToolStatus.Unusable, null);
}

/// <summary>
/// Finds the client tools and reads their version. The tools are executed with <c>--version</c> only: nothing from a task ever selects a path.
/// A successful resolution is cached for the life of the agent; a failed one is not, so installing the tools needs no agent restart.
/// </summary>
internal sealed partial class PostgreSqlClientTools(PostgreSqlProviderOptions options, ILogger logger)
{
    public const string PgDump = "pg_dump";
    public const string PgRestore = "pg_restore";
    public const string PgBaseBackup = "pg_basebackup";
    public const string PgVerifyBackup = "pg_verifybackup";

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, PostgreSqlToolInfo> _resolved = new(StringComparer.Ordinal);

    public async Task<PostgreSqlToolResolution> ResolveAsync(string tool, CancellationToken cancellationToken)
    {
        if (_resolved.TryGetValue(tool, out var known))
        {
            return new PostgreSqlToolResolution(PostgreSqlToolStatus.Available, known);
        }

        var path = Locate(tool);
        if (path is null)
        {
            return PostgreSqlToolResolution.Missing;
        }

        string? version = null;
        try
        {
            var result = await PostgreSqlProcessRunner.RunAsync(
                new PostgreSqlProcessSpec(path, ["--version"], new Dictionary<string, string>(), VersionTimeout, PostgreSqlStdoutMode.Lines),
                onErrorLine: null,
                onOutputLine: line =>
                {
                    version ??= line;
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0 || !TryParseVersion(version, out var major, out var minor))
            {
                LogUnusable(logger, tool);
                return PostgreSqlToolResolution.Unusable;
            }

            var info = new PostgreSqlToolInfo(tool, path, major, minor, version!.Trim());
            _resolved[tool] = info;
            return new PostgreSqlToolResolution(PostgreSqlToolStatus.Available, info);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            LogUnusable(logger, tool);
            return PostgreSqlToolResolution.Unusable;
        }
    }

    private string? Locate(string tool)
    {
        var file = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        if (!string.IsNullOrWhiteSpace(options.ClientToolsDirectory))
        {
            if (!Path.IsPathFullyQualified(options.ClientToolsDirectory)) return null;
            var configured = Path.Combine(options.ClientToolsDirectory, file);
            return File.Exists(configured) ? configured : null;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), file);
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped, like the shell does.
            }
        }

        return null;
    }

    /// <summary>Parses <c>pg_dump (PostgreSQL) 18.6 (Debian ...)</c>, <c>... 17beta1</c> and <c>... 18devel</c>. Pure.</summary>
    internal static bool TryParseVersion(string? text, out int major, out int minor)
    {
        major = minor = 0;
        if (text is null)
        {
            return false;
        }

        var match = VersionPattern().Match(text);
        if (!match.Success || !int.TryParse(match.Groups["major"].Value, out major))
        {
            return false;
        }

        _ = int.TryParse(match.Groups["minor"].Value, out minor);
        return true;
    }

    [GeneratedRegex(@"\(PostgreSQL\)\s+(?<major>\d+)(?:\.(?<minor>\d+))?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionPattern();

    [LoggerMessage(EventId = 300, Level = LogLevel.Warning, Message = "The PostgreSQL client tool did not run or reported no version: Tool={Tool}")]
    private static partial void LogUnusable(ILogger logger, string tool);
}
