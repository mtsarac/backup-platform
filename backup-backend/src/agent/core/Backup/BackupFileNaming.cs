using System.Globalization;
using System.Text;

namespace BackupPlatform.Agent.Core.Backup;

/// <summary>
/// Names staged backup files. The name is derived from untrusted input (the database name), so it is reduced to a safe, portable
/// alphabet; uniqueness comes from the timestamp plus a random part, never from the database name, because no staged file
/// may ever be appended to or silently overwritten.
/// </summary>
internal static class BackupFileNaming
{
    private const int MaxDatabaseNameLength = 48;

    public static string StagedFileName(string databaseName, DateTimeOffset startedAt, Guid unique)
    {
        ArgumentNullException.ThrowIfNull(databaseName);
        var safe = new StringBuilder(Math.Min(databaseName.Length, MaxDatabaseNameLength));
        foreach (var character in databaseName)
        {
            if (safe.Length == MaxDatabaseNameLength)
            {
                break;
            }

            safe.Append(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' ? character : '_');
        }

        if (safe.Length == 0)
        {
            safe.Append("database");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{safe}_{startedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_{unique.ToString("N", CultureInfo.InvariantCulture)[..8]}_FULL.bak");
    }
}

/// <summary>Stable machine-readable reason codes the pipeline itself adds to <see cref="BackupPlatform.BackupProviders.Abstractions.Database.BackupFailure"/>; providers contribute their own.</summary>
public static class BackupPipelineReasons
{
    public const string EngineNotAvailable = "engine-not-available";
    public const string StorageNotAvailable = "storage-not-available";
    public const string PreCheckError = "pre-check-error";
    public const string ProviderError = "provider-error";
    public const string StagedFileMissing = "staged-file-missing";
    public const string StagedFileChanged = "staged-file-changed";
    public const string SourceHashFailed = "source-hash-failed";
    public const string ArtifactPreparationFailed = "artifact-preparation-failed";
    public const string ArtifactFileMissing = "artifact-file-missing";
    public const string ArtifactHashFailed = "artifact-hash-failed";
    public const string StorageInvalidKey = "storage-invalid-key";
    public const string StorageAlreadyExists = "storage-already-exists";
    public const string StorageIntegrityMismatch = "storage-integrity-mismatch";
    public const string StorageInsufficientSpace = "storage-insufficient-space";
    public const string StorageAccessDenied = "storage-access-denied";
    public const string StorageNotSupported = "storage-not-supported";
    public const string StorageInvalidConfiguration = "storage-invalid-configuration";
    public const string StorageIoError = "storage-io-error";
    public const string StorageUnexpected = "storage-unexpected";
    public const string ArtifactFileUnreadable = "artifact-file-unreadable";
    public const string ReplicaMissing = "replica-missing";
    public const string ReplicaSizeMismatch = "replica-size-mismatch";
    public const string ReplicaChecksumMismatch = "replica-checksum-mismatch";
    public const string ReplicaInconclusive = "replica-verification-inconclusive";
    public const string ReplicaVerificationError = "replica-verification-error";
    public const string StagingCleanupFailed = "staging-cleanup-failed";
    public const string ReplicaCleanupFailed = "replica-cleanup-failed";
    public const string Cancelled = "cancelled";
    public const string Unexpected = "unexpected";
}
