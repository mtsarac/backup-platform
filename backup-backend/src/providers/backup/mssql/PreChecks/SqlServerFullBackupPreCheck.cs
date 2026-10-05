using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Capabilities;
using BackupPlatform.BackupProviders.MSSQL.Discovery;

namespace BackupPlatform.BackupProviders.MSSQL.PreChecks;

/// <summary>What was learned from the server, gathered separately so the decision below is a pure function.</summary>
internal sealed record SqlServerPreCheckFacts(
    DatabaseProviderException? ConnectFailure,
    DatabaseProviderException? MetadataFailure,
    SqlServerInstanceInfo? Instance,
    SqlServerDatabaseInfo? Database,
    SqlServerBackupPermission Permission);

/// <summary>Machine-readable reasons the SQL Server full-backup pre-check can report.</summary>
public static class SqlServerPreCheckReasons
{
    public const string Ok = "ok";
    public const string PrerequisiteFailed = "prerequisite-failed";
    public const string ServerUnreachable = "server-unreachable";
    public const string ConnectionTimeout = "connection-timeout";
    public const string ConnectionSettingsInvalid = "connection-settings-invalid";
    public const string ConnectionFailed = "connection-failed";
    public const string AuthenticationFailed = "authentication-failed";
    public const string MetadataUnavailable = "metadata-unavailable";
    public const string DatabaseNotFound = "database-not-found";
    public const string DatabaseOnline = "database-online";
    public const string DatabaseOffline = "database-offline";
    public const string DatabaseRestoring = "database-restoring";
    public const string DatabaseRecovering = "database-recovering";
    public const string DatabaseRecoveryPending = "database-recovery-pending";
    public const string DatabaseSuspect = "database-suspect";
    public const string DatabaseEmergency = "database-emergency";
    public const string DatabaseStateUnknown = "database-state-unknown";
    public const string DatabaseSingleUser = "database-single-user";
    public const string DatabaseRestrictedUser = "database-restricted-user";
    public const string DatabaseTdeEnabled = "database-tde-enabled";
    public const string RecoveryModelKnown = "recovery-model-known";
    public const string RecoveryModelUnknown = "recovery-model-unknown";
    public const string PermissionGranted = "backup-permission-granted";
    public const string PermissionDenied = "backup-permission-denied";
    public const string PermissionUnknown = "backup-permission-unknown";
    public const string CapabilitySupported = "capability-supported";
    public const string CapabilityUnsupported = "capability-unsupported";
    public const string CapabilityUnknown = "capability-unknown";
    public const string CapabilityNotRecognized = "capability-not-recognized";
    public const string StagingPathValid = "staging-path-valid";
    public const string StagingPathPlatformUnknown = "staging-path-platform-unknown";
    public const string StagingPathInvalid = "staging-path-invalid";
    public const string StagingPathWrongStyle = "staging-path-wrong-style";
    public const string ExtensionFailed = "extension-failed";
}

/// <summary>
/// The decision logic of the SQL Server full-backup pre-check: given what the server said, which checks passed, warn or fail.
/// It executes nothing and reads nothing. A condition that cannot be verified is a Warning, never a silent Pass; a condition that
/// makes the backup impossible is a Failure; checks that depend on a failed one are Skipped rather than invented.
/// </summary>
internal static class SqlServerFullBackupPreCheck
{
    public const string RecoveryModelCheckId = "database.recovery-model";
    public const string EncryptionCheckId = "database.encryption";

