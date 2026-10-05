namespace BackupPlatform.BackupProviders.MSSQL.Backup;

/// <summary>Stable reason codes of failed executions (the <c>ReasonCode</c> of a <c>BackupFailure</c>).</summary>
public static class SqlServerBackupReasons
{
    public const string DatabaseNotFound = "database-not-found";
    public const string DatabaseNotOnline = "database-not-online";
    public const string BackupNotSupported = "backup-not-supported";
    public const string RequiredOptionUnsupported = "required-option-unsupported";
    public const string ServerUnavailable = "server-unavailable";
    public const string AuthenticationFailed = "authentication-failed";
    public const string StagedFileMissing = "staged-file-missing";
    public const string VerifyPermissionDenied = "verify-permission-denied";
    public const string StagedFileExists = "staged-file-exists";
    public const string StagingAgentDirectoryMissing = "staging-agent-directory-missing";
    public const string StagingMappingMismatch = "staging-mapping-mismatch";
    public const string StagingWriteFailed = "staging-write-failed";
    public const string PermissionDenied = "permission-denied";
    public const string BackupTimeout = "backup-timeout";
    public const string ConnectionLost = "connection-lost";
    public const string DatabaseCorruptionDetected = "database-corruption-detected";
    public const string BackupFailed = "backup-failed";
    public const string VerifyTimeout = "verify-timeout";
    public const string BackupFileUnreadable = "backup-file-unreadable";
    public const string BackupMediaInvalid = "backup-media-invalid";
    public const string BackupChecksumMismatch = "backup-checksum-mismatch";
    public const string BackupVersionIncompatible = "backup-version-incompatible";
    public const string VerifyFailed = "verify-failed";
    public const string UnexpectedError = "unexpected-error";
}

internal enum SqlServerStatement
{
    Backup,
    Verify
}

/// <summary>
/// Maps the SQL Server error numbers of a failed BACKUP / RESTORE VERIFYONLY to a reason code and a fixed user-safe sentence. Pure.
/// A failed statement usually raises several errors (the cause first, then the generic "terminating abnormally" 3013), so the
/// numbers are scanned in order and the first one with a known meaning decides; an unknown set yields the generic reason. Only
/// numbers observed against SQL Server or documented by Microsoft are mapped; engine text is never used (it is localised, may
/// contain paths, and is logged by the caller instead).
/// </summary>
internal static class SqlServerBackupFailureClassifier
{
    // Permission denied on object/database/server, or the database is not accessible under the current security context.
    private static readonly HashSet<int> PermissionErrors = [229, 230, 262, 297, 300, 916];

    // Broken or lost transport while the statement was running.
    private static readonly HashSet<int> ConnectionLostErrors = [-1, 0, 64, 121, 233, 1231, 10053, 10054, 10060, 10061];

    // 3201: cannot open the backup device (observed: Operating system error 5, access denied). 3202: write on the device failed (disk full, I/O error).
    private static readonly HashSet<int> StagingWriteErrors = [3201, 3202];

    // 3043: BACKUP detected an error on a page; 823/824/825: the engine could not read a page consistently.
    private static readonly HashSet<int> CorruptionErrors = [3043, 823, 824, 825];

    // 3241/3242/3243: the file is not a (well-formed) backup media family (3241 observed with a garbled file).
    private static readonly HashSet<int> MediaErrors = [3241, 3242, 3243];

    // 3187: RESTORE detected an error on a page; 3043 as above.
    private static readonly HashSet<int> ChecksumErrors = [3187, 3043];

    public static (string Reason, string Message) Classify(SqlServerStatement statement, IReadOnlyCollection<int> errorNumbers)
    {
        ArgumentNullException.ThrowIfNull(errorNumbers);

        foreach (var number in errorNumbers)
        {
            if (Match(statement, number) is { } match)
            {
                return match;
            }
        }

        return statement == SqlServerStatement.Backup
            ? (SqlServerBackupReasons.BackupFailed, "SQL Server reported an error while taking the backup. Details are in the agent log.")
            : (SqlServerBackupReasons.VerifyFailed, "SQL Server could not verify the backup file. Details are in the agent log.");
    }

    private static (string Reason, string Message)? Match(SqlServerStatement statement, int number)
    {
        if (number == -2)
        {
            return statement == SqlServerStatement.Backup
                ? (SqlServerBackupReasons.BackupTimeout, "The backup did not finish within the configured time limit and was stopped.")
                : (SqlServerBackupReasons.VerifyTimeout, "The backup verification did not finish within the configured time limit and was stopped.");
        }

        if (PermissionErrors.Contains(number))
        {
            return (SqlServerBackupReasons.PermissionDenied, statement == SqlServerStatement.Backup
                ? "The SQL Server login lacks the permission to back up this database."
                : "The SQL Server login lacks the permission to verify the backup file.");
        }

        if (ConnectionLostErrors.Contains(number))
        {
            return (SqlServerBackupReasons.ConnectionLost, "The connection to SQL Server was lost while the statement was running.");
        }

        if (statement == SqlServerStatement.Backup)
        {
            if (number == 911)
            {
                return (SqlServerBackupReasons.DatabaseNotFound, "The database does not exist on this instance.");
            }

            if (StagingWriteErrors.Contains(number))
            {
                return (SqlServerBackupReasons.StagingWriteFailed,
                    "SQL Server could not write the backup file to the staging directory. Check that the directory exists on the server, that the SQL Server service account can write to it and that the disk has free space.");
            }

            if (CorruptionErrors.Contains(number))
            {
                return (SqlServerBackupReasons.DatabaseCorruptionDetected,
                    "SQL Server detected damaged pages in the database while backing it up. Run a consistency check on the database.");
            }

            return null;
        }

        if (number == 3203)
        {
            return (SqlServerBackupReasons.BackupFileUnreadable, "SQL Server could not read the backup file; it may be truncated or damaged.");
        }

        if (number == 3201)
        {
            return (SqlServerBackupReasons.BackupFileUnreadable,
                "SQL Server could not open the backup file in the staging directory. Check the server's access to it.");
        }

        if (MediaErrors.Contains(number))
        {
            return (SqlServerBackupReasons.BackupMediaInvalid, "The file is not a valid, complete SQL Server backup.");
        }

        if (ChecksumErrors.Contains(number) || CorruptionErrors.Contains(number))
        {
            return (SqlServerBackupReasons.BackupChecksumMismatch, "SQL Server found damaged data in the backup file.");
        }

        if (number == 3169)
        {
            return (SqlServerBackupReasons.BackupVersionIncompatible, "The backup was taken by a SQL Server version this server cannot read.");
        }

        return null;
    }
}
