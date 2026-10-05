using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.BackupProviders.MSSQL.Backup;

/// <summary>
/// The only place that turns names and paths into T-SQL text. BACKUP and RESTORE take no parameters for database names or devices,
/// so safety comes from two rules applied to every value: identifiers go through <see cref="QuoteIdentifier"/> and string values
/// through <see cref="QuoteStringLiteral"/>, and both refuse control characters, which could truncate or reshape a statement
/// (NUL ends a string in some layers, CR/LF break a literal in logs and tooling). Pure functions, no I/O.
/// </summary>
internal static class SqlServerBackupCommandBuilder
{
    /// <summary>The longest <c>sysname</c> SQL Server accepts.</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>Media-set name recorded in msdb, so operators can recognise backups taken by this platform.</summary>
    public const string BackupSetName = "BackupPlatform full backup";

    /// <summary><c>[name]</c> with every closing bracket doubled.</summary>
    public static string QuoteIdentifier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Length > MaxIdentifierLength)
        {
            throw new ArgumentException($"An identifier is 1 to {MaxIdentifierLength} characters.", nameof(name));
        }

        RejectControlCharacters(name, nameof(name));
        return "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    /// <summary><c>N'value'</c> (always Unicode) with every single quote doubled.</summary>
    public static string QuoteStringLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RejectControlCharacters(value, nameof(value));
        return "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// A full backup of <paramref name="databaseName"/> into one new file. <c>INIT</c> because the staged file name is unique and an
    /// append to an existing media set must never happen. <c>NO_CHECKSUM</c> / <c>NO_COMPRESSION</c> are always spelled out when the
    /// option is not applied: the server-level defaults (<c>backup checksum default</c>, <c>backup compression default</c>) would
    /// otherwise turn them on silently and make the reported "applied" options untrue. The compression keyword is only emitted where
    /// the capability is known to be supported, so editions without the feature never see it.
    /// </summary>
    public static string BuildFullBackup(
        string databaseName,
        DatabaseServerPath serverFilePath,
        AppliedFullBackupOptions applied,
        bool compressionCapabilitySupported,
        bool stats)
    {
        ArgumentNullException.ThrowIfNull(serverFilePath);
        ArgumentNullException.ThrowIfNull(applied);

        var options = new List<string> { "INIT", "NAME = " + QuoteStringLiteral(BackupSetName) };
        if (applied.CopyOnly)
        {
            options.Add("COPY_ONLY");
        }

        options.Add(applied.Checksum ? "CHECKSUM" : "NO_CHECKSUM");

        if (applied.Compression)
        {
            options.Add("COMPRESSION");
        }
        else if (compressionCapabilitySupported)
        {
            options.Add("NO_COMPRESSION");
        }

        if (stats)
        {
            options.Add("STATS = 1");
        }

        return $"BACKUP DATABASE {QuoteIdentifier(databaseName)} TO DISK = {QuoteStringLiteral(serverFilePath.Value)} WITH {string.Join(", ", options)};";
    }

    /// <summary><c>WITH CHECKSUM</c> only when the backup was taken with checksums: asking for it on a backup without them is an error.</summary>
    public static string BuildVerifyOnly(DatabaseServerPath serverFilePath, bool checksumApplied)
    {
        ArgumentNullException.ThrowIfNull(serverFilePath);
        return $"RESTORE VERIFYONLY FROM DISK = {QuoteStringLiteral(serverFilePath.Value)}{(checksumApplied ? " WITH CHECKSUM" : string.Empty)};";
    }

    /// <summary>Creates a directory (and missing parents) with the SQL Server service account's own rights.</summary>
    public static string BuildCreateSubdirectory(DatabaseServerPath directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return $"EXEC master.sys.xp_create_subdir {QuoteStringLiteral(directory.Value)};";
    }

    private static void RejectControlCharacters(string value, string parameter)
    {
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("The value contains control characters.", parameter);
        }
    }
}
