using System.Globalization;
using System.Text.RegularExpressions;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql.Tools;

namespace BackupPlatform.BackupProviders.PostgreSql.Restore;

/// <summary>Only custom archives are accepted. SQL generation reads and decompresses every data block without executing it.</summary>
internal sealed partial class PostgreSqlArchive
{
    internal const string DatabaseSqlKey = "postgres.database-sql";
    internal const string DumpMajorKey = "postgres.dump-major";
    internal const string ExtensionsKey = "postgres.extensions";
    private const int MaximumMetadataCharacters = 4 * 1024 * 1024;
    private readonly Dictionary<string, string> _details = new(StringComparer.Ordinal);
    private readonly HashSet<string> _extensions = new(StringComparer.Ordinal);
    private readonly List<string> _tocEntries = [];
    private readonly List<string> _databaseSql = [];
    private string? _database;
    private Version? _sourceVersion;
    private int _dumpMajor;
    private int _metadataCharacters;
    private bool _limitExceeded;
    private bool _hasSubscriptions;
    private bool _compressed;

    public static async Task<RestoreBackupInspection> ReadAsync(string path, PostgreSqlToolInfo tool, bool verify, TimeSpan timeout, TimeProvider clock, CancellationToken token)
    {
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var magic = new byte[5];
            if (await stream.ReadAsync(magic, token).ConfigureAwait(false) != 5 || !magic.AsSpan().SequenceEqual("PGDMP"u8))
                throw Failure(PostgreSqlReasons.ArchiveNotPostgreSql, "The file is not a PostgreSQL custom-format archive.");
        }

        var metadata = new PostgreSqlArchive();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            await RunAsync(tool, ["--list", "--create", "--", path], timeout, PostgreSqlStdoutMode.Lines, metadata.ReadToc, deadline.Token).ConfigureAwait(false);
            if (metadata._hasSubscriptions)
                throw Failure(RestoreReasonCodes.BackupTypeUnsupported, "Archives containing logical replication subscriptions are not supported.", BackupFailureCause.Unsupported);
            if (metadata._database is null || metadata._sourceVersion is null || metadata._dumpMajor == 0 || metadata._limitExceeded)
                throw Failure(PostgreSqlReasons.ArchiveInvalid, "The archive metadata is missing or exceeds the inspection limit.");
            if (tool.Major < metadata._dumpMajor)
                throw Failure(PostgreSqlReasons.ArchiveVersionUnsupported, "Install pg_restore at least as new as the pg_dump that wrote the archive.");

            // Select actual TOC objects before SQL generation: function bodies and object comments are not metadata to execute.
            var selection = Path.GetTempFileName();
            try
            {
                await File.WriteAllLinesAsync(selection, metadata._tocEntries, deadline.Token).ConfigureAwait(false);
                await RunAsync(tool, ["--schema-only", "--create", "--use-list", selection, "--file=-", "--", path], timeout,
                    PostgreSqlStdoutMode.Lines, metadata.ReadSchema, deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(selection); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Harmless selection metadata, never archive bytes. */ }
            }
            if (metadata._limitExceeded || metadata._databaseSql.Count == 0 || !metadata._databaseSql[0].StartsWith("CREATE DATABASE ", StringComparison.Ordinal))
                throw Failure(PostgreSqlReasons.ArchiveInvalid, "The archive database metadata cannot be safely interpreted.");
            var started = verify ? clock.GetUtcNow() : (DateTimeOffset?)null;
            if (verify)
                await RunAsync(tool, ["--file=-", "--", path], timeout, PostgreSqlStdoutMode.Discard, null, deadline.Token).ConfigureAwait(false);