    public static IReadOnlyList<BackupPreCheckItem> Evaluate(SqlServerPreCheckFacts facts, FullBackupPreCheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(request);

        var items = new List<BackupPreCheckItem>();
        var downstream = DownstreamCheckIds(request);

        if (facts.ConnectFailure is { } connectFailure)
        {
            if (connectFailure.Kind == DatabaseFailureKind.AuthenticationFailed)
            {
                // The server answered, so it is reachable; it is the login that was refused.
                items.Add(Passed(BackupPreCheckIds.ServerReachable, SqlServerPreCheckReasons.Ok, "The SQL Server instance answered."));
                items.Add(Failed(BackupPreCheckIds.Authentication, SqlServerPreCheckReasons.AuthenticationFailed, connectFailure.Message));
            }
            else
            {
                items.Add(Failed(BackupPreCheckIds.ServerReachable, ConnectReason(connectFailure.Kind), connectFailure.Message));
                items.Add(Skipped(BackupPreCheckIds.Authentication));
            }

            items.AddRange(downstream.Select(Skipped));
            return items;
        }

        items.Add(Passed(BackupPreCheckIds.ServerReachable, SqlServerPreCheckReasons.Ok, "The SQL Server instance answered."));
        items.Add(Passed(BackupPreCheckIds.Authentication, SqlServerPreCheckReasons.Ok, "SQL Server accepted the credentials."));

        if (facts.MetadataFailure is not null || facts.Instance is null)
        {
            items.Add(Failed(BackupPreCheckIds.DatabaseExists, SqlServerPreCheckReasons.MetadataUnavailable,
                facts.MetadataFailure?.Message ?? "SQL Server metadata could not be read."));
            items.AddRange(downstream.Skip(1).Select(Skipped));
            return items;
        }

        if (facts.Database is not { } database)
        {
            items.Add(Failed(BackupPreCheckIds.DatabaseExists, SqlServerPreCheckReasons.DatabaseNotFound,
                "The database does not exist on this instance, or the login cannot see it."));
            items.AddRange(downstream.Skip(1).Select(Skipped));
            return items;
        }

        var instance = facts.Instance;
        var capabilities = SqlServerCapabilityEvaluator.EvaluateDatabase(SqlServerCapabilityEvaluator.EvaluateInstance(instance), database);

        items.Add(Passed(BackupPreCheckIds.DatabaseExists, SqlServerPreCheckReasons.Ok, "The database exists."));
        items.Add(CheckState(database));
        items.Add(CheckRecoveryModel(database));
        items.Add(FromCapability(BackupPreCheckIds.BackupCapability, SqlServerCapabilityIds.FullBackup, capabilities));
        // The permission function cannot be trusted for a database that is not online (see the provider), and such a database
        // already failed the state check, so the permission is not evaluated rather than reported as denied.
        items.Add(database.State == SqlServerDatabaseState.Online
            ? CheckPermission(facts.Permission)
            : Skipped(BackupPreCheckIds.BackupPermission));

        if (database.IsTransparentDataEncrypted == true)
        {
            items.Add(new BackupPreCheckItem(EncryptionCheckId, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DatabaseTdeEnabled,
                "The database uses Transparent Data Encryption. The backup is encrypted with the server certificate; a restore elsewhere needs that certificate and its key."));
        }

        if (request.StagingPath is { } stagingPath)
        {
            items.Add(CheckStagingPathFormat(stagingPath, instance.HostPlatform));
        }

        foreach (var requested in request.RequestedCapabilities)
        {
            items.Add(FromCapability(BackupPreCheckIds.RequestedCapability(requested), requested, capabilities));
        }

        return items;
    }

    /// <summary>The checks that depend on the database being found, in report order.</summary>
    private static IReadOnlyList<string> DownstreamCheckIds(FullBackupPreCheckRequest request)
    {
        var ids = new List<string>
        {
            BackupPreCheckIds.DatabaseExists,
            BackupPreCheckIds.DatabaseState,
            RecoveryModelCheckId,
            BackupPreCheckIds.BackupCapability,
            BackupPreCheckIds.BackupPermission
        };

        if (request.StagingPath is not null)
        {
            ids.Add(BackupPreCheckIds.StagingPathFormat);
        }

        ids.AddRange(request.RequestedCapabilities.Select(BackupPreCheckIds.RequestedCapability));
        return ids;
    }

    // Policy for a normal full backup: only an ONLINE database qualifies. Every other state is either unusable or transient,
    // and an unrecognised state is refused rather than assumed fine.
    private static BackupPreCheckItem CheckState(SqlServerDatabaseInfo database)
    {
        const string id = BackupPreCheckIds.DatabaseState;
        switch (database.State)
        {
            case SqlServerDatabaseState.Online:
                return database.UserAccess switch
                {
                    SqlServerUserAccess.SingleUser => new(id, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DatabaseSingleUser,
                        "The database is in SINGLE_USER mode; the backup fails if another session holds the only connection."),
                    SqlServerUserAccess.RestrictedUser => new(id, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DatabaseRestrictedUser,
                        "The database is in RESTRICTED_USER mode; only privileged principals can use it."),
                    _ => Passed(id, SqlServerPreCheckReasons.DatabaseOnline, "The database is online.")
                };
            case SqlServerDatabaseState.Offline:
                return Failed(id, SqlServerPreCheckReasons.DatabaseOffline, "The database is offline and cannot be backed up.");
            case SqlServerDatabaseState.Restoring:
                return Failed(id, SqlServerPreCheckReasons.DatabaseRestoring, "The database is being restored and cannot be backed up.");
            case SqlServerDatabaseState.Recovering:
                return Failed(id, SqlServerPreCheckReasons.DatabaseRecovering, "The database is recovering; retry when it is online.");
            case SqlServerDatabaseState.RecoveryPending:
                return Failed(id, SqlServerPreCheckReasons.DatabaseRecoveryPending, "The database is in recovery-pending state and cannot be backed up.");
            case SqlServerDatabaseState.Suspect:
                return Failed(id, SqlServerPreCheckReasons.DatabaseSuspect, "The database is marked suspect and cannot be backed up.");
            case SqlServerDatabaseState.Emergency:
                return Failed(id, SqlServerPreCheckReasons.DatabaseEmergency, "The database is in emergency mode; it is not eligible for a normal full backup.");
            default:
                return Failed(id, SqlServerPreCheckReasons.DatabaseStateUnknown, "The database state is not recognised, so it is not assumed to be backupable.");
        }
    }

