using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;
using Microsoft.Data.SqlClient;

namespace BackupPlatform.BackupProviders.MSSQL.Restore;

/// <summary>
/// Stable reason codes of the SQL Server restore: failed pre-check / plan items and <see cref="RestoreFailure.ReasonCode"/>. The
/// engine-independent conditions use <see cref="RestoreReasonCodes"/> and are not repeated here. Codes whose meaning is the same as in the
/// backup flow share its value, so one vocabulary describes "SQL Server could not read this file" for both directions.
/// </summary>
public static class SqlServerRestoreReasons
{
    // ---- failures of a statement ---------------------------------------------------------------------------------------------
    public const string BackupFileUnreadable = SqlServerBackupReasons.BackupFileUnreadable;
    public const string BackupMediaInvalid = SqlServerBackupReasons.BackupMediaInvalid;
    public const string BackupChecksumMismatch = SqlServerBackupReasons.BackupChecksumMismatch;
    public const string BackupEncryptedUnsupported = "backup-encrypted-unsupported";
    public const string BackupEncryptionKeyMissing = "backup-encryption-key-missing";
    public const string InspectFailed = "inspect-failed";
    public const string InspectTimeout = "inspect-timeout";
    public const string VerifyFailed = SqlServerBackupReasons.VerifyFailed;
    public const string VerifyTimeout = SqlServerBackupReasons.VerifyTimeout;
    public const string RestoreFailed = "restore-failed";
    public const string RestoreTimeout = "restore-timeout";
    public const string RestoreDestinationInvalid = "restore-destination-invalid";
    public const string RestoreDestinationExists = "restore-destination-exists";
    public const string TargetAccessChangeFailed = "target-access-change-failed";
    public const string RestoreCancelled = "restore-cancelled";
    public const string TargetNotOnline = "target-not-online";
    public const string PermissionDenied = SqlServerBackupReasons.PermissionDenied;
    public const string ConnectionLost = SqlServerBackupReasons.ConnectionLost;
    public const string ServerUnavailable = SqlServerBackupReasons.ServerUnavailable;
    public const string AuthenticationFailed = SqlServerBackupReasons.AuthenticationFailed;
    public const string ConnectionTimeout = SqlServerPreCheckReasons.ConnectionTimeout;
    public const string ConnectionSettingsInvalid = SqlServerPreCheckReasons.ConnectionSettingsInvalid;
    public const string UnexpectedError = SqlServerBackupReasons.UnexpectedError;

    // ---- pre-check / plan items ----------------------------------------------------------------------------------------------
    public const string Ok = SqlServerPreCheckReasons.Ok;
    public const string DatabaseNameInvalid = "database-name-invalid";
    public const string TargetNotProtected = "target-not-protected";
    public const string TargetProtectionUnknown = "target-protection-unknown";
    public const string RestorePermissionGranted = "restore-permission-granted";
    public const string RestorePermissionDenied = "restore-permission-denied";
    public const string RestorePermissionUnknown = "restore-permission-unknown";
    public const string SessionsNone = "sessions-none";
    public const string SessionsWillBeTerminated = "sessions-will-be-terminated";
    public const string SessionsUnknown = "sessions-unknown";
    public const string DirectoriesKnown = "target-directories-known";
    public const string DirectoriesFallback = "target-directories-fallback";
    public const string DirectoriesUnknown = "target-directories-unknown";
    public const string ServerVersionUnknown = "server-version-unknown";
    public const string DiskSpaceSufficient = SqlServerPreCheckReasons.DiskSpaceSufficient;
    public const string DiskSpaceUnknown = SqlServerPreCheckReasons.DiskSpaceUnknown;
    public const string BackupTypeSupported = "backup-type-supported";
    public const string VersionCompatible = "backup-version-compatible";
    public const string VersionNewerBuild = "backup-from-newer-build";
    public const string VersionUnknown = "backup-version-unknown";
    public const string VersionOld = "backup-version-old";
    public const string EditionCompatible = "edition-compatible";
    public const string EditionUnknown = "edition-compatibility-unknown";
    public const string TargetRelated = "target-related-to-backup";
    public const string TargetUnrelated = "target-unrelated-to-backup";
    public const string TargetRelationUnknown = "target-relation-unknown";
    public const string FileTypesSupported = "file-types-supported";
    public const string FileMappingReady = "file-mapping-ready";
    public const string ExistingFilesUnknown = "existing-files-unknown";
}