            metadata._details[DatabaseSqlKey] = string.Join('\n', metadata._databaseSql);
            metadata._details[DumpMajorKey] = metadata._dumpMajor.ToString(CultureInfo.InvariantCulture);
            metadata._details[ExtensionsKey] = System.Text.Json.JsonSerializer.Serialize(metadata._extensions);
            return new RestoreBackupInspection(new RestoreBackupHeader(metadata._database, RestoreBackupKind.Database, metadata._sourceVersion,
                null, null, null, null, null, 1, new FileInfo(path).Length, true, false, metadata._compressed, false, null), [], verify,
                started, verify ? clock.GetUtcNow() : null, metadata._details);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw Failure(PostgreSqlReasons.VerifyTimeout, "Archive inspection exceeded its time limit.", BackupFailureCause.Timeout);
        }
    }

    private bool ReadToc(string line)
    {
        if (line.Length > 65536) { _limitExceeded = true; return false; }
        if (MetadataTocEntry().IsMatch(line))
        {
            _metadataCharacters += line.Length;
            if (_metadataCharacters > MaximumMetadataCharacters) { _limitExceeded = true; return false; }
            _tocEntries.Add(line);
        }
        if (line.StartsWith(";     dbname: ", StringComparison.Ordinal)) _database = line[(line.IndexOf(':') + 1)..].Trim();
        if (line.StartsWith(";     Dumped from database version: ", StringComparison.Ordinal)) _sourceVersion = ParseVersion(line[(line.IndexOf(':') + 1)..]);
        if (line.StartsWith(";     Dumped by pg_dump version: ", StringComparison.Ordinal)) _dumpMajor = ParseVersion(line[(line.IndexOf(':') + 1)..])?.Major ?? 0;
        if (line.StartsWith(";     Compression: ", StringComparison.Ordinal))
        {
            var compression = line[(line.IndexOf(':') + 1)..].Trim();
            _compressed = compression is not ("0" or "none" or "None");
        }
        if (!line.StartsWith(';') && line.Contains(" SUBSCRIPTION ", StringComparison.Ordinal)) _hasSubscriptions = true;
        return true;
    }

    private bool ReadSchema(string line)
    {
        if (line.Length > 65536) { _limitExceeded = true; return false; }
        var matches = SqlToken().Matches(line);
        var tokens = matches.Select(match => match.Value).ToArray();
        if (tokens.Length == 0) return true;
        if (line.StartsWith("CREATE EXTENSION IF NOT EXISTS ", StringComparison.Ordinal) && tokens.Length >= 6)
        {
            if (!IsCompleteStatement(line, matches, tokens)) { _limitExceeded = true; return false; }
            _extensions.Add(Unquote(tokens[5]));
        }

        var databaseIndex = tokens.Length >= 3 && (tokens[0] is "CREATE" or "ALTER") && tokens[1] == "DATABASE" ? 2
            : tokens.Length >= 4 && tokens[0] == "COMMENT" && tokens[1] == "ON" && tokens[2] == "DATABASE" ? 3
            : tokens.Length >= 5 && (tokens[0] is "GRANT" or "REVOKE") ? Array.IndexOf(tokens, "DATABASE") + 1 : -1;
        if (databaseIndex > 0 && databaseIndex < tokens.Length && Unquote(tokens[databaseIndex]) == _database)
        {
            if (!IsCompleteStatement(line, matches, tokens)) { _limitExceeded = true; return false; }
            _metadataCharacters += line.Length;
            if (_metadataCharacters > MaximumMetadataCharacters) { _limitExceeded = true; return false; }
            _databaseSql.Add(line);
        }
        return true;
    }

    private static bool IsCompleteStatement(string line, MatchCollection matches, string[] tokens)
    {
        if (tokens[^1] != ";" || tokens.Count(token => token == ";") != 1) return false;
        var end = 0;
        foreach (Match match in matches)
        {
            if (!string.IsNullOrWhiteSpace(line[end..match.Index])) return false;
            end = match.Index + match.Length;
        }
        return string.IsNullOrWhiteSpace(line[end..]);
    }

    public static string RewriteDatabaseStatement(string statement, string source, string target)
    {
        var matches = SqlToken().Matches(statement);
        for (var index = 0; index + 1 < matches.Count; index++)
        {
            if (matches[index].Value != "DATABASE" || Unquote(matches[index + 1].Value) != source) continue;
            var name = matches[index + 1];
            return statement[..name.Index] + PostgreSqlIdentifiers.Quote(target) + statement[(name.Index + name.Length)..];
        }
        throw Failure(PostgreSqlReasons.ArchiveInvalid, "The archive database statement cannot be safely interpreted.");
    }

    /// <summary>
    /// The archive's <c>CREATE DATABASE</c> under a verification name, without <c>OWNER</c> and <c>TABLESPACE</c>: a verification keeps neither owners nor ACLs, and
    /// the login that creates it (CREATEDB only) may not be allowed to assign another owner or create in the archive's tablespace. Encoding, locale and
    /// template stay exactly as archived, so collation-dependent data restores identically.
    /// </summary>
    public static string RewriteVerificationCreateStatement(string statement, string source, string target) =>
        DropOption(DropOption(RewriteDatabaseStatement(statement, source, target), "OWNER"), "TABLESPACE");

    private static string DropOption(string statement, string option)
    {
        var matches = SqlToken().Matches(statement);
        for (var index = 0; index + 2 < matches.Count; index++)
        {
            if (matches[index].Value != option || matches[index + 1].Value != "=") continue;
            var last = matches[index + 2];
            return statement[..matches[index].Index] + statement[(last.Index + last.Length)..];
        }
        return statement;
    }

    private static string Unquote(string value) => value.StartsWith('"') && value.EndsWith('"')
        ? value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : value;

    private static Version? ParseVersion(string text)
    {
        var first = text.Trim().Split(' ', 2)[0];
        var match = NumericVersion().Match(first);
        return match.Success && int.TryParse(match.Groups[1].Value, out var major)
            ? new Version(major, int.TryParse(match.Groups[2].Value, out var minor) ? minor : 0) : null;
    }

    private static async Task RunAsync(PostgreSqlToolInfo tool, string[] arguments, TimeSpan timeout, PostgreSqlStdoutMode mode, Func<string, bool>? line, CancellationToken token)
    {
        var result = await PostgreSqlProcessRunner.RunAsync(new PostgreSqlProcessSpec(tool.Path, arguments,
            new Dictionary<string, string> { ["LC_ALL"] = "C" }, timeout, mode), null, line, token).ConfigureAwait(false);
        if (result.ExitCode == 0 && !result.StoppedEarly) return;
        if (result.TimedOut) throw Failure(PostgreSqlReasons.VerifyTimeout, "Archive inspection exceeded its time limit.", BackupFailureCause.Timeout);
        var failure = PostgreSqlToolFailureClassifier.Classify(PostgreSqlToolKind.Verify, result.ErrorTail);
        throw Failure(failure.ReasonCode, failure.Message, failure.Cause);
    }

    internal static PostgreSqlRestoreException Failure(string code, string message, BackupFailureCause cause = BackupFailureCause.Unclassified) => new(code, message, cause);

    [GeneratedRegex("\"(?:[^\"]|\"\")*\"|'(?:[^']|'')*'|[^\\s;=,'\"]+|[=;,]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SqlToken();
    [GeneratedRegex(@"^(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumericVersion();
    [GeneratedRegex(@"^\d+;\s+\d+\s+\d+\s+(?:DATABASE(?: PROPERTIES)? - |(?:ACL|COMMENT) - DATABASE |EXTENSION - )", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MetadataTocEntry();
}

internal sealed class PostgreSqlRestoreException(string reasonCode, string message, BackupFailureCause cause) : Exception(message)
{
    public string ReasonCode { get; } = reasonCode;
    public BackupFailureCause Cause { get; } = cause;
}
