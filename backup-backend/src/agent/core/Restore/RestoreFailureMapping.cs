using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Core.Restore;

/// <summary>
/// The failure codes an agent reports for a restore: the same names as the platform's closed vocabulary (<c>RestoreFailureCodes</c> in the
/// domain, which the agent must not reference). A unit test pins the two lists together.
/// </summary>
public static class RestoreAgentFailureCodes
{
    public const string ReplicaUnavailable = "ReplicaUnavailable";
    public const string ArtifactTransferFailed = "ArtifactTransferFailed";
    public const string ChecksumMismatch = "ChecksumMismatch";
    public const string StorageUnavailable = "StorageUnavailable";
    public const string BackupInvalid = "BackupInvalid";
    public const string BackupTypeUnsupported = "BackupTypeUnsupported";
    public const string IncompatibleVersion = "IncompatibleVersion";
    public const string FileMappingFailed = "FileMappingFailed";
    public const string PreCheckFailed = "PreCheckFailed";
    public const string TargetDatabaseExists = "TargetDatabaseExists";
    public const string TargetDatabaseNotFound = "TargetDatabaseNotFound";
    public const string TargetInUse = "TargetInUse";
    public const string TargetProtected = "TargetProtected";
    public const string InsufficientDiskSpace = "InsufficientDiskSpace";
    public const string SqlRestoreFailed = "SqlRestoreFailed";
    public const string DatabaseUnavailable = "DatabaseUnavailable";
    public const string OperationTimedOut = "OperationTimedOut";
    public const string AuthenticationFailed = "AuthenticationFailed";
    public const string PermissionDenied = "PermissionDenied";
    public const string InvalidConfiguration = "InvalidConfiguration";
    public const string UnsupportedCapability = "UnsupportedCapability";
    public const string StagingInvalid = "StagingInvalid";
    public const string InternalError = "InternalError";

    /// <summary>Not a failure: how a cancelled restore is reported (status Cancelled).</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>Every code an agent may report with a Failed outcome.</summary>
    public static IReadOnlyList<string> Reportable { get; } =
    [
        ReplicaUnavailable, ArtifactTransferFailed, ChecksumMismatch, StorageUnavailable,
        BackupInvalid, BackupTypeUnsupported, IncompatibleVersion, FileMappingFailed,
        PreCheckFailed, TargetDatabaseExists, TargetDatabaseNotFound, TargetInUse, TargetProtected, InsufficientDiskSpace,
        SqlRestoreFailed, DatabaseUnavailable, OperationTimedOut, AuthenticationFailed, PermissionDenied,
        InvalidConfiguration, UnsupportedCapability, StagingInvalid, InternalError
    ];
}

/// <summary>Stable machine-readable reason codes and check identifiers the restore pipeline itself produces; providers contribute their own.</summary>
public static class RestorePipelineReasons
{
    public const string EngineNotAvailable = "engine-not-available";
    public const string StorageNotAvailable = "storage-not-available";
    public const string StorageNotReadable = "storage-not-readable";
    public const string InvalidReplicaKey = "invalid-replica-key";
    public const string ReplicaTenantMismatch = "replica-tenant-mismatch";
    public const string ReplicaMissing = "replica-missing";
    public const string ReplicaSizeMismatch = "replica-size-mismatch";
    public const string ReplicaChecksumMismatch = "replica-checksum-mismatch";
    public const string ReplicaReadFailed = "replica-read-failed";
    public const string StagingMissing = "staging-missing";
    public const string StagingNotWritable = "staging-not-writable";
    public const string StagingInsufficientSpace = "staging-insufficient-space";
    public const string StagingSpaceUnknown = "staging-space-unknown";
    public const string StagingWriteFailed = "staging-write-failed";
    public const string StagedFileMissing = "staged-file-missing";
    public const string PreCheckError = "pre-check-error";
    public const string InspectionError = "inspection-error";
    public const string PlanError = "plan-error";
    public const string ProviderError = "provider-error";
    public const string StagingCleanupFailed = "staging-cleanup-failed";
    public const string ChecksumNotVerified = "checksum-not-verified";
    public const string Cancelled = "cancelled";
    public const string Unexpected = "unexpected";

