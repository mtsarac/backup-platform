using System.Data;
using System.Globalization;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using Microsoft.Data.SqlClient;

namespace BackupPlatform.BackupProviders.MSSQL.Restore;

/// <summary>The directory facts exactly as the server reported them: the instance defaults (2012+) and the directories of master's own files, the fallback.</summary>
internal sealed record SqlServerRawDirectories(string? DefaultData, string? DefaultLog, string? MasterData, string? MasterLog);

/// <summary>Where restored files go on the target. A <c>*IsFallback</c> flag says the directory is a derived guess (master's directory), not the instance's declared default.</summary>
internal sealed record SqlServerTargetDirectories(DatabaseServerPath Data, DatabaseServerPath Log, bool DataIsFallback, bool LogIsFallback);

/// <summary>One row of <c>sys.master_files</c>: a file some database on the target already owns. <see cref="DatabaseName"/> is null when the name cannot be resolved.</summary>
internal sealed record SqlServerMasterFile(string? DatabaseName, int DatabaseId, int FileId, RestoreFileKind Kind, string LogicalName, string PhysicalName, long SizeBytes);

/// <summary>Free and total space of the volume that holds <see cref="FilePath"/>, as <c>sys.dm_os_volume_stats</c> reports it.</summary>
internal sealed record SqlServerVolume(string FilePath, long AvailableBytes, long TotalBytes);

/// <summary>A database as the catalog shows it right now. <see cref="DatabaseId"/> and <see cref="CreateDate"/> are its identity: a restore that replaces it changes the creation date unless it restores the same database.</summary>
internal sealed record SqlServerObservedDatabase(int DatabaseId, DateTime CreateDate, SqlServerDatabaseState State, SqlServerUserAccess UserAccess, string? StateDescription);

/// <summary>
/// Reads what the restore needs to know about the target server over an open session. Every statement is constant text; the only
/// caller-supplied value (a database name) is always a parameter. Each method throws the session's
/// <see cref="DatabaseProviderException"/> when the server refuses the query; callers that can live with "unknown" use
/// <see cref="OptionalAsync{T}"/>.
/// </summary>
internal static class SqlServerRestoreTargetReader
{
    private const string DirectoriesSql = """
        SELECT CONVERT(nvarchar(512), SERVERPROPERTY(N'InstanceDefaultDataPath')),
               CONVERT(nvarchar(512), SERVERPROPERTY(N'InstanceDefaultLogPath')),
               (SELECT TOP (1) physical_name FROM master.sys.database_files WHERE type = 0 ORDER BY file_id),
               (SELECT TOP (1) physical_name FROM master.sys.database_files WHERE type = 1 ORDER BY file_id);
        """;

    private const string ObserveSql = """
        SELECT d.database_id, d.create_date, d.state, d.user_access, d.state_desc
        FROM sys.databases AS d
        WHERE d.name = @name;
        """;

    private const string FamilySql = """
        SELECT rs.family_guid
        FROM sys.databases AS d
        JOIN sys.database_recovery_status AS rs ON rs.database_id = d.database_id
        WHERE d.name = @name;
        """;

    // Other sessions that currently have the database as their context. Needs VIEW SERVER STATE: without it the DMV shows only the caller's own session.
    private const string SessionsSql = """
        SELECT COUNT_BIG(*)
        FROM sys.dm_exec_sessions
        WHERE database_id = DB_ID(@name) AND is_user_process = 1 AND session_id <> @@SPID;
        """;

    private const string AvailabilityGroupSql = "SELECT COUNT_BIG(*) FROM sys.availability_databases_cluster WHERE database_name = @name;";

    private const string MirroringSql = "SELECT COUNT_BIG(*) FROM sys.database_mirroring WHERE database_id = DB_ID(@name) AND mirroring_guid IS NOT NULL;";

    private const string MasterFilesSql = """
        SELECT DB_NAME(mf.database_id), mf.database_id, mf.file_id, mf.type, mf.name, mf.physical_name, mf.size
        FROM sys.master_files AS mf;
        """;

    // One row per database file with the volume it lives on; the caller matches a directory to a row. Needs VIEW SERVER STATE.
    private const string VolumesSql = """
        SELECT mf.physical_name, vs.available_bytes, vs.total_bytes
        FROM sys.master_files AS mf
        CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) AS vs;
        """;

    // Restoring OVER an existing database needs sysadmin, dbcreator or being the database's owner (RESTORE permissions, Microsoft docs).
    private const string ExistingRestorePermissionSql = """
        SELECT CONVERT(int, IS_SRVROLEMEMBER(N'sysadmin')),
               CONVERT(int, IS_SRVROLEMEMBER(N'dbcreator')),
               CONVERT(int, CASE WHEN EXISTS (SELECT 1 FROM sys.databases WHERE name = @name AND owner_sid = SUSER_SID()) THEN 1 ELSE 0 END);
        """;


