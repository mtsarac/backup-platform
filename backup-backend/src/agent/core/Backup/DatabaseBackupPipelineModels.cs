using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Core.Backup;

/// <summary>
/// One full database backup, expressed without any engine or storage implementation: which engine and database, where the
/// staging directory is from both sides, which optional features, and where the finished artifact goes. Resolving the engine
/// provider and the storage provider from <see cref="Engine"/> and <see cref="Storage"/> is the pipeline's business; building this
/// request (from a job, a tenant, a schedule) is the orchestrator's.
/// </summary>
public sealed record DatabaseBackupPipelineRequest
{
    public DatabaseBackupPipelineRequest(
        DatabaseEngineId engine,
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        string databaseName,
        StagingArea staging,
        StorageProviderId storage,
        StorageObjectKey storagePrefix,
        FullBackupOptions? options = null,
        StorageOverwritePolicy overwrite = StorageOverwritePolicy.Fail,
        TimeSpan? backupTimeout = null,
        TimeSpan? verifyTimeout = null,
        StagingFailurePolicy stagingOnFailure = StagingFailurePolicy.Delete)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(storagePrefix);
        if (!Enum.IsDefined(overwrite))
        {
            throw new ArgumentOutOfRangeException(nameof(overwrite), overwrite, "Unknown overwrite policy.");
        }

        if (!Enum.IsDefined(stagingOnFailure))
        {
            throw new ArgumentOutOfRangeException(nameof(stagingOnFailure), stagingOnFailure, "Unknown staging failure policy.");
        }

        Engine = engine;
        Connection = connection;
        Credential = credential;
        DatabaseName = databaseName;
        Staging = staging;
        Storage = storage;
        StoragePrefix = storagePrefix;
        Options = options ?? FullBackupOptions.None;
        Overwrite = overwrite;
        BackupTimeout = backupTimeout;
        VerifyTimeout = verifyTimeout;
        StagingOnFailure = stagingOnFailure;
    }

    public DatabaseEngineId Engine { get; }
    public DatabaseConnectionSettings Connection { get; }
    public DatabaseCredential Credential { get; }
    public string DatabaseName { get; }
    public StagingArea Staging { get; }

    /// <summary>Which registered storage provider receives the artifact.</summary>
    public StorageProviderId Storage { get; }

    /// <summary>A task-scoped provider; used for authenticated backend-relayed storage without host-wide credentials.</summary>
    public IStorageProvider? StorageOverride { get; init; }

    /// <summary>The directory-like key prefix (tenant, job, database, ...) under which the artifact is stored; the artifact file name is appended.</summary>
    public StorageObjectKey StoragePrefix { get; }

    public FullBackupOptions Options { get; }

    /// <summary>Whether an artifact already stored under the same key is replaced or the upload fails. The default is to fail.</summary>
    public StorageOverwritePolicy Overwrite { get; }

    /// <summary>Null selects the provider default.</summary>
    public TimeSpan? BackupTimeout { get; }

    /// <summary>Null selects the provider default.</summary>
    public TimeSpan? VerifyTimeout { get; }

    /// <summary>What happens to staged files when the execution fails (cancellation always removes them).</summary>
    public StagingFailurePolicy StagingOnFailure { get; }
}

public enum ArtifactTransformKind
{
    Compression = 1,
    Encryption
}

/// <summary>One transformation applied between the engine's backup file and the stored artifact (none exist yet; the list is how they will be recorded).</summary>
public sealed record ArtifactTransform(ArtifactTransformKind Kind, string Algorithm);

/// <summary>
/// The backup file exactly as the database engine wrote it, after the engine's own verification. <see cref="Checksum"/> is the
/// SourceChecksum of the plan: it describes what the engine produced.
/// </summary>
public sealed record BackupSourceFile(string FileName, long SizeBytes, ContentChecksum Checksum);