/// <summary>The statement that failed; the same error number means different things for different statements.</summary>
internal enum SqlServerRestoreStatement
{
    HeaderOnly,
    FileListOnly,
    VerifyOnly,
    Restore,
    /// <summary><c>ALTER DATABASE ... SET SINGLE_USER / MULTI_USER</c>.</summary>
    AccessChange
}

internal sealed record SqlServerRestoreFailureInfo(RestoreFailureKind Kind, string ReasonCode, string Message, BackupFailureCause Cause);

/// <summary>
/// Maps the SQL Server error numbers of a failed restore statement to a reason code, a failure kind, a cause and a fixed user-safe
/// sentence. Pure. A failed statement raises several errors (the cause first, then generic ones such as 3119 and 3013), so the numbers are
/// scanned in order and the first one with a known meaning decides. Only numbers whose text is in the server's own message catalog and
/// that were observed or are documented are mapped; engine text is never used (it is localised, contains paths and is logged by the caller).
/// <para>
/// Observed against SQL Server 2025 on Linux: 3201 (file missing or unreadable), 3254 (a file that is not a backup), 3241 (garbled media),
/// 3203 (truncated), 3156 together with 3634 (destination directory unusable), 1834 and 3142 (destination file already exists), 5052 (ALTER
/// DATABASE while RESTORING), 3159 (tail-log refusal) and 50001 (this provider's own target-exists guard).
/// </para>
/// </summary>
internal static class SqlServerRestoreFailureClassifier
{
    private static readonly HashSet<int> PermissionErrors = [229, 230, 262, 297, 300, 916];
    private static readonly HashSet<int> ConnectionLostErrors = [-1, 0, 64, 121, 233, 1231, 10053, 10054, 10060, 10061];
    private static readonly HashSet<int> MediaErrors = [3241, 3242, 3243, 3254];

    // 3183: RESTORE detected an error on a page as read from the backup set; 3189: damage to the backup set; 3043: a BACKUP-time page error.
    // 3187 is deliberately absent: it is "RESTORE WITH CHECKSUM cannot be specified because the backup set has no checksums", a request error.
    private static readonly HashSet<int> DamageErrors = [3183, 3189, 3043];

    // 33111 / 33049: the certificate or key that protects the backup (or a TDE database) is not on this server.
    private static readonly HashSet<int> EncryptionKeyErrors = [33111, 33049];

    public static SqlServerRestoreFailureInfo Classify(SqlServerRestoreStatement statement, IReadOnlyCollection<int> errorNumbers)
    {
        ArgumentNullException.ThrowIfNull(errorNumbers);

        foreach (var number in errorNumbers)
        {
            if (Match(statement, number) is { } match)
            {
                return match;
            }
        }

        return statement switch
        {
            SqlServerRestoreStatement.HeaderOnly or SqlServerRestoreStatement.FileListOnly => new(
                RestoreFailureKind.BackupInvalid, SqlServerRestoreReasons.InspectFailed,
                "SQL Server could not read the backup file. Details are in the agent log.", BackupFailureCause.Unclassified),
            SqlServerRestoreStatement.VerifyOnly => new(
                RestoreFailureKind.BackupInvalid, SqlServerRestoreReasons.VerifyFailed,
                "SQL Server could not verify the backup file. Details are in the agent log.", BackupFailureCause.Unclassified),
            SqlServerRestoreStatement.AccessChange => new(
                RestoreFailureKind.SqlRestoreFailed, SqlServerRestoreReasons.TargetAccessChangeFailed,
                "SQL Server could not change the access mode of the target database. Details are in the agent log.", BackupFailureCause.Unclassified),
            _ => new(
                RestoreFailureKind.SqlRestoreFailed, SqlServerRestoreReasons.RestoreFailed,
                "SQL Server reported an error while restoring the database. Details are in the agent log.", BackupFailureCause.Unclassified)
        };
    }

    /// <summary>
    /// Classifies a failure that reached the caller as a <see cref="DatabaseProviderException"/> (everything <c>SqlServerSession.QueryAsync</c> and
    /// the connection do): the engine's numbers are read from the inner <see cref="SqlException"/>, and a transport failure without one falls back to its kind.
    /// </summary>
    public static SqlServerRestoreFailureInfo Classify(SqlServerRestoreStatement statement, DatabaseProviderException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.InnerException is SqlException sql)
        {
            return Classify(statement, ErrorNumbers(sql));
        }

