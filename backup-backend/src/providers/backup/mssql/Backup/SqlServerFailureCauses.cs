using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;

namespace BackupPlatform.BackupProviders.MSSQL.Backup;

/// <summary>
/// Maps SQL Server reason codes to the engine-independent <see cref="BackupFailureCause"/>. Pure. A reason without an entry is
/// <see cref="BackupFailureCause.Unclassified"/>: a database state such as offline or restoring is a fact about the database, not a
/// cause this provider claims to understand.
/// </summary>
internal static class SqlServerFailureCauses
{
    public static BackupFailureCause ForPreCheckReason(string reasonCode) => reasonCode switch
    {
        SqlServerPreCheckReasons.ServerUnreachable or SqlServerPreCheckReasons.ConnectionFailed
            or SqlServerPreCheckReasons.MetadataUnavailable => BackupFailureCause.DatabaseUnavailable,
        SqlServerPreCheckReasons.ConnectionTimeout => BackupFailureCause.Timeout,
        SqlServerPreCheckReasons.ConnectionSettingsInvalid => BackupFailureCause.InvalidConfiguration,
        SqlServerPreCheckReasons.AuthenticationFailed => BackupFailureCause.AuthenticationFailed,
        SqlServerPreCheckReasons.DatabaseNotFound => BackupFailureCause.DatabaseNotFound,
        SqlServerPreCheckReasons.PermissionDenied or SqlServerPreCheckReasons.VerifyPermissionDenied => BackupFailureCause.PermissionDenied,
        SqlServerPreCheckReasons.CapabilityUnsupported or SqlServerPreCheckReasons.CapabilityNotRecognized => BackupFailureCause.Unsupported,
        SqlServerPreCheckReasons.StagingPathInvalid or SqlServerPreCheckReasons.StagingPathWrongStyle
            or SqlServerPreCheckReasons.StagingAgentDirectoryMissing or SqlServerPreCheckReasons.StagingAgentNotListable
            or SqlServerPreCheckReasons.StagingAgentNotWritable or SqlServerPreCheckReasons.StagingServerCannotWrite
            or SqlServerPreCheckReasons.StagingServerDirectoryMissing or SqlServerPreCheckReasons.StagingMappingMismatch
            or SqlServerPreCheckReasons.DiskSpaceInsufficient => BackupFailureCause.StagingInvalid,
        _ => BackupFailureCause.Unclassified
    };

    public static BackupFailureCause ForBackupReason(string reasonCode) => reasonCode switch
    {
        SqlServerBackupReasons.ServerUnavailable or SqlServerBackupReasons.ConnectionLost => BackupFailureCause.DatabaseUnavailable,
        SqlServerBackupReasons.BackupTimeout or SqlServerBackupReasons.VerifyTimeout => BackupFailureCause.Timeout,
        SqlServerBackupReasons.AuthenticationFailed => BackupFailureCause.AuthenticationFailed,
        SqlServerBackupReasons.PermissionDenied or SqlServerBackupReasons.VerifyPermissionDenied => BackupFailureCause.PermissionDenied,
        SqlServerBackupReasons.DatabaseNotFound => BackupFailureCause.DatabaseNotFound,
        SqlServerBackupReasons.BackupNotSupported or SqlServerBackupReasons.RequiredOptionUnsupported => BackupFailureCause.Unsupported,
        SqlServerBackupReasons.StagingAgentDirectoryMissing or SqlServerBackupReasons.StagingMappingMismatch
            or SqlServerBackupReasons.StagingWriteFailed or SqlServerBackupReasons.StagedFileExists => BackupFailureCause.StagingInvalid,
        _ => BackupFailureCause.Unclassified
    };
}
