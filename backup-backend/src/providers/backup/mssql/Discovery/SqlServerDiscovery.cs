using System.Data;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using Microsoft.Data.SqlClient;

namespace BackupPlatform.BackupProviders.MSSQL.Discovery;

/// <summary>
/// Reads metadata over an open session. Every statement is constant text; the only caller-supplied value (a database name)
/// is always a parameter. Raw rows are converted by <see cref="SqlServerMetadataMapper"/>, which is the only place that gives
/// catalog codes a meaning.
/// </summary>
internal static class SqlServerDiscovery
{
    // SERVERPROPERTY returns sql_variant and yields NULL for properties an older server does not know, so every value is converted explicitly.
    private const string ServerPropertiesSql = """
        SELECT
            CONVERT(nvarchar(256), SERVERPROPERTY(N'ServerName')),
            CONVERT(nvarchar(256), SERVERPROPERTY(N'MachineName')),
            CONVERT(nvarchar(256), SERVERPROPERTY(N'InstanceName')),
            CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductVersion')),
            CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductLevel')),
            CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductUpdateLevel')),
            CONVERT(nvarchar(128), SERVERPROPERTY(N'Edition')),
            CONVERT(int, SERVERPROPERTY(N'EngineEdition')),
            CONVERT(nvarchar(512), SERVERPROPERTY(N'InstanceDefaultBackupPath'));
        """;

    // sys.dm_os_host_info exists from SQL Server 2017. On SQL Server 2025 any login can read it (no VIEW SERVER STATE needed), while
    // permission-gated DMVs there fail with Msg 300 + 297, which the failure classifier maps to PermissionDenied. Older versions may gate it,
    // so a permission failure is treated as "platform unknown" rather than failing discovery.
    private const string HostInfoSql = "SELECT host_platform FROM sys.dm_os_host_info;";

    private const string DatabaseColumns = """
        SELECT d.database_id, d.name, d.state, d.user_access, d.recovery_model, d.compatibility_level,
               d.is_read_only, d.is_encrypted, d.source_database_id
        FROM sys.databases AS d
        """;

    private const string ListDatabasesSql = DatabaseColumns + " ORDER BY d.database_id;";
    private const string FindDatabaseSql = DatabaseColumns + " WHERE d.name = @name;";

    // HAS_PERMS_BY_NAME evaluates the effective permission (sysadmin, db_owner, db_backupoperator, explicit GRANT, and DENY) for the
    // current principal against any database, regardless of the connection's current database. Observed on SQL Server 2025: it
    // returns NULL only for an invalid permission name, and 0 (not NULL) for a missing or OFFLINE database, even for sysadmin,
    // so a 0 is only meaningful for an existing, online database. The pre-check enforces that.
    private const string BackupPermissionSql = "SELECT CONVERT(int, HAS_PERMS_BY_NAME(@name, N'DATABASE', N'BACKUP DATABASE'));";

    // Server-scope permission (no securable name); a principal may always ask about itself. 1 for sysadmin, 0 for db_backupoperator alone.
    private const string VerifyPermissionSql = "SELECT CONVERT(int, HAS_PERMS_BY_NAME(NULL, NULL, N'CREATE ANY DATABASE'));";

    /// <summary>The longest name <c>sysname</c> can hold; longer names cannot exist and are not sent to the server.</summary>
    public const int MaxDatabaseNameLength = 128;

    public static async Task<SqlServerInstanceInfo> ReadInstanceAsync(SqlServerSession session, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(
            "read-server-properties",
            ServerPropertiesSql,
            bind: null,
            reader => new RawServerProperties(
                ServerName: GetString(reader, 0),
                MachineName: GetString(reader, 1),
                InstanceName: GetString(reader, 2),
                ProductVersion: GetString(reader, 3),
                ProductLevel: GetString(reader, 4),
                ProductUpdateLevel: GetString(reader, 5),
                Edition: GetString(reader, 6),
                EngineEdition: GetInt(reader, 7),
                InstanceDefaultBackupPath: GetString(reader, 8)),
            cancellationToken).ConfigureAwait(false);

        var raw = rows.Count == 1
            ? rows[0]
            : throw new DatabaseProviderException(DatabaseFailureKind.Unexpected, "SQL Server returned no server metadata.");

        var platform = SqlServerHostPlatform.Unknown;
        if (SqlServerVersion.TryParse(raw.ProductVersion, out var version) && version.Major >= 14)
        {
            platform = await ReadHostPlatformAsync(session, cancellationToken).ConfigureAwait(false);
        }

        return SqlServerMetadataMapper.MapInstance(raw, platform);
    }

