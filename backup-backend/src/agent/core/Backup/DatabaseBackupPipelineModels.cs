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

    /// <summary>The hard-quota artifact bound, checked after hashing and before copying to any destination.</summary>
    public long? MaxArtifactBytes { get; init; }

    /// <summary>
    /// Every place the one artifact is copied to, in order (the primary first). Empty means the single destination named by
    /// <see cref="Storage"/> and <see cref="StorageOverride"/>; <see cref="EffectiveDestinations"/> is the one answer to "where does it go".
    /// </summary>
    public IReadOnlyList<BackupPipelineDestination> Destinations { get; init; } = [];

    public IReadOnlyList<BackupPipelineDestination> EffectiveDestinations =>
        Destinations.Count > 0 ? Destinations : [new BackupPipelineDestination(Storage, StorageOverride, null, true)];

    /// <summary>
    /// How many destinations must end Verified for the run to be worth taking. It only lets the pipeline refuse BEFORE the engine backup when too few
    /// destinations can even be reached; what a run with fewer verified copies than required MEANS is the backend's replication compliance, not the engine's success.
    /// </summary>
    public int RequiredVerifiedReplicas { get; init; } = 1;

    /// <summary>Upper bound for the upload and verification of ONE destination; null selects <see cref="DefaultDestinationTimeout"/>. A destination that exceeds it fails alone.</summary>
    public TimeSpan? DestinationTimeout { get; init; }

    public static readonly TimeSpan DefaultDestinationTimeout = TimeSpan.FromHours(6);

    /// <summary>Paces the upload bytes of every destination to the backend's limits; null uploads at full speed. The timeout of a paced destination is widened by <see cref="IUploadBandwidthSession.DeadlineAllowance"/>.</summary>
    public IUploadBandwidth? UploadBandwidth { get; init; }

    /// <summary>Null means the job does not encrypt; otherwise handed to the artifact preparer unchanged.</summary>
    public ArtifactEncryption? Encryption { get; init; }
}

/// <summary>One copy target of an execution. <see cref="StorageOverride"/> is a task-scoped (lease-bound, relayed) provider; <see cref="ConnectionId"/> is the snapshot destination it belongs to.</summary>
public sealed record BackupPipelineDestination(StorageProviderId Storage, IStorageProvider? StorageOverride = null, Guid? ConnectionId = null, bool IsPrimary = false);

public enum ArtifactTransformKind
{
    Compression = 1,
    Encryption
}

/// <summary>
/// What the header of an encrypted artifact says, read back after writing it: the tenant-wrap recipient (<see cref="WrappedDek"/> is ciphertext only the backend's private wrap key
/// opens) and the index of every recipient, which the backend checks against the keys it issued.
/// </summary>
public sealed record ArtifactSeal(string KeyId, int KeyVersion, byte[] WrappedDek, IReadOnlyList<ArtifactRecipientRef> Recipients);

/// <summary>One recipient of an encrypted artifact as its header names it: wire type name, key id, key version. No wrapped key.</summary>
public sealed record ArtifactRecipientRef(string Type, string KeyId, int KeyVersion);

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
    IReadOnlyList<ArtifactTransform> Transforms,
    ArtifactSeal? Seal = null);

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

public enum BackupDestinationState
{
    Verified = 1,
    Failed,
    /// <summary>Not completed because the execution was cancelled (the destination in flight, or one that was never started).</summary>
    Cancelled
}

/// <summary>
/// The independent outcome of one destination. A destination is Verified only with a <see cref="Replica"/> whose storage verification passed;
/// <see cref="ObjectMayRemain"/> is set when an object was written under <see cref="Key"/> and could not be proven removed afterwards.
/// </summary>
public sealed record BackupDestinationResult(
    BackupPipelineDestination Destination,
    BackupDestinationState State,
    BackupReplicaInfo? Replica,
    BackupFailure? Failure,
    bool Uploaded,
    StorageObjectKey? Key,
    bool ObjectMayRemain,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

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

    /// <summary>One entry per destination, in request order. Empty only when the execution stopped before any transfer began.</summary>
    public IReadOnlyList<BackupDestinationResult> Destinations { get; init; } = [];

    /// <summary>The first verified copy (for a single destination: THE copy). Null when no destination verified.</summary>
    public BackupReplicaInfo? Replica => Destinations.Select(destination => destination.Replica).FirstOrDefault(replica => replica is not null);

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