    /// <summary>Runs an optional read; a server refusal (permission, missing view on an old version) is "unknown" (<paramref name="unknown"/>), cancellation is not swallowed.</summary>
    public static async Task<T> OptionalAsync<T>(Func<Task<T>> read, T unknown)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (DatabaseProviderException)
        {
            return unknown;
        }
    }

    public static async Task<SqlServerRawDirectories> ReadDirectoriesAsync(SqlServerSession session, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(
            "restore-read-directories", DirectoriesSql, bind: null,
            reader => new SqlServerRawDirectories(Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3)),
            cancellationToken).ConfigureAwait(false);
        return rows.Count == 1 ? rows[0] : new SqlServerRawDirectories(null, null, null, null);
    }

    /// <summary>The database as it is right now, or null when no database of that name is visible. A name over 128 characters cannot exist.</summary>
    public static async Task<SqlServerObservedDatabase?> ObserveAsync(SqlServerSession session, string name, CancellationToken cancellationToken)
    {
        if (name.Length > SqlServerDiscovery.MaxDatabaseNameLength)
        {
            return null;
        }

        var rows = await session.QueryAsync(
            "restore-observe-database", ObserveSql, parameters => BindName(parameters, name),
            reader => new SqlServerObservedDatabase(
                Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.GetDateTime(1),
                SqlServerMetadataMapper.MapState(reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture)),
                SqlServerMetadataMapper.MapUserAccess(reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture)),
                Text(reader, 4)),
            cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>The family GUID of the target database (the identity of its backup lineage); null when it is not visible.</summary>
    public static async Task<Guid?> ReadFamilyGuidAsync(SqlServerSession session, string name, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(
            "restore-read-family", FamilySql, parameters => BindName(parameters, name),
            reader => reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0), cancellationToken).ConfigureAwait(false);
        return rows.Count == 1 ? rows[0] : null;
    }

    public static async Task<int> CountSessionsAsync(SqlServerSession session, string name, CancellationToken cancellationToken)
    {
        var count = await ScalarAsync(session, "restore-count-sessions", SessionsSql, name, cancellationToken).ConfigureAwait(false);
        return (int)Math.Min(count, int.MaxValue);
    }

    public static async Task<bool> IsInAvailabilityGroupAsync(SqlServerSession session, string name, CancellationToken cancellationToken) =>
        await ScalarAsync(session, "restore-check-availability-group", AvailabilityGroupSql, name, cancellationToken).ConfigureAwait(false) > 0;

    public static async Task<bool> IsMirroredAsync(SqlServerSession session, string name, CancellationToken cancellationToken) =>
        await ScalarAsync(session, "restore-check-mirroring", MirroringSql, name, cancellationToken).ConfigureAwait(false) > 0;

    public static async Task<IReadOnlyList<SqlServerMasterFile>> ReadMasterFilesAsync(SqlServerSession session, CancellationToken cancellationToken) =>
        await session.QueryAsync(
            "restore-read-master-files", MasterFilesSql, bind: null,
            reader => new SqlServerMasterFile(
                Text(reader, 0),
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                MapMasterFileKind(reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture)),
                Text(reader, 4) ?? string.Empty,
                Text(reader, 5) ?? string.Empty,
                // sys.master_files.size counts 8 KB pages.
                reader.IsDBNull(6) ? 0 : Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture) * 8192),
            cancellationToken).ConfigureAwait(false);

    public static async Task<IReadOnlyList<SqlServerVolume>> ReadVolumesAsync(SqlServerSession session, CancellationToken cancellationToken) =>
        await session.QueryAsync(
            "restore-read-volumes", VolumesSql, bind: null,
            reader => new SqlServerVolume(
                Text(reader, 0) ?? string.Empty,
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Whether the principal may restore OVER the named existing database: sysadmin, dbcreator or the database's owner. Definite "no" only when
    /// every answer is a definite 0; a NULL answer without a grant is unknown.
    /// </summary>
    public static async Task<SqlServerBackupPermission> ProbeExistingRestorePermissionAsync(SqlServerSession session, string name, CancellationToken cancellationToken)
    {
        if (name.Length > SqlServerDiscovery.MaxDatabaseNameLength)
        {
            return SqlServerBackupPermission.Unknown;
        }

        var rows = await session.QueryAsync(
            "restore-probe-existing-permission", ExistingRestorePermissionSql, parameters => BindName(parameters, name),
            reader => new int?[]
            {
                reader.IsDBNull(0) ? null : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture)
            },
            cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1)
        {
            return SqlServerBackupPermission.Unknown;
        }

        return rows[0].Any(answer => answer == 1) ? SqlServerBackupPermission.Granted
            : rows[0].All(answer => answer == 0) ? SqlServerBackupPermission.Denied
            : SqlServerBackupPermission.Unknown;
    }


    /// <summary><c>sys.master_files.type</c>: 0 rows (data), 1 log, 2 FILESTREAM (also memory-optimized data), 4 full-text.</summary>
    internal static RestoreFileKind MapMasterFileKind(int? type) => type switch
    {
        0 => RestoreFileKind.Data,
        1 => RestoreFileKind.Log,
        2 => RestoreFileKind.FileStream,
        4 => RestoreFileKind.FullText,
        _ => RestoreFileKind.Other
    };

    private static async Task<long> ScalarAsync(SqlServerSession session, string operation, string sql, string name, CancellationToken cancellationToken)
    {
        if (name.Length > SqlServerDiscovery.MaxDatabaseNameLength)
        {
            return 0;
        }

        var rows = await session.QueryAsync(
            operation, sql, parameters => BindName(parameters, name),
            reader => reader.IsDBNull(0) ? 0L : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        return rows.Count == 1 ? rows[0] : 0;
    }

    private static void BindName(SqlParameterCollection parameters, string name) =>
        parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, SqlServerDiscovery.MaxDatabaseNameLength) { Value = name });

    private static string? Text(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}
