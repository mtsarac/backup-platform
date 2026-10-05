namespace BackupPlatform.BackupProviders.MSSQL.Discovery;

/// <summary>Server metadata exactly as the catalog returned it. Nothing here is interpreted.</summary>
internal sealed record RawServerProperties(
    string? ServerName,
    string? MachineName,
    string? InstanceName,
    string? ProductVersion,
    string? ProductLevel,
    string? ProductUpdateLevel,
    string? Edition,
    int? EngineEdition,
    string? InstanceDefaultBackupPath);

/// <summary>One <c>sys.databases</c> row exactly as the catalog returned it.</summary>
internal sealed record RawDatabaseRow(
    int DatabaseId,
    string? Name,
    int? State,
    int? UserAccess,
    int? RecoveryModel,
    int? CompatibilityLevel,
    bool? IsReadOnly,
    bool? IsEncrypted,
    int? SourceDatabaseId);

/// <summary>
/// The boundary between raw catalog values and the typed model. Pure functions: every unknown or missing code maps to an
/// explicit <c>Unknown</c>, never to an exception, so a new SQL Server release cannot break discovery.
/// </summary>
internal static class SqlServerMetadataMapper
{
    public static SqlServerInstanceInfo MapInstance(RawServerProperties raw, SqlServerHostPlatform hostPlatform)
    {
        ArgumentNullException.ThrowIfNull(raw);

        SqlServerVersion? version = SqlServerVersion.TryParse(raw.ProductVersion, out var parsed) ? parsed : null;

        // SQL Server only ran on Windows before 2017 (14.x), so the platform is a fact there even where the host-info DMV does not exist.
        if (hostPlatform == SqlServerHostPlatform.Unknown && version is { Major: < 14 })
        {
            hostPlatform = SqlServerHostPlatform.Windows;
        }

        return new SqlServerInstanceInfo(
            ServerName: Clean(raw.ServerName) ?? "(unknown)",
            MachineName: Clean(raw.MachineName),
            InstanceName: Clean(raw.InstanceName),
            Version: version,
            RawProductVersion: Clean(raw.ProductVersion),
            ProductLevel: Clean(raw.ProductLevel),
            ProductUpdateLevel: Clean(raw.ProductUpdateLevel),
            Edition: SqlServerEdition.FromServerProperties(raw.Edition, raw.EngineEdition),
            HostPlatform: hostPlatform,
            DefaultBackupDirectory: Clean(raw.InstanceDefaultBackupPath));
    }

    public static SqlServerDatabaseInfo MapDatabase(RawDatabaseRow raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        return new SqlServerDatabaseInfo(
            DatabaseId: raw.DatabaseId,
            Name: raw.Name ?? string.Empty,
            State: MapState(raw.State),
            StateCode: raw.State,
            UserAccess: MapUserAccess(raw.UserAccess),
            RecoveryModel: MapRecoveryModel(raw.RecoveryModel),
            CompatibilityLevel: raw.CompatibilityLevel is > 0 ? new SqlServerCompatibilityLevel(raw.CompatibilityLevel.Value) : null,
            SystemDatabase: MapSystemDatabase(raw.DatabaseId),
            IsReadOnly: raw.IsReadOnly ?? false,
            IsTransparentDataEncrypted: raw.IsEncrypted,
            IsSnapshot: raw.SourceDatabaseId is not null);
    }

    public static SqlServerDatabaseState MapState(int? code) => code switch
    {
        0 => SqlServerDatabaseState.Online,
        1 => SqlServerDatabaseState.Restoring,
        2 => SqlServerDatabaseState.Recovering,
        3 => SqlServerDatabaseState.RecoveryPending,
        4 => SqlServerDatabaseState.Suspect,
        5 => SqlServerDatabaseState.Emergency,
        6 => SqlServerDatabaseState.Offline,
        _ => SqlServerDatabaseState.Unknown
    };

    public static SqlServerRecoveryModel MapRecoveryModel(int? code) => code switch
    {
        1 => SqlServerRecoveryModel.Full,
        2 => SqlServerRecoveryModel.BulkLogged,
        3 => SqlServerRecoveryModel.Simple,
        _ => SqlServerRecoveryModel.Unknown
    };

    public static SqlServerUserAccess MapUserAccess(int? code) => code switch
    {
        0 => SqlServerUserAccess.MultiUser,
        1 => SqlServerUserAccess.SingleUser,
        2 => SqlServerUserAccess.RestrictedUser,
        _ => SqlServerUserAccess.Unknown
    };

    // The four system databases have fixed, reserved ids. Names are not used: a name is mutable metadata, the id is not.
    public static SqlServerSystemDatabase MapSystemDatabase(int databaseId) => databaseId switch
    {
        1 => SqlServerSystemDatabase.Master,
        2 => SqlServerSystemDatabase.Tempdb,
        3 => SqlServerSystemDatabase.Model,
        4 => SqlServerSystemDatabase.Msdb,
        _ => SqlServerSystemDatabase.None
    };

    public static SqlServerHostPlatform MapHostPlatform(string? hostPlatform) => hostPlatform?.Trim().ToUpperInvariant() switch
    {
        "WINDOWS" => SqlServerHostPlatform.Windows,
        "LINUX" => SqlServerHostPlatform.Linux,
        _ => SqlServerHostPlatform.Unknown
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
