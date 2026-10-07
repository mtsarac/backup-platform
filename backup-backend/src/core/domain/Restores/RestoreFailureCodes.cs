namespace BackupPlatform.Domain.Restores;

/// <summary>
/// The closed vocabulary of restore failure codes. Codes are opaque, provider-independent identifiers: the agent maps engine errors to a
/// code before they reach the backend. Nothing here is retried automatically: a restore changes the target, so repeating one is an explicit
/// new request, never a silent attempt.
/// </summary>
public static class RestoreFailureCodes
{
    // Source side.
    public const string ReplicaUnavailable = "ReplicaUnavailable";
    public const string ArtifactTransferFailed = "ArtifactTransferFailed";
    public const string ChecksumMismatch = "ChecksumMismatch";
    public const string StorageUnavailable = "StorageUnavailable";

    // Backup and compatibility.
    public const string BackupInvalid = "BackupInvalid";
    public const string BackupTypeUnsupported = "BackupTypeUnsupported";
    public const string IncompatibleVersion = "IncompatibleVersion";
    public const string FileMappingFailed = "FileMappingFailed";

    // Target side.
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

    // Outcomes that are not agent-reported failures.
    public const string Cancelled = "Cancelled";
    public const string AgentLost = "AgentLost";
    /// <summary>No agent claimed the restore before it expired, so it was withdrawn without touching the target.</summary>
    public const string NotClaimed = "NotClaimed";

    private static readonly HashSet<string> AgentReported = new(StringComparer.Ordinal)
    {
        ReplicaUnavailable, ArtifactTransferFailed, ChecksumMismatch, StorageUnavailable,
        BackupInvalid, BackupTypeUnsupported, IncompatibleVersion, FileMappingFailed,
        PreCheckFailed, TargetDatabaseExists, TargetDatabaseNotFound, TargetInUse, TargetProtected, InsufficientDiskSpace,
        SqlRestoreFailed, DatabaseUnavailable, OperationTimedOut, AuthenticationFailed, PermissionDenied,
        InvalidConfiguration, UnsupportedCapability, StagingInvalid, InternalError
    };

    /// <summary>Codes an agent may report together with a Failed outcome.</summary>
    public static IReadOnlyCollection<string> AgentReportable => AgentReported;

    public static bool IsAgentReportable(string? code) => code is not null && AgentReported.Contains(code);
}

/// <summary>Why a replica cannot serve a restore. Stable names; the API renders them in snake case.</summary>
public enum RestoreSourceRejection
{
    ReplicaNotFound = 1,
    /// <summary>Deleted, deleting, delete-failed, or past its grace period: the bytes may be gone.</summary>
    ReplicaNotRestorable,
    ReplicaNotVerified,
    /// <summary>No recorded SHA-256 to verify the bytes against.</summary>
    ChecksumUnavailable,
    BackupTypeUnsupported,
    /// <summary>
    /// The replica is agent-local on another agent. Moving it to the target agent would need an explicit artifact transfer, which this
    /// version does not offer, so the replica is not pretended to be reachable.
    /// </summary>
    AgentTransferRequired,
    NoAccessibleReplica
}