/// <summary>
/// The logical artifact that is stored. <see cref="Checksum"/> is the ArtifactChecksum: it describes what is sent to storage. It
/// equals the SourceChecksum whenever the artifact is the engine's file unchanged and will differ as soon as a compression or
/// encryption transform exists, which is why the two are always separate values. <see cref="IsSourceFile"/> says whether the
/// artifact is physically the same file as the source.
/// </summary>
public sealed record BackupArtifactInfo(
    string FileName,
    long SizeBytes,
    ContentChecksum Checksum,
    bool IsSourceFile,
    IReadOnlyList<ArtifactTransform> Transforms);

/// <summary>One physical copy of the artifact on a storage, with what the verification of that copy established.</summary>
public sealed record BackupReplicaInfo(
    StorageLocation Location,
    long SizeBytes,
    ContentChecksum Checksum,
    StorageVerificationStatus VerificationStatus,
    StorageVerificationMethod VerificationMethod,
    DateTimeOffset VerifiedAt);

/// <summary>When a stage began and ended (null while it was still running when the execution stopped).</summary>
public sealed record BackupStageRecord(BackupExecutionStage Stage, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

public enum DatabaseBackupStatus
{
    Succeeded = 1,
    Failed,
    Cancelled
}

/// <summary>
/// The complete outcome of one pipeline execution: the metadata an orchestrator persists as BackupArtifact and BackupReplica and
/// maps onto a BackupRunAttempt (<see cref="Stages"/> is the attempt timeline, <see cref="FinalStage"/> the terminal state).
/// Fields are filled in as far as the execution got. Nothing in it contains raw engine exception text, credentials, connection
/// strings or directory paths.
/// </summary>
public sealed record DatabaseBackupPipelineResult
{
    public required DatabaseBackupStatus Status { get; init; }

    /// <summary><see cref="BackupExecutionStage.Completed"/>, <see cref="BackupExecutionStage.Failed"/> or <see cref="BackupExecutionStage.Cancelled"/>.</summary>
    public required BackupExecutionStage FinalStage { get; init; }

    /// <summary>Why the execution did not succeed; always set unless <see cref="Status"/> is Succeeded, and then Kind is Cancelled for cancellation.</summary>
    public BackupFailure? Failure { get; init; }

    public DatabaseBackupType BackupType { get; init; } = DatabaseBackupType.Full;

    /// <summary>The database and the engine metadata as seen at backup time.</summary>
    public DatabaseBackupSource? Source { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }

    /// <summary>Start and end of the engine's backup statement.</summary>
    public DateTimeOffset? BackupStartedAt { get; init; }
    public DateTimeOffset? BackupFinishedAt { get; init; }

    /// <summary>Requested next to applied options; the differences are explained by <see cref="Warnings"/>.</summary>
    public BackupOptionsOutcome? Options { get; init; }

    public BackupPreCheckReport? PreCheck { get; init; }

    /// <summary>The engine-side verification (SQL Server: RESTORE VERIFYONLY).</summary>
    public BackupVerification Verification { get; init; } = BackupVerification.NotRun;

    public BackupSourceFile? SourceFile { get; init; }
    public BackupArtifactInfo? Artifact { get; init; }
    public BackupReplicaInfo? Replica { get; init; }

    public IReadOnlyList<BackupWarning> Warnings { get; init; } = [];

    /// <summary>What happened to the staged files at the end of the execution.</summary>
    public StagingCleanup StagingCleanup { get; init; } = StagingCleanup.NotNeeded;

    /// <summary>
    /// Failures of clean-up work (<see cref="BackupFailureKind.CleanupFailed"/>) that did not change the outcome: a stored and verified
    /// artifact stays a success when its staging file could not be removed, and a cancellation stays a cancellation.
    /// </summary>
    public IReadOnlyList<BackupFailure> CleanupFailures { get; init; } = [];

    public IReadOnlyList<BackupStageRecord> Stages { get; init; } = [];
}
