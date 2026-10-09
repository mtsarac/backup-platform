using System.Globalization;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.Restore;

namespace BackupPlatform.BackupProviders.MSSQL.PreChecks;

/// <summary>Stable identifiers of the SQL Server restore checks (pre-check and plan). Consumers key on these and on the reason codes, never on message text.</summary>
public static class SqlServerRestoreCheckIds
{
    public const string TargetServer = "restore.target-server";
    public const string DatabaseName = "restore.database-name";
    public const string TargetProtected = "restore.target-protected";
    public const string RestorePermission = "permission.restore";
    public const string ActiveSessions = "restore.target-sessions";
    public const string Directories = "restore.target-directories";
    public const string DiskSpace = "restore.disk-space";
    public const string BackupType = "restore.backup-type";
    public const string VersionCompatibility = "restore.version-compatibility";
    public const string EditionCompatibility = "restore.edition-compatibility";
    public const string TargetRelation = "restore.target-relation";
    public const string FileTypes = "restore.file-types";
    public const string FileMapping = "restore.file-mapping";
}

/// <summary>What was learned from the target server, gathered separately so every decision below is a pure function. A null collection or flag means "could not be read".</summary>
internal sealed record SqlServerRestoreTargetFacts(
    DatabaseProviderException? ConnectFailure,
    DatabaseProviderException? MetadataFailure,
    SqlServerInstanceInfo? Instance,
    SqlServerDatabaseInfo? Database,
    SqlServerRawDirectories? Directories,
    bool? InAvailabilityGroup,
    bool? IsMirrored,
    IReadOnlyList<SqlServerVolume>? Volumes);

/// <summary>
/// The checks the restore pre-check and the restore plan share, as pure functions over <see cref="SqlServerRestoreTargetFacts"/>. A condition that
/// cannot be verified is a Warning, never a silent Pass; a condition that makes the restore impossible or unsafe is a Failure; checks that depend on
/// a failed one are Skipped rather than invented.
/// </summary>
internal static class SqlServerRestoreChecks
{
    private static readonly string[] ReservedDatabaseNames = ["master", "model", "msdb", "tempdb", "mssqlsystemresource"];

    public static BackupPreCheckItem Passed(string id, string reason, string message) => new(id, BackupPreCheckStatus.Passed, reason, message);

    public static BackupPreCheckItem Warning(string id, string reason, string message) => new(id, BackupPreCheckStatus.Warning, reason, message);

    public static BackupPreCheckItem Failed(string id, string reason, string message) =>
        new(id, BackupPreCheckStatus.Failed, reason, message, SqlServerFailureCauses.ForRestoreReason(reason));

    public static BackupPreCheckItem Skipped(string id) =>
        new(id, BackupPreCheckStatus.Skipped, SqlServerPreCheckReasons.PrerequisiteFailed, "Not evaluated because an earlier check failed.");

    /// <summary>
    /// The reachability and login items when the connection failed, together with the downstream checks as Skipped; null when the
    /// connection worked and the caller continues.
    /// </summary>
    public static IReadOnlyList<BackupPreCheckItem>? ConnectionFailureItems(SqlServerRestoreTargetFacts facts, IReadOnlyList<string> downstream)
    {
        if (facts.ConnectFailure is not { } failure)
        {
            return null;
        }

        var items = new List<BackupPreCheckItem>();
        if (failure.Kind == DatabaseFailureKind.AuthenticationFailed)
        {
            // The server answered, so it is reachable; it is the login that was refused.
            items.Add(Passed(BackupPreCheckIds.ServerReachable, SqlServerRestoreReasons.Ok, "The SQL Server instance answered."));
            items.Add(Failed(BackupPreCheckIds.Authentication, SqlServerRestoreReasons.AuthenticationFailed, failure.Message));
        }
        else
        {
            items.Add(Failed(BackupPreCheckIds.ServerReachable, ConnectReason(failure.Kind), failure.Message));
            items.Add(Skipped(BackupPreCheckIds.Authentication));
        }

        items.AddRange(downstream.Select(Skipped));
        return items;
    }

    public static IEnumerable<BackupPreCheckItem> ConnectedItems() =>
    [
        Passed(BackupPreCheckIds.ServerReachable, SqlServerRestoreReasons.Ok, "The SQL Server instance answered."),
        Passed(BackupPreCheckIds.Authentication, SqlServerRestoreReasons.Ok, "SQL Server accepted the credentials.")
    ];

