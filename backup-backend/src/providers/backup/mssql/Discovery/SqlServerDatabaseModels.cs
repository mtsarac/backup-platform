namespace BackupPlatform.BackupProviders.MSSQL.Discovery;

/// <summary><c>sys.databases.state</c>. Codes this code does not know (new or Azure-only states) map to <see cref="Unknown"/>.</summary>
public enum SqlServerDatabaseState
{
    Unknown = 0,
    Online,
    Restoring,
    Recovering,
    RecoveryPending,
    Suspect,
    Emergency,
    Offline
}

/// <summary><c>sys.databases.recovery_model</c>.</summary>
public enum SqlServerRecoveryModel
{
    Unknown = 0,
    Simple,
    Full,
    BulkLogged
}

/// <summary><c>sys.databases.user_access</c>.</summary>
public enum SqlServerUserAccess
{
    Unknown = 0,
    MultiUser,
    SingleUser,
    RestrictedUser
}

public enum SqlServerSystemDatabase
{
    None = 0,
    Master,
    Tempdb,
    Model,
    Msdb
}

public enum SqlServerHostPlatform
{
    Unknown = 0,
    Windows,
    Linux
}

/// <summary>Whether the connected principal may run <c>BACKUP DATABASE</c> on a database, as reported by the server. Never guessed.</summary>
public enum SqlServerBackupPermission
{
    /// <summary>The server did not give a definite answer (database not visible, metadata restricted, query failed).</summary>
    Unknown = 0,
    Granted,
    Denied
}

/// <summary>Strongly typed server metadata. <see cref="Version"/> is null when the server's version text is not in a recognised shape.</summary>
public sealed record SqlServerInstanceInfo(
    string ServerName,
    string? MachineName,
    string? InstanceName,
    SqlServerVersion? Version,
    string? RawProductVersion,
    string? ProductLevel,
    string? ProductUpdateLevel,
    SqlServerEdition Edition,
    SqlServerHostPlatform HostPlatform,
    string? DefaultBackupDirectory);

/// <summary>
/// Strongly typed database metadata. The raw numeric codes are retained next to their typed counterparts so an unrecognised
/// value is diagnosable without losing the safe <c>Unknown</c> mapping.
/// </summary>
public sealed record SqlServerDatabaseInfo(
    int DatabaseId,
    string Name,
    SqlServerDatabaseState State,
    int? StateCode,
    SqlServerUserAccess UserAccess,
    SqlServerRecoveryModel RecoveryModel,
    SqlServerCompatibilityLevel? CompatibilityLevel,
    SqlServerSystemDatabase SystemDatabase,
    bool IsReadOnly,
    bool? IsTransparentDataEncrypted,
    bool IsSnapshot)
{
    public bool IsSystem => SystemDatabase != SqlServerSystemDatabase.None;
}

/// <summary>The typed result of inspecting one instance. Databases are listed in full, system databases included and flagged.</summary>
public sealed record SqlServerInstanceDiscovery(
    SqlServerInstanceInfo Instance,
    IReadOnlyList<SqlServerDatabaseInfo> Databases);
