using System.Globalization;
using BackupPlatform.BackupProviders.Abstractions.Database;
using Microsoft.Data.SqlClient;

namespace BackupPlatform.BackupProviders.MSSQL.Restore;

/// <summary>
/// The boundary between the result sets of <c>RESTORE HEADERONLY</c> / <c>RESTORE FILELISTONLY</c> and the typed restore models. A row is a
/// case-insensitive column-name to value dictionary, so nothing downstream sees a reader or a table and a test can build a row by hand.
/// Columns are read BY NAME and every one is optional: the column set differs between SQL Server versions (HEADERONLY grew
/// <c>KeyAlgorithm</c>, <c>Containment</c>, <c>CompressionAlgorithm</c>, <c>TimeZone</c>... over the years), and a column that this server does not
/// send is "unknown", never an error. Only a row that cannot describe a backup set at all (no database name, no backup type) is rejected.
/// Pure functions, no I/O.
/// </summary>
internal static class SqlServerBackupMetadataMapper
{
    /// <summary>One result-set row as a case-insensitive name to value map; <see cref="DBNull"/> becomes null.</summary>
    public static IReadOnlyDictionary<string, object?> ReadRow(SqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            row[reader.GetName(ordinal)] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
        }

        return row;
    }

    /// <summary>
    /// The header of one backup set, or null when the row is not a usable backup set. Header dates retain the source server's wall clock:
    /// a cross-instance target's current timezone cannot locate those historical dates on the UTC timeline.
    /// </summary>
    public static RestoreBackupHeader? MapHeader(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var databaseName = Text(row, "DatabaseName");
        if (string.IsNullOrWhiteSpace(databaseName) || Integer(row, "BackupType") is not { } type)
        {
            return null;
        }

        Version? version = Integer(row, "SoftwareVersionMajor") is { } major and >= 0
            && Integer(row, "SoftwareVersionMinor") is { } minor and >= 0
            && Integer(row, "SoftwareVersionBuild") is { } build and >= 0
                ? new Version(major, minor, build)
                : null;

        return new RestoreBackupHeader(
            DatabaseName: databaseName,
            Kind: MapKind(type),
            SourceServerVersion: version,
            SourceServerName: Text(row, "ServerName"),
            CompatibilityLevel: Integer(row, "CompatibilityLevel"),
            Collation: Text(row, "Collation"),
            BackupStartedAt: Moment(row, "BackupStartDate"),
            BackupFinishedAt: Moment(row, "BackupFinishDate"),
            Position: Integer(row, "Position") ?? 1,
            BackupSizeBytes: Bytes(row, "BackupSize") ?? 0,
            IsCopyOnly: Flag(row, "IsCopyOnly"),
            HasChecksums: Flag(row, "HasBackupChecksums"),
            IsCompressed: Flag(row, "Compressed") || Flag(row, "IsCompressed"),
            IsEncrypted: IsEncrypted(row),
            FamilyGuid: GuidValue(row, "FamilyGUID"));
    }

    /// <summary>One logical file, or null when the row has no logical name.</summary>
    public static RestoreBackupFile? MapFile(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var logicalName = Text(row, "LogicalName");
        if (string.IsNullOrEmpty(logicalName))
        {
            return null;
        }

        return new RestoreBackupFile(
            logicalName,
            Text(row, "PhysicalName") ?? string.Empty,
            MapFileKind(Text(row, "Type")),
            Text(row, "FileGroupName"),
            Bytes(row, "Size") ?? 0);
    }

    /// <summary>HEADERONLY <c>BackupType</c>: 1 database, 2 transaction log, 5 differential database; file (4), differential file (6), partial (7) and differential partial (8) are not interpreted.</summary>
    public static RestoreBackupKind MapKind(int code) => code switch
    {
        1 => RestoreBackupKind.Database,
        2 => RestoreBackupKind.TransactionLog,
        5 => RestoreBackupKind.Differential,
        _ => RestoreBackupKind.Other
    };

    /// <summary>FILELISTONLY <c>Type</c>: D data, L log, F full-text catalog, S FILESTREAM container or memory-optimized data.</summary>
    public static RestoreFileKind MapFileKind(string? type) => type?.Trim().ToUpperInvariant() switch
    {
        "D" => RestoreFileKind.Data,
        "L" => RestoreFileKind.Log,
        "F" => RestoreFileKind.FullText,
        "S" => RestoreFileKind.FileStream,
        _ => RestoreFileKind.Other
    };

    // A backup written with BACKUP ... WITH ENCRYPTION names its algorithm and encryptor in the header (2014+). A TDE database's backup has
    // none of them: it is encrypted by the database itself and carries no header marker.
    private static bool IsEncrypted(IReadOnlyDictionary<string, object?> row) =>
        Text(row, "KeyAlgorithm") is { Length: > 0 } algorithm && !string.Equals(algorithm, "NO_Encryption", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(Text(row, "EncryptorType"))
        || row.TryGetValue("EncryptorThumbprint", out var thumbprint) && thumbprint is byte[] { Length: > 0 };

    private static string? Text(IReadOnlyDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim()
            : null;

    private static int? Integer(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var value) || value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            return null;
        }
    }

    // numeric(20,0) arrives as decimal; anything beyond long is clamped, a negative size cannot be real.
    private static long? Bytes(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var value) || value is null)
        {
            return null;
        }

        try
        {
            var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return number <= 0 ? 0 : number >= long.MaxValue ? long.MaxValue : (long)number;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            return null;
        }
    }

    private static bool Flag(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var value) || value is null)
        {
            return false;
        }

        try
        {
            return value is bool flag ? flag : Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            return false;
        }
    }

    private static Guid? GuidValue(IReadOnlyDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is Guid guid && guid != Guid.Empty ? guid : null;

    private static DateTime? Moment(IReadOnlyDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is DateTime local
            ? DateTime.SpecifyKind(local, DateTimeKind.Unspecified)
            : null;
}