    /// <summary>The item that stands in for everything the server metadata unlocks when it could not be read.</summary>
    public static BackupPreCheckItem MetadataUnavailable(SqlServerRestoreTargetFacts facts, string checkId) =>
        Failed(checkId, SqlServerPreCheckReasons.MetadataUnavailable, facts.MetadataFailure?.Message ?? "SQL Server metadata could not be read.");

    public static BackupPreCheckItem CheckServer(SqlServerInstanceInfo instance)
    {
        var platform = instance.HostPlatform == SqlServerHostPlatform.Unknown ? "an unknown operating system" : instance.HostPlatform.ToString();
        var edition = instance.Edition.Text is { } text ? $" ({text})" : string.Empty;
        return instance.Version is { } version
            ? Passed(SqlServerRestoreCheckIds.TargetServer, SqlServerRestoreReasons.Ok, $"The target runs SQL Server {version}{edition} on {platform}.")
            : Warning(SqlServerRestoreCheckIds.TargetServer, SqlServerRestoreReasons.ServerVersionUnknown,
                $"The target's SQL Server version could not be read{edition}; version compatibility cannot be verified.");
    }

    /// <summary>The name must fit <c>sysname</c> and be free of control characters, which no statement may carry.</summary>
    public static BackupPreCheckItem CheckDatabaseName(string name) =>
        name.Length is 0 or > SqlServerDiscovery.MaxDatabaseNameLength || name.Any(char.IsControl)
            ? Failed(SqlServerRestoreCheckIds.DatabaseName, SqlServerRestoreReasons.DatabaseNameInvalid,
                $"The database name must be 1 to {SqlServerDiscovery.MaxDatabaseNameLength.ToString(CultureInfo.InvariantCulture)} characters without control characters.")
            : Passed(SqlServerRestoreCheckIds.DatabaseName, SqlServerRestoreReasons.Ok, "The database name is valid.");

    /// <summary>NewDatabase needs the name free, ReplaceExistingDatabase needs a database to replace.</summary>
    public static BackupPreCheckItem CheckExists(DatabaseRestoreMode mode, SqlServerDatabaseInfo? database) => (mode, database) switch
    {
        (DatabaseRestoreMode.NewDatabase, not null) => Failed(BackupPreCheckIds.DatabaseExists, RestoreReasonCodes.TargetDatabaseExists,
            "A database with this name already exists on the target server; a new database is not created over it."),
        (DatabaseRestoreMode.NewDatabase, null) => Passed(BackupPreCheckIds.DatabaseExists, SqlServerRestoreReasons.Ok, "No database with this name exists yet."),
        (_, null) => Failed(BackupPreCheckIds.DatabaseExists, RestoreReasonCodes.TargetDatabaseMissing,
            "There is no database with this name on the target server (or the login cannot see it), so there is nothing to replace."),
        _ => Passed(BackupPreCheckIds.DatabaseExists, SqlServerRestoreReasons.Ok, "The database to replace exists.")
    };

    /// <summary>
    /// System databases and the engine's reserved names are never a restore target; neither is a database snapshot, nor a database that belongs to an
    /// availability group or is mirrored (the engine refuses to restore over those). Null when the check does not apply (a new database with an ordinary name).
    /// </summary>
    public static BackupPreCheckItem? CheckProtected(string name, DatabaseRestoreMode mode, SqlServerRestoreTargetFacts facts)
    {
        const string id = SqlServerRestoreCheckIds.TargetProtected;
        var database = facts.Database;

        if (ReservedDatabaseNames.Contains(name, StringComparer.OrdinalIgnoreCase) || database is { IsSystem: true })
        {
            return Failed(id, RestoreReasonCodes.TargetProtected, "System databases are never a restore target.");
        }

        if (mode != DatabaseRestoreMode.ReplaceExistingDatabase || database is null)
        {
            return null;
        }

        if (database.IsSnapshot)
        {
            return Failed(id, RestoreReasonCodes.TargetProtected, "The target is a database snapshot, which cannot be restored over.");
        }

        if (facts.InAvailabilityGroup == true)
        {
            return Failed(id, RestoreReasonCodes.TargetProtected,
                "The target database belongs to an availability group; remove it from the group before restoring over it.");
        }

        if (facts.IsMirrored == true)
        {
            return Failed(id, RestoreReasonCodes.TargetProtected,
                "The target database is part of a database mirroring session; end the mirroring session before restoring over it.");
        }

        return facts.InAvailabilityGroup is null || facts.IsMirrored is null
            ? Warning(id, SqlServerRestoreReasons.TargetProtectionUnknown,
                "SQL Server did not report whether the target database belongs to an availability group or a mirroring session; the restore itself will tell.")
            : Passed(id, SqlServerRestoreReasons.TargetNotProtected, "The target is not a system database, a snapshot, an availability group member or mirrored.");
    }

