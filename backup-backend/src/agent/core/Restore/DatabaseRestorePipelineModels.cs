using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Core.Restore;

public enum RestorePipelinePurpose
{
    Execution,
    Precheck,
    /// <summary>Restore into a disposable platform-named database, prove it is usable, then drop it. Never changes any other database.</summary>
    Verification
}

/// <summary>What became of a verification's disposable database.</summary>
public enum VerificationCleanup
{
    /// <summary>The restore never reached the point of creating it, or it was already absent when removal ran.</summary>
    NotCreated,
    Dropped,
    /// <summary>It may still exist: the backend keeps the removal owed and retries.</summary>
    Failed
}

/// <summary>The engine's verification report (null when verification never ran) and the disposable database's fate. <see cref="CleanupReasonCode"/> is set only when cleanup failed.</summary>
public sealed record RestoreVerificationOutcome(RestoreVerificationReport? Report, VerificationCleanup Cleanup, string? CleanupReasonCode = null);

/// <summary>
/// Marks a replica as an encrypted (<c>bpe</c> v2) artifact. <paramref name="UnwrapDek"/> is bound to the restore attempt by whoever builds the request,
/// so the pipeline itself never sees a lease or an identifier it could misuse; <paramref name="SourceChecksum"/> is the plaintext SHA-256 the decrypted bytes must match.
/// </summary>
public sealed record ArtifactDecryption(ContentChecksum SourceChecksum, Func<CancellationToken, Task<byte[]>> UnwrapDek);

/// <summary>Read-only discovery needs only a connection and the agent-local credential, never a replica or staging area.</summary>
public sealed record RestoreDiscoveryRequest(DatabaseEngineId Engine, DatabaseConnectionSettings Connection, DatabaseCredential Credential);

/// <summary>
/// One restore of a stored backup replica into a database, expressed without any engine or storage implementation: which engine,
/// server and target database, which replica (storage provider, key, the size and SHA-256 it was published with) and where to stage
/// it. Resolving the engine provider and the storage provider from <see cref="Engine"/> and <see cref="Storage"/> is the pipeline's
/// business; building this request from a claimed task is the worker's. <see cref="TenantId"/> exists for one reason: the replica key
/// must belong to the same tenant as the restore, which the pipeline proves from the key itself.
/// </summary>
public sealed record DatabaseRestorePipelineRequest
{
    public DatabaseRestorePipelineRequest(
        DatabaseEngineId engine,
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        string databaseName,
        DatabaseRestoreMode mode,
        StagingArea staging,
        StorageProviderId storage,
        StorageObjectKey storageKey,
        Guid tenantId,
        long expectedSizeBytes,
        ContentChecksum expectedChecksum,
        bool terminateActiveConnections = false,
        bool verifyChecksum = true,
        bool verifyBackup = true,
        TimeSpan? verifyTimeout = null,
        TimeSpan? restoreTimeout = null,
        RestorePipelinePurpose purpose = RestorePipelinePurpose.Execution)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(storageKey);
        ArgumentNullException.ThrowIfNull(expectedChecksum);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown restore mode.");
        }
        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown restore purpose.");
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A restore belongs to a tenant.", nameof(tenantId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedSizeBytes);

        Engine = engine;
        Purpose = purpose;
        Connection = connection;
        Credential = credential;
        DatabaseName = databaseName;
        Mode = mode;
        Staging = staging;
        Storage = storage;
        StorageKey = storageKey;
        TenantId = tenantId;
        ExpectedSizeBytes = expectedSizeBytes;
        ExpectedChecksum = expectedChecksum;
        TerminateActiveConnections = terminateActiveConnections;
        VerifyChecksum = verifyChecksum;
        VerifyBackup = verifyBackup;
        VerifyTimeout = verifyTimeout;
        RestoreTimeout = restoreTimeout;
    }

    public DatabaseEngineId Engine { get; }
    public RestorePipelinePurpose Purpose { get; }
    public DatabaseConnectionSettings Connection { get; }
    public DatabaseCredential Credential { get; }

    /// <summary>The database to create or replace on the target server.</summary>
    public string DatabaseName { get; }
    public DatabaseRestoreMode Mode { get; }
    public StagingArea Staging { get; }

    /// <summary>Which registered storage provider holds the replica. It must be able to read (<see cref="StorageCapabilityIds.Read"/>).</summary>
    public StorageProviderId Storage { get; }

    /// <summary>A task-scoped authenticated backend storage proxy.</summary>
    public IStorageProvider? StorageOverride { get; init; }

    /// <summary>Null for a plain (v1) replica; otherwise the staged replica is ciphertext that is decrypted and verified before the engine sees it.</summary>
    public ArtifactDecryption? Decryption { get; init; }
    public FileRestoreSelection? Files { get; init; }

    /// <summary>The replica's key. Must be a key of the platform's own layout whose tenant is <see cref="TenantId"/>.</summary>
    public StorageObjectKey StorageKey { get; }
    public Guid TenantId { get; }

    /// <summary>The size the replica was published with; the staged copy must have exactly this many bytes.</summary>
    public long ExpectedSizeBytes { get; }

    /// <summary>The SHA-256 the replica was published with; the bytes read now must still hash to it unless <see cref="VerifyChecksum"/> is off.</summary>
    public ContentChecksum ExpectedChecksum { get; }

    /// <summary>Whether sessions of an existing target may be disconnected (replace only).</summary>
    public bool TerminateActiveConnections { get; }

    /// <summary>Hash the replica while copying it and compare with <see cref="ExpectedChecksum"/>. Off is the user's explicit choice and is recorded as a warning.</summary>
    public bool VerifyChecksum { get; }

    /// <summary>Run the engine's own verification (<c>RESTORE VERIFYONLY</c>) on the staged copy.</summary>
    public bool VerifyBackup { get; }

    /// <summary>Null selects the provider default.</summary>
    public TimeSpan? VerifyTimeout { get; }

    /// <summary>Null selects the provider default.</summary>
    public TimeSpan? RestoreTimeout { get; }
}

