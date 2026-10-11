namespace BackupPlatform.Domain.Backups;

public static class ReplicaRepairFailureCodes
{
    public const string SourceInaccessible = "SourceInaccessible";
    public const string SourceChecksumMismatch = "SourceChecksumMismatch";
    public const string DestinationReadbackFailed = "DestinationReadbackFailed";
    public const string StorageWriteFailed = "StorageWriteFailed";
    public const string StorageUnavailable = "StorageUnavailable";
    public const string NoVerifiedSourceAvailable = "NoVerifiedSourceAvailable";
    public const string UnsupportedSource = "UnsupportedSource";
    public const string QuotaExceeded = "QuotaExceeded";
    public const string Cancelled = "Cancelled";
    public const string OperationTimedOut = "OperationTimedOut";
    public const string LeaseLost = "LeaseLost";
    public const string ArtifactExpired = "ArtifactExpired";
    public const string DestinationNotConfigured = "DestinationNotConfigured";
    public const string AlreadyVerified = "AlreadyVerified";
    public const string InternalError = "InternalError";
}