    /// <summary>The state of an existing database that is about to be replaced. Only transient and unrecognised states fail; a damaged or offline database is exactly what a replacement is for.</summary>
    public static BackupPreCheckItem CheckExistingState(SqlServerDatabaseInfo database)
    {
        const string id = BackupPreCheckIds.DatabaseState;
        return database.State switch
        {
            SqlServerDatabaseState.Online => database.UserAccess switch
            {
                SqlServerUserAccess.SingleUser => Warning(id, SqlServerPreCheckReasons.DatabaseSingleUser,
                    "The database is in SINGLE_USER mode; the restore fails if another session holds the only connection."),
                SqlServerUserAccess.RestrictedUser => Warning(id, SqlServerPreCheckReasons.DatabaseRestrictedUser,
                    "The database is in RESTRICTED_USER mode."),
                _ => Passed(id, SqlServerPreCheckReasons.DatabaseOnline, "The database is online.")
            },
            SqlServerDatabaseState.Restoring => Warning(id, SqlServerPreCheckReasons.DatabaseRestoring,
                "The database is in RESTORING state (for example after an unfinished restore); it will be overwritten."),
            SqlServerDatabaseState.Offline => Warning(id, SqlServerPreCheckReasons.DatabaseOffline,
                "The database is offline; it will be overwritten."),
            SqlServerDatabaseState.RecoveryPending => Warning(id, SqlServerPreCheckReasons.DatabaseRecoveryPending,
                "The database is in recovery-pending state; it will be overwritten."),
            SqlServerDatabaseState.Suspect => Warning(id, SqlServerPreCheckReasons.DatabaseSuspect,
                "The database is marked suspect; it will be overwritten."),
            SqlServerDatabaseState.Emergency => Warning(id, SqlServerPreCheckReasons.DatabaseEmergency,
                "The database is in emergency mode; it will be overwritten."),
            SqlServerDatabaseState.Recovering => Failed(id, SqlServerPreCheckReasons.DatabaseRecovering,
                "The database is recovering; retry when it is online."),
            _ => Failed(id, SqlServerPreCheckReasons.DatabaseStateUnknown,
                "The database state is not recognised, so it is not assumed to be replaceable.")
        };
    }

    /// <summary>Active sessions against the database that is about to be replaced; null <paramref name="sessions"/> means the server did not say.</summary>
    public static BackupPreCheckItem CheckSessions(int? sessions, bool terminateActiveConnections)
    {
        const string id = SqlServerRestoreCheckIds.ActiveSessions;
        return sessions switch
        {
            null => Warning(id, SqlServerRestoreReasons.SessionsUnknown,
                terminateActiveConnections
                    ? "SQL Server did not report the active sessions (VIEW SERVER STATE is needed); if there are any they will be disconnected."
                    : "SQL Server did not report the active sessions (VIEW SERVER STATE is needed); the restore fails if any session is connected."),
            0 => Passed(id, SqlServerRestoreReasons.SessionsNone, "No other session is connected to the database."),
            int count when terminateActiveConnections => Warning(id, SqlServerRestoreReasons.SessionsWillBeTerminated,
                $"{count.ToString(CultureInfo.InvariantCulture)} other session(s) are connected to the database; they will be disconnected and their open transactions rolled back."),
            int count => Failed(id, RestoreReasonCodes.TargetInUse,
                $"{count.ToString(CultureInfo.InvariantCulture)} other session(s) are connected to the database and disconnecting them was not allowed.")
        };
    }