/// <summary>When a stage began and ended (null while it was still running when the execution stopped).</summary>
public sealed record RestoreStageRecord(RestoreExecutionStage Stage, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

/// <summary>
/// Why a restore did not succeed, in the platform's own vocabulary. <see cref="Code"/> is one of <see cref="RestoreAgentFailureCodes"/> (the names
/// the backend accepts from an agent); <see cref="ReasonCode"/> is the more specific stable reason that led to it (a provider's or the pipeline's),
/// and <see cref="Message"/> is a fixed user-safe sentence: never engine exception text, a credential, a connection string or a path.
/// </summary>
public sealed record RestorePipelineFailure(string Code, RestoreExecutionStage Stage, string ReasonCode, string Message);

/// <summary>
/// The complete outcome of one restore execution. Fields are filled in as far as the execution got. <see cref="TargetState"/> always says what was
/// left on the target: it is never derived from <see cref="Status"/>, so a cancelled or failed restore never implies a healthy database.
/// Nothing in here contains raw engine exception text, credentials or connection strings.
/// </summary>
public sealed record DatabaseRestorePipelineResult
{
    public required DatabaseRestoreStatus Status { get; init; }

    /// <summary><see cref="RestoreExecutionStage.Completed"/>, <see cref="RestoreExecutionStage.Failed"/> or <see cref="RestoreExecutionStage.Cancelled"/>.</summary>
    public required RestoreExecutionStage FinalStage { get; init; }

    /// <summary>Always set unless <see cref="Status"/> is Succeeded; its Code is <see cref="RestoreAgentFailureCodes.Cancelled"/> for a cancellation.</summary>
    public RestorePipelineFailure? Failure { get; init; }

    public required DatabaseRestoreTargetState TargetState { get; init; }

    /// <summary>The state the engine reports for the target database after the attempt (for example <c>ONLINE</c>, <c>RESTORING</c>); null when never established.</summary>
    public string? DatabaseState { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }

    /// <summary>Start and end of the engine's restore statement; null when it never started or never finished.</summary>
    public DateTimeOffset? RestoreStartedAt { get; init; }
    public DateTimeOffset? RestoreFinishedAt { get; init; }

    /// <summary>Every pre-check item of the run in the order it was produced: the agent's own checks, then the provider's target checks and plan checks.</summary>
    public IReadOnlyList<BackupPreCheckItem> PreCheck { get; init; } = [];

    public RestoreBackupInspection? Inspection { get; init; }
    public RestorePlan? Plan { get; init; }
    public DatabaseInstanceDiscovery? Discovery { get; init; }

    /// <summary>Where each logical file was restored (from the engine's result; the plan's mapping when the engine never reported one).</summary>
    public IReadOnlyList<RestoreFileMapping> FileMappings { get; init; } = [];

    public IReadOnlyList<BackupWarning> Warnings { get; init; } = [];

    /// <summary>True when the bytes read from the replica were hashed during this restore and equal the recorded checksum.</summary>
    public bool ChecksumVerified { get; init; }

    /// <summary>True when the engine's own verification ran on the staged copy and passed.</summary>
    public bool BackupVerified { get; init; }

    /// <summary>What happened to the staged copy at the end of the execution. A staged copy that could not be removed is a warning, never a failure.</summary>
    public StagingCleanup StagingCleanup { get; init; } = StagingCleanup.NotNeeded;

    public IReadOnlyList<RestoreStageRecord> Stages { get; init; } = [];

    /// <summary>Set for a verification only.</summary>
    public RestoreVerificationOutcome? Verification { get; init; }

    /// <summary>The <c>bp_old_*</c> rollback copy a replace restore kept (PostgreSQL); null otherwise.</summary>
    public string? RetainedDatabaseName { get; init; }
}