    public static async Task<IReadOnlyList<SqlServerDatabaseInfo>> ListDatabasesAsync(SqlServerSession session, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(
            "list-databases", ListDatabasesSql, bind: null, ReadDatabase, cancellationToken).ConfigureAwait(false);
        return rows.Select(SqlServerMetadataMapper.MapDatabase).ToArray();
    }

    public static async Task<SqlServerDatabaseInfo?> FindDatabaseAsync(SqlServerSession session, string name, CancellationToken cancellationToken)
    {
        if (name.Length > MaxDatabaseNameLength)
        {
            return null;
        }

        var rows = await session.QueryAsync(
            "find-database", FindDatabaseSql, parameters => BindName(parameters, name), ReadDatabase, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : SqlServerMetadataMapper.MapDatabase(rows[0]);
    }

    /// <summary>
    /// Asks the server whether the connected principal holds <c>BACKUP DATABASE</c> on the named database. This executes no backup.
    /// Only meaningful for an existing, online database; an invalid answer from the server yields <see cref="SqlServerBackupPermission.Unknown"/>.
    /// </summary>
    public static async Task<SqlServerBackupPermission> ProbeBackupPermissionAsync(SqlServerSession session, string name, CancellationToken cancellationToken)
    {
        if (name.Length > MaxDatabaseNameLength)
        {
            return SqlServerBackupPermission.Unknown;
        }

        var rows = await session.QueryAsync(
            "probe-backup-permission", BackupPermissionSql, parameters => BindName(parameters, name), reader => GetInt(reader, 0), cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 1 ? MapPermission(rows[0]) : SqlServerBackupPermission.Unknown;
    }

    /// <summary>
    /// Asks the server whether the connected principal may run <c>RESTORE VERIFYONLY</c>. Observed on SQL Server 2025: it needs the
    /// server-level <c>CREATE ANY DATABASE</c> permission (Msg 262 "CREATE DATABASE permission denied in database 'master'")
    /// that <c>db_backupoperator</c> does not include, and the principal can read its own answer without extra rights.
    /// </summary>
    public static async Task<SqlServerBackupPermission> ProbeVerifyPermissionAsync(SqlServerSession session, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(
            "probe-verify-permission", VerifyPermissionSql, bind: null, reader => GetInt(reader, 0), cancellationToken).ConfigureAwait(false);
        return rows.Count == 1 ? MapPermission(rows[0]) : SqlServerBackupPermission.Unknown;
    }

    internal static SqlServerBackupPermission MapPermission(int? hasPermission) => hasPermission switch
    {
        1 => SqlServerBackupPermission.Granted,
        0 => SqlServerBackupPermission.Denied,
        _ => SqlServerBackupPermission.Unknown
    };

    private static async Task<SqlServerHostPlatform> ReadHostPlatformAsync(SqlServerSession session, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await session.QueryAsync(
                "read-host-platform", HostInfoSql, bind: null, reader => GetString(reader, 0), cancellationToken).ConfigureAwait(false);
            return rows.Count == 1 ? SqlServerMetadataMapper.MapHostPlatform(rows[0]) : SqlServerHostPlatform.Unknown;
        }
        catch (DatabaseProviderException exception) when (exception.Kind == DatabaseFailureKind.PermissionDenied)
        {
            return SqlServerHostPlatform.Unknown;
        }
    }

    private static void BindName(SqlParameterCollection parameters, string name) =>
        parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, MaxDatabaseNameLength) { Value = name });

    private static RawDatabaseRow ReadDatabase(SqlDataReader reader) => new(
        DatabaseId: GetInt(reader, 0) ?? 0,
        Name: GetString(reader, 1),
        State: GetInt(reader, 2),
        UserAccess: GetInt(reader, 3),
        RecoveryModel: GetInt(reader, 4),
        CompatibilityLevel: GetInt(reader, 5),
        IsReadOnly: GetBool(reader, 6),
        IsEncrypted: GetBool(reader, 7),
        SourceDatabaseId: GetInt(reader, 8));

    // tinyint/smallint/int/bit columns are read through Convert so the column width never matters.
    private static string? GetString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static int? GetInt(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static bool? GetBool(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToBoolean(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
}