    // A full backup does not depend on the recovery model; the item exists so later log-backup decisions are known to have their input.
    private static BackupPreCheckItem CheckRecoveryModel(SqlServerDatabaseInfo database) =>
        database.RecoveryModel == SqlServerRecoveryModel.Unknown
            ? new(RecoveryModelCheckId, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.RecoveryModelUnknown,
                "The recovery model could not be read; transaction log backup decisions need it.")
            : Passed(RecoveryModelCheckId, SqlServerPreCheckReasons.RecoveryModelKnown, "The recovery model is known.");

    private static BackupPreCheckItem CheckPermission(SqlServerBackupPermission permission) => permission switch
    {
        SqlServerBackupPermission.Granted => Passed(BackupPreCheckIds.BackupPermission, SqlServerPreCheckReasons.PermissionGranted,
            "The login holds BACKUP DATABASE on this database."),
        SqlServerBackupPermission.Denied => Failed(BackupPreCheckIds.BackupPermission, SqlServerPreCheckReasons.PermissionDenied,
            "The login does not hold BACKUP DATABASE on this database (sysadmin, db_owner, db_backupoperator or an explicit grant is required)."),
        _ => new(BackupPreCheckIds.BackupPermission, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.PermissionUnknown,
            "SQL Server did not report whether the login may back up this database; the backup itself will tell.")
    };

    private static BackupPreCheckItem FromCapability(string checkId, DatabaseCapabilityId id, DatabaseCapabilitySet capabilities)
    {
        if (!capabilities.TryGet(id, out var capability))
        {
            return Failed(checkId, SqlServerPreCheckReasons.CapabilityNotRecognized,
                $"'{id.Value}' is not a capability this provider evaluates.");
        }

        return capability.Support switch
        {
            DatabaseCapabilitySupport.Supported => Passed(checkId, SqlServerPreCheckReasons.CapabilitySupported, capability.Explanation),
            DatabaseCapabilitySupport.Unsupported => Failed(checkId, SqlServerPreCheckReasons.CapabilityUnsupported, capability.Explanation),
            _ => new(checkId, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.CapabilityUnknown, capability.Explanation)
        };
    }

    /// <summary>
    /// Verifies the shape of a server-side path only: absolute, no traversal, in the style of the server's operating system.
    /// It deliberately says nothing about whether the database service account can write there.
    /// </summary>
    internal static BackupPreCheckItem CheckStagingPathFormat(DatabaseServerPath path, SqlServerHostPlatform platform)
    {
        const string id = BackupPreCheckIds.StagingPathFormat;
        var value = path.Value;

        if (value.Any(char.IsControl) || value.Split('/', '\\').Contains(".."))
        {
            return Failed(id, SqlServerPreCheckReasons.StagingPathInvalid,
                "The staging path contains control characters or parent-directory segments.");
        }

        var windowsStyle = IsWindowsAbsolute(value);
        var posixStyle = value.StartsWith('/');

        if (!windowsStyle && !posixStyle)
        {
            return Failed(id, SqlServerPreCheckReasons.StagingPathInvalid, "The staging path must be absolute on the database server.");
        }

        const string formatOnly = " Whether the SQL Server service account can write there is not checked yet.";
        return platform switch
        {
            SqlServerHostPlatform.Windows when !windowsStyle => Failed(id, SqlServerPreCheckReasons.StagingPathWrongStyle,
                "The server runs on Windows; the staging path must be a drive or UNC path."),
            SqlServerHostPlatform.Linux when !posixStyle => Failed(id, SqlServerPreCheckReasons.StagingPathWrongStyle,
                "The server runs on Linux; the staging path must start with '/'."),
            SqlServerHostPlatform.Unknown => new(id, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.StagingPathPlatformUnknown,
                "The server's operating system is unknown, so the path style could not be matched to it." + formatOnly),
            _ => Passed(id, SqlServerPreCheckReasons.StagingPathValid, "The staging path is well-formed for the server's operating system." + formatOnly)
        };
    }

    private static bool IsWindowsAbsolute(string value) =>
        (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/')
        || (value.Length >= 3 && value.StartsWith(@"\\", StringComparison.Ordinal) && value[2] != '\\');

    private static string ConnectReason(DatabaseFailureKind kind) => kind switch
    {
        DatabaseFailureKind.Unreachable => SqlServerPreCheckReasons.ServerUnreachable,
        DatabaseFailureKind.Timeout => SqlServerPreCheckReasons.ConnectionTimeout,
        DatabaseFailureKind.InvalidConfiguration => SqlServerPreCheckReasons.ConnectionSettingsInvalid,
        _ => SqlServerPreCheckReasons.ConnectionFailed
    };

    private static BackupPreCheckItem Passed(string id, string reason, string message) => new(id, BackupPreCheckStatus.Passed, reason, message);

    private static BackupPreCheckItem Failed(string id, string reason, string message) => new(id, BackupPreCheckStatus.Failed, reason, message);

    private static BackupPreCheckItem Skipped(string id) =>
        new(id, BackupPreCheckStatus.Skipped, SqlServerPreCheckReasons.PrerequisiteFailed, "Not evaluated because an earlier check failed.");
}