    /// <summary>Check identifiers of the agent-side pre-check items (the engine-independent staging ids are <see cref="BackupPreCheckIds"/>).</summary>
    public static class Checks
    {
        public const string SourceKey = "source.key";
        public const string SourceStorage = "source.storage";
        public const string SourceReplica = "source.replica";
    }
}

/// <summary>
/// Translates what a restore provider reports into the platform's failure vocabulary, in a fixed order of precedence: the engine-independent
/// <see cref="RestoreReasonCodes"/> first (they name the exact condition), then the coarse <see cref="BackupFailureCause"/> (what kind of
/// problem it was), then the <see cref="RestoreFailureKind"/> (which step failed), and <see cref="RestoreAgentFailureCodes.InternalError"/>
/// when none of them says anything.
/// </summary>
internal static class RestoreFailureMapping
{
    public static string Map(string reasonCode, BackupFailureCause cause, RestoreFailureKind? kind)
    {
        if (FromReason(reasonCode) is { } byReason)
        {
            return byReason;
        }

        if (FromCause(cause) is { } byCause)
        {
            return byCause;
        }

        return kind switch
        {
            RestoreFailureKind.PreCheckFailed => RestoreAgentFailureCodes.PreCheckFailed,
            RestoreFailureKind.BackupInvalid => RestoreAgentFailureCodes.BackupInvalid,
            RestoreFailureKind.SqlRestoreFailed => RestoreAgentFailureCodes.SqlRestoreFailed,
            _ => RestoreAgentFailureCodes.InternalError
        };
    }

    public static string? FromReason(string reasonCode) => reasonCode switch
    {
        RestoreReasonCodes.TargetDatabaseExists => RestoreAgentFailureCodes.TargetDatabaseExists,
        RestoreReasonCodes.TargetDatabaseMissing => RestoreAgentFailureCodes.TargetDatabaseNotFound,
        RestoreReasonCodes.TargetInUse => RestoreAgentFailureCodes.TargetInUse,
        RestoreReasonCodes.TargetProtected => RestoreAgentFailureCodes.TargetProtected,
        RestoreReasonCodes.BackupTypeUnsupported => RestoreAgentFailureCodes.BackupTypeUnsupported,
        RestoreReasonCodes.TargetVersionTooOld => RestoreAgentFailureCodes.IncompatibleVersion,
        RestoreReasonCodes.InsufficientDiskSpace => RestoreAgentFailureCodes.InsufficientDiskSpace,
        RestoreReasonCodes.FileMappingFailed => RestoreAgentFailureCodes.FileMappingFailed,
        _ => null
    };

    public static string? FromCause(BackupFailureCause cause) => cause switch
    {
        BackupFailureCause.DatabaseUnavailable => RestoreAgentFailureCodes.DatabaseUnavailable,
        BackupFailureCause.Timeout => RestoreAgentFailureCodes.OperationTimedOut,
        BackupFailureCause.DatabaseNotFound => RestoreAgentFailureCodes.TargetDatabaseNotFound,
        BackupFailureCause.InvalidConfiguration => RestoreAgentFailureCodes.InvalidConfiguration,
        BackupFailureCause.Unsupported => RestoreAgentFailureCodes.UnsupportedCapability,
        BackupFailureCause.AuthenticationFailed => RestoreAgentFailureCodes.AuthenticationFailed,
        BackupFailureCause.PermissionDenied => RestoreAgentFailureCodes.PermissionDenied,
        BackupFailureCause.StagingInvalid => RestoreAgentFailureCodes.StagingInvalid,
        BackupFailureCause.StorageUnavailable => RestoreAgentFailureCodes.StorageUnavailable,
        _ => null
    };

    /// <summary>The platform code of a storage failure met while reading the replica.</summary>
    public static string FromStorage(StorageFailureKind kind) => kind switch
    {
        StorageFailureKind.NotFound => RestoreAgentFailureCodes.ReplicaUnavailable,
        StorageFailureKind.InsufficientSpace => RestoreAgentFailureCodes.InsufficientDiskSpace,
        StorageFailureKind.IntegrityMismatch => RestoreAgentFailureCodes.ChecksumMismatch,
        StorageFailureKind.NotSupported => RestoreAgentFailureCodes.UnsupportedCapability,
        StorageFailureKind.InvalidConfiguration => RestoreAgentFailureCodes.InvalidConfiguration,
        _ => RestoreAgentFailureCodes.StorageUnavailable
    };
}
