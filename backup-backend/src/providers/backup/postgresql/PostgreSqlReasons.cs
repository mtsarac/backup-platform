namespace BackupPlatform.BackupProviders.PostgreSql;

/// <summary>Stable machine-readable reason codes of the PostgreSQL provider (pre-check items, failures). Messages are fixed sentences; these never change.</summary>
public static class PostgreSqlReasons
{
    public const string ServerUnreachable = "server-unreachable";
    public const string ConnectionTimeout = "connection-timeout";
    public const string ConnectionSettingsInvalid = "connection-settings-invalid";
    public const string AuthenticationFailed = "authentication-failed";
    public const string PermissionDenied = "permission-denied";
    public const string MetadataUnavailable = "metadata-unavailable";
    public const string DatabaseNotFound = "database-not-found";
    public const string DatabaseNameInvalid = "database-name-invalid";
    public const string DatabaseNotConnectable = "database-not-connectable";
    public const string ServerVersionUnsupported = "server-version-unsupported";

    public const string ToolsMissing = "tools-missing";
    public const string ToolsUnusable = "tools-unusable";
    public const string ToolVersionTooOld = "tool-version-too-old";

    public const string BackupNotSupported = "backup-not-supported";
    public const string RequiredOptionUnsupported = "required-option-unsupported";
    public const string TablesNotReadable = "tables-not-readable";
    public const string RowSecurityNotBypassed = "row-security-not-bypassed";

    public const string StagingAgentDirectoryMissing = "staging-agent-directory-missing";
    public const string StagedFileExists = "staged-file-exists";
    public const string StagedFileMissing = "staged-file-missing";
    public const string StagingWriteFailed = "staging-write-failed";
    public const string StagingDiskFull = "staging-disk-full";
    public const string StagingInsufficientSpace = "staging-insufficient-space";

    public const string DumpFailed = "dump-failed";
    public const string DumpTimeout = "dump-timeout";
    public const string DumpLockTimeout = "dump-lock-timeout";
    public const string ConnectionLost = "connection-lost";
    public const string VerifyFailed = "verify-failed";
    public const string VerifyTimeout = "verify-timeout";
    public const string ArchiveInvalid = "archive-invalid";
    public const string ArchiveVersionUnsupported = "archive-version-unsupported";
    public const string ArchiveNotPostgreSql = "archive-not-postgresql";
    public const string AgentFileMissing = "agent-file-missing";
    public const string UnexpectedError = "unexpected-error";

    public const string RestoreRolePrivilege = "restore-role-privilege";
    public const string RestoreFailed = "restore-failed";
    public const string RestoreTimeout = "restore-timeout";
    public const string TargetCreateFailed = "target-create-failed";
    public const string TargetSwapFailed = "target-swap-failed";
    public const string ExtensionUnavailable = "extension-unavailable";
    public const string RoleMissing = "role-missing";
}

/// <summary>Warning codes of the PostgreSQL provider.</summary>
public static class PostgreSqlWarningCodes
{
    public const string ChecksumUnavailable = "checksum-unavailable";
    public const string StagingCleanupFailed = "staging-cleanup-failed";
    public const string OwnershipNotPreserved = "ownership-not-preserved";
    public const string DatabaseSettingsNotPreserved = "database-settings-not-preserved";
    public const string OwnerNotRestored = "database-owner-not-restored";
    public const string ConnectionsTerminated = "connections-terminated";
    public const string TargetCleanupFailed = "target-cleanup-failed";
    public const string RestoredDataRetained = "restored-data-retained";
    public const string StatisticsNotRefreshed = "statistics-not-refreshed";
    public const string ClientEncodingUnknown = "client-encoding-unknown";
    public const string LargeObjectPrivilegeUnverified = "large-object-privilege-unverified";
}