        return exception.Kind switch
        {
            DatabaseFailureKind.Timeout => Classify(statement, [-2]),
            DatabaseFailureKind.PermissionDenied => Classify(statement, [262]),
            DatabaseFailureKind.Unreachable => Classify(statement, [-1]),
            _ => Classify(statement, [])
        };
    }

    /// <summary>A failure to connect (or to read the server's own metadata before any statement): the kind of the failure decides.</summary>
    public static SqlServerRestoreFailureInfo FromConnection(DatabaseProviderException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.Kind switch
        {
            DatabaseFailureKind.AuthenticationFailed => new(RestoreFailureKind.PreCheckFailed, SqlServerRestoreReasons.AuthenticationFailed, exception.Message, BackupFailureCause.AuthenticationFailed),
            DatabaseFailureKind.PermissionDenied => new(RestoreFailureKind.PreCheckFailed, SqlServerRestoreReasons.PermissionDenied, exception.Message, BackupFailureCause.PermissionDenied),
            DatabaseFailureKind.Timeout => new(RestoreFailureKind.PreCheckFailed, SqlServerRestoreReasons.ConnectionTimeout, exception.Message, BackupFailureCause.Timeout),
            DatabaseFailureKind.InvalidConfiguration => new(RestoreFailureKind.PreCheckFailed, SqlServerRestoreReasons.ConnectionSettingsInvalid, exception.Message, BackupFailureCause.InvalidConfiguration),
            DatabaseFailureKind.Unreachable => new(RestoreFailureKind.PreCheckFailed, SqlServerRestoreReasons.ServerUnavailable, exception.Message, BackupFailureCause.DatabaseUnavailable),
            _ => new(RestoreFailureKind.PreCheckFailed, SqlServerRestoreReasons.UnexpectedError, exception.Message, BackupFailureCause.Unclassified)
        };
    }

    public static int[] ErrorNumbers(SqlException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Errors.Cast<SqlError>().Select(error => error.Number).ToArray();
    }

    private static SqlServerRestoreFailureInfo? Match(SqlServerRestoreStatement statement, int number)
    {
        var executing = statement is SqlServerRestoreStatement.Restore or SqlServerRestoreStatement.AccessChange;

        if (number == -2)
        {
            var (reason, message) = statement switch
            {
                SqlServerRestoreStatement.Restore => (SqlServerRestoreReasons.RestoreTimeout,
                    "The restore did not finish within the configured time limit and was stopped."),
                SqlServerRestoreStatement.VerifyOnly => (SqlServerRestoreReasons.VerifyTimeout,
                    "The backup verification did not finish within the configured time limit and was stopped."),
                SqlServerRestoreStatement.AccessChange => (SqlServerRestoreReasons.RestoreTimeout,
                    "Changing the access mode of the target database did not finish within the time limit and was stopped."),
                _ => (SqlServerRestoreReasons.InspectTimeout, "Reading the backup file did not finish within the time limit and was stopped.")
            };
            return new(executing ? RestoreFailureKind.SqlRestoreFailed : RestoreFailureKind.PreCheckFailed, reason, message, BackupFailureCause.Timeout);
        }

        if (PermissionErrors.Contains(number))
        {
            return new(
                executing ? RestoreFailureKind.SqlRestoreFailed : RestoreFailureKind.PreCheckFailed,
                SqlServerRestoreReasons.PermissionDenied,
                statement switch
                {
                    SqlServerRestoreStatement.Restore => "The SQL Server login lacks the permission to restore this database.",
                    SqlServerRestoreStatement.AccessChange => "The SQL Server login lacks the permission to change the access mode of the target database.",
                    _ => "The SQL Server login lacks the permission to read backup files (the server-level CREATE ANY DATABASE permission)."
                },
                BackupFailureCause.PermissionDenied);
        }

        if (ConnectionLostErrors.Contains(number))
        {
            return new(
                executing ? RestoreFailureKind.SqlRestoreFailed : RestoreFailureKind.PreCheckFailed,
                SqlServerRestoreReasons.ConnectionLost,
                "The connection to SQL Server was lost while the statement was running.",
                BackupFailureCause.DatabaseUnavailable);
        }

        if (statement == SqlServerRestoreStatement.AccessChange)
        {
            return number switch
            {
                // 5011: no permission to ALTER the database (or it does not exist / is not in a state that allows access checks).
                5011 => new(RestoreFailureKind.SqlRestoreFailed, SqlServerRestoreReasons.PermissionDenied,
                    "The SQL Server login may not change the access mode of the target database, or the database is not in a state that allows it.",
                    BackupFailureCause.PermissionDenied),
                // 5052: ALTER DATABASE is not permitted in this state (observed for RESTORING); 5070: other users hold the database.
                5052 or 5070 => new(RestoreFailureKind.SqlRestoreFailed, SqlServerRestoreReasons.TargetAccessChangeFailed,
                    "The target database is in a state that does not allow changing its access mode, or other sessions still hold it.",
                    BackupFailureCause.Unclassified),
                _ => null
            };
        }

        // File-level and media-level problems of the backup file are the same whichever statement meets them first.
        if (number is 3201 or 3203)
        {
            return new(RestoreFailureKind.BackupInvalid, SqlServerRestoreReasons.BackupFileUnreadable,
                "SQL Server could not open or read the staged backup file. Check that the SQL Server service account can read the staging directory and that the file is complete.",
                BackupFailureCause.StagingInvalid);
        }

        if (MediaErrors.Contains(number))
        {
            return new(RestoreFailureKind.BackupInvalid, SqlServerRestoreReasons.BackupMediaInvalid,
                "The file is not a valid, complete SQL Server backup.", BackupFailureCause.Unclassified);
        }

        if (DamageErrors.Contains(number))
        {
            return new(RestoreFailureKind.BackupInvalid, SqlServerRestoreReasons.BackupChecksumMismatch,
                "SQL Server found damaged data in the backup file.", BackupFailureCause.Unclassified);
        }

        if (number == 3169)
        {
            return new(RestoreFailureKind.PreCheckFailed, RestoreReasonCodes.TargetVersionTooOld,
                "The backup was taken by a newer SQL Server version than this server runs, so it cannot be restored here.", BackupFailureCause.Unsupported);
        }

        if (EncryptionKeyErrors.Contains(number))
        {
            return new(RestoreFailureKind.BackupInvalid, SqlServerRestoreReasons.BackupEncryptionKeyMissing,
                "The backup is protected by a certificate or key that this server does not have, so it cannot be read here.", BackupFailureCause.Unsupported);
        }

        if (statement != SqlServerRestoreStatement.Restore)
        {
            return null;
        }

        return number switch
        {
            // 3101: exclusive access could not be obtained (sessions are connected); the guard 50001 and 3154 / 3159: the database exists.
            3101 => new(RestoreFailureKind.SqlRestoreFailed, RestoreReasonCodes.TargetInUse,
                "The target database is in use by other sessions; the restore needs exclusive access.", BackupFailureCause.Unclassified),
            SqlServerRestoreCommandBuilder.TargetExistsErrorNumber or 3154 or 3159 => new(
                RestoreFailureKind.PreCheckFailed, RestoreReasonCodes.TargetDatabaseExists,
                "A database with this name already exists on the target server, so it was not restored over.", BackupFailureCause.Unclassified),
            // 3156: a file cannot be restored to the chosen path (observed with 3634 for an unusable directory); 5133: directory lookup failed.
            3156 or 5133 => new(RestoreFailureKind.SqlRestoreFailed, SqlServerRestoreReasons.RestoreDestinationInvalid,
                "SQL Server could not create the database files in the chosen directory. Check that the directory exists and that the SQL Server service account can write to it.",
                BackupFailureCause.Unclassified),
            // 1834 / 3142: a file already exists at the destination (observed) and may not be overwritten.
            1834 or 3142 => new(RestoreFailureKind.SqlRestoreFailed, SqlServerRestoreReasons.RestoreDestinationExists,
                "A file already exists at the destination of a restored database file and was not overwritten.", BackupFailureCause.Unclassified),
            3257 => new(RestoreFailureKind.SqlRestoreFailed, RestoreReasonCodes.InsufficientDiskSpace,
                "The target volume does not have enough free space for the restored database files.", BackupFailureCause.Unclassified),
            _ => null
        };
    }
}