    /// <summary>The restore permission item for a new database (server-level CREATE ANY DATABASE) or an existing one (sysadmin, dbcreator or owner).</summary>
    public static BackupPreCheckItem CheckPermission(DatabaseRestoreMode mode, SqlServerBackupPermission permission)
    {
        const string id = SqlServerRestoreCheckIds.RestorePermission;
        var existing = mode == DatabaseRestoreMode.ReplaceExistingDatabase;
        return permission switch
        {
            SqlServerBackupPermission.Granted => Passed(id, SqlServerRestoreReasons.RestorePermissionGranted,
                existing ? "The login may restore over the existing database." : "The login may create databases."),
            SqlServerBackupPermission.Denied => Failed(id, SqlServerRestoreReasons.RestorePermissionDenied,
                existing
                    ? "The login may not restore over this database: SQL Server requires sysadmin, dbcreator or being the database's owner."
                    : "The login may not create databases: restoring into a new database needs the server-level CREATE ANY DATABASE permission (or dbcreator or sysadmin)."),
            _ => Warning(id, SqlServerRestoreReasons.RestorePermissionUnknown,
                "SQL Server did not report whether the login may restore this database; the restore itself will tell.")
        };
    }

    /// <summary>
    /// The directories restored files go to. Unreadable metadata is unknown (the plan checks again), an unusable one is a failure, and
    /// master's directory as a stand-in for an unreported default is worth a warning.
    /// </summary>
    public static (BackupPreCheckItem Item, SqlServerTargetDirectories? Directories) CheckDirectories(SqlServerRestoreTargetFacts facts)
    {
        const string id = SqlServerRestoreCheckIds.Directories;
        if (facts.Directories is not { } raw)
        {
            return (Warning(id, SqlServerRestoreReasons.DirectoriesUnknown,
                "SQL Server did not report its default data and log directories; they are determined when the backup is planned."), null);
        }

        var directories = SqlServerTargetDirectoryResolver.Resolve(raw);
        if (directories is null)
        {
            return (Failed(id, RestoreReasonCodes.FileMappingFailed,
                "SQL Server reports no usable default data directory and master's own directory could not be determined, so there is no safe place for the restored files."), null);
        }

        var platform = facts.Instance?.HostPlatform ?? SqlServerHostPlatform.Unknown;
        if ((platform == SqlServerHostPlatform.Windows && (!directories.Data.IsWindowsStyle || !directories.Log.IsWindowsStyle))
            || (platform == SqlServerHostPlatform.Linux && (directories.Data.IsWindowsStyle || directories.Log.IsWindowsStyle)))
        {
            return (Failed(id, RestoreReasonCodes.FileMappingFailed,
                "The default directories the server reports do not match the server's operating system, so no safe destination can be derived."), null);
        }

        return directories.DataIsFallback || directories.LogIsFallback
            ? (Warning(id, SqlServerRestoreReasons.DirectoriesFallback,
                "SQL Server does not report its default directories; the directory of master's own files is used instead."), directories)
            : (Passed(id, SqlServerRestoreReasons.DirectoriesKnown, "The server's default data and log directories are known."), directories);
    }

    /// <summary>The volume that holds files of <paramref name="directory"/>: the volume of any database file that lives directly in it. Null when none does.</summary>
    public static SqlServerVolume? FindVolume(IReadOnlyList<SqlServerVolume> volumes, string directory)
    {
        var comparison = new DatabaseServerPath(directory).IsWindowsStyle ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var wanted = SqlServerTargetDirectoryResolver.Usable(directory);
        return wanted is null
            ? null
            : volumes.FirstOrDefault(volume =>
                SqlServerTargetDirectoryResolver.Usable(SqlServerTargetDirectoryResolver.DirectoryOf(volume.FilePath)) is { } own
                && string.Equals(own, wanted, comparison));
    }

    private static string ConnectReason(DatabaseFailureKind kind) => kind switch
    {
        DatabaseFailureKind.Unreachable => SqlServerPreCheckReasons.ServerUnreachable,
        DatabaseFailureKind.Timeout => SqlServerPreCheckReasons.ConnectionTimeout,
        DatabaseFailureKind.InvalidConfiguration => SqlServerPreCheckReasons.ConnectionSettingsInvalid,
        _ => SqlServerPreCheckReasons.ConnectionFailed
    };
}
