namespace BackupPlatform.BackupProviders.Abstractions.Database;

/// <summary>The kind of backup. Only full backups exist in this iteration; the member exists so results can already say what they are.</summary>
public enum DatabaseBackupType
{
    Full = 1
}

/// <summary>
/// The stages of one backup execution, in order. A database provider reports <see cref="Preparing"/>, <see cref="BackingUp"/> and
/// <see cref="Verifying"/>; the pipeline around it reports the remaining stages with the same vocabulary, so one progress stream and
/// one result shape describe the whole execution and an orchestrator can map them onto its own attempt records.
/// <see cref="Completed"/>, <see cref="Failed"/> and <see cref="Cancelled"/> are terminal.
/// </summary>
public enum BackupExecutionStage
{
    Preparing = 0,
    BackingUp,
    Verifying,
    HashingSource,
    PreparingArtifact,
    HashingArtifact,
    Uploading,
    VerifyingReplica,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// A best-effort progress report. Reporting never influences execution: a consumer that throws or is slow cannot change the outcome.
/// <see cref="Percent"/> is 0..100 and null when the stage has no meaningful percentage; <see cref="Bytes"/> is the bytes processed
/// so far and <see cref="TotalBytes"/> the expected total, each null when unknown.
/// </summary>
public sealed record BackupProgress
{
    public BackupProgress(BackupExecutionStage stage, double? percent, long? bytes, long? totalBytes, DateTimeOffset timestamp)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown backup stage.");
        }

        if (percent is < 0 or > 100 || percent is double.NaN)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Percent is 0..100.");
        }

        if (bytes is < 0 || totalBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "Byte counts are not negative.");
        }

        Stage = stage;
        Percent = percent;
        Bytes = bytes;
        TotalBytes = totalBytes;
        Timestamp = timestamp;
    }

    public BackupExecutionStage Stage { get; }
    public double? Percent { get; }
    public long? Bytes { get; }
    public long? TotalBytes { get; }
    public DateTimeOffset Timestamp { get; }
}

/// <summary>Stage-based failure classification. Cancellation is a classification of its own, never a failure of a stage.</summary>
public enum BackupFailureKind
{
    PreCheckFailed = 1,
    SqlBackupFailed,
    VerificationFailed,
    SourceHashFailed,
    ArtifactPreparationFailed,
    StorageWriteFailed,
    ReplicaVerificationFailed,
    CleanupFailed,
    Cancelled
}

/// <summary>
/// An engine-independent, coarse reason a pre-check or an execution failed, for consumers that must tell a transient condition from
/// a permanent one without knowing engine reason codes. <see cref="Unclassified"/> means the provider could not say.
/// </summary>
public enum BackupFailureCause
{
    /// <summary>The provider did not classify the failure.</summary>
    Unclassified = 0,

    /// <summary>The database server could not be reached or the connection broke.</summary>
    DatabaseUnavailable,

    /// <summary>The database server did not answer, or the statement did not finish, within its time limit.</summary>
    Timeout,

    /// <summary>The database to back up does not exist on the server.</summary>
    DatabaseNotFound,

    /// <summary>The connection or backup settings are invalid.</summary>
    InvalidConfiguration,

    /// <summary>The engine or the requested capability cannot do what was asked.</summary>
    Unsupported,

    /// <summary>The server rejected the credentials.</summary>
    AuthenticationFailed,

    /// <summary>The credentials are valid but lack a required permission.</summary>
    PermissionDenied,

    /// <summary>The staging directory is unusable (wrong, missing, not writable, not shared by both sides, or full).</summary>
    StagingInvalid,

    /// <summary>A storage destination could not be reached or written.</summary>
    StorageUnavailable
}

/// <summary>
/// Why an execution did not succeed. <see cref="ReasonCode"/> is stable and machine-readable; <see cref="Message"/> is a fixed
/// user-safe sentence: it never embeds engine exception text, credentials, connection strings or directory paths.
/// </summary>
public sealed record BackupFailure
{
    public BackupFailure(BackupFailureKind kind, BackupExecutionStage stage, string reasonCode, string message, BackupFailureCause cause = BackupFailureCause.Unclassified)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown failure kind.");
        }

        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown backup stage.");
        }

        if (!Enum.IsDefined(cause))
        {
            throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unknown failure cause.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Kind = kind;
        Stage = stage;
        ReasonCode = reasonCode;
        Message = message;
        Cause = cause;
    }

    public BackupFailureKind Kind { get; }

    /// <summary>The stage that was running when the execution stopped.</summary>
    public BackupExecutionStage Stage { get; }

    public string ReasonCode { get; }
    public string Message { get; }

    /// <summary>Engine-independent classification of the failure; <see cref="BackupFailureCause.Unclassified"/> when unknown.</summary>
    public BackupFailureCause Cause { get; }
}

/// <summary>A non-fatal observation (an option fell back, a check could not be verified, a probe left something behind).</summary>
public sealed record BackupWarning
{
    public BackupWarning(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
    }

    public string Code { get; }
    public string Message { get; }
}

/// <summary>
/// How a caller wants an optional backup feature treated. <see cref="Required"/> matches the existing pre-check policy: a feature
/// the engine reports as unsupported fails the pre-check. <see cref="IfSupported"/> is the fallback policy: the feature is used only
/// when the engine reports it supported, otherwise the backup proceeds without it and says so with a <see cref="BackupWarning"/>.
/// A feature whose support is unknown is never used silently: <see cref="Required"/> applies it with a warning (the engine decides),
/// <see cref="IfSupported"/> leaves it out with a warning.
/// </summary>
public enum BackupOptionMode
{
    Off = 0,
    IfSupported,
    Required
}

/// <summary>The optional features of a full backup as the caller requested them.</summary>
public sealed record FullBackupOptions
{
    public FullBackupOptions(
        BackupOptionMode copyOnly = BackupOptionMode.Off,
        BackupOptionMode checksum = BackupOptionMode.Off,
        BackupOptionMode compression = BackupOptionMode.Off)
    {
        CopyOnly = Validate(copyOnly, nameof(copyOnly));
        Checksum = Validate(checksum, nameof(checksum));
        Compression = Validate(compression, nameof(compression));
    }

    public static FullBackupOptions None { get; } = new();

    /// <summary>A copy-only backup does not disturb the differential base or the log chain.</summary>
    public BackupOptionMode CopyOnly { get; }

    /// <summary>Backup page checksums and verification against them.</summary>
    public BackupOptionMode Checksum { get; }

    /// <summary>The engine's native backup compression.</summary>
    public BackupOptionMode Compression { get; }

    private static BackupOptionMode Validate(BackupOptionMode mode, string name) =>
        Enum.IsDefined(mode) ? mode : throw new ArgumentOutOfRangeException(name, mode, "Unknown option mode.");
}

/// <summary>The features that were actually in effect for the backup. Never inferred from what was requested.</summary>
public sealed record AppliedFullBackupOptions(bool CopyOnly, bool Checksum, bool Compression);

/// <summary>What was asked for next to what was done; the difference is explained by <see cref="BackupWarning"/>s.</summary>
public sealed record BackupOptionsOutcome(FullBackupOptions Requested, AppliedFullBackupOptions Applied);

public enum StagingFailurePolicy
{
    /// <summary>A staged file left by a failed execution is deleted. The default: temporary files must not accumulate.</summary>
    Delete = 0,

    /// <summary>A failed execution leaves its staged file for diagnosis. A cancelled execution always removes it.</summary>
    Keep
}

/// <summary>Everything the provider needs to take one verified full backup into the staging directory.</summary>
public sealed record FullBackupRequest
{
    public static readonly TimeSpan DefaultBackupTimeout = TimeSpan.FromHours(12);
    public static readonly TimeSpan DefaultVerifyTimeout = TimeSpan.FromHours(2);

    private static readonly TimeSpan MinimumTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromDays(7);

    public FullBackupRequest(
        string databaseName,
        StagingArea staging,
        string stagedFileName,
        FullBackupOptions? options = null,
        TimeSpan? backupTimeout = null,
        TimeSpan? verifyTimeout = null,
        StagingFailurePolicy onFailure = StagingFailurePolicy.Delete)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedFileName);
        if (stagedFileName.Any(char.IsControl) || stagedFileName.IndexOfAny(['/', '\\', ':']) >= 0 || stagedFileName is "." or "..")
        {
            throw new ArgumentException("The staged file name is a simple name without directory components.", nameof(stagedFileName));
        }

        if (!Enum.IsDefined(onFailure))
        {
            throw new ArgumentOutOfRangeException(nameof(onFailure), onFailure, "Unknown staging failure policy.");
        }

        DatabaseName = databaseName;
        Staging = staging;
        StagedFileName = stagedFileName;
        Options = options ?? FullBackupOptions.None;
        BackupTimeout = ValidateTimeout(backupTimeout ?? DefaultBackupTimeout, nameof(backupTimeout));
        VerifyTimeout = ValidateTimeout(verifyTimeout ?? DefaultVerifyTimeout, nameof(verifyTimeout));
        OnFailure = onFailure;
    }

    public string DatabaseName { get; }
    public StagingArea Staging { get; }

    /// <summary>The simple name of the backup file inside the staging directory; it must not exist yet.</summary>
    public string StagedFileName { get; }

    public FullBackupOptions Options { get; }

    /// <summary>Upper bound of the backup statement. Finite by construction.</summary>
    public TimeSpan BackupTimeout { get; }

    /// <summary>Upper bound of the verification statement. Finite by construction.</summary>
    public TimeSpan VerifyTimeout { get; }

    public StagingFailurePolicy OnFailure { get; }

    private static TimeSpan ValidateTimeout(TimeSpan value, string name) =>
        value < MinimumTimeout || value > MaximumTimeout
            ? throw new ArgumentOutOfRangeException(name, value, $"Timeouts must be between {MinimumTimeout} and {MaximumTimeout}.")
            : TimeSpan.FromSeconds(Math.Ceiling(value.TotalSeconds));
}

/// <summary>
/// Engine metadata about the backed-up database at the time of the backup. <see cref="Details"/> holds engine-specific facts as
/// stable keys (for example <c>recovery-model</c>, <c>compatibility-level</c>) so this type stays engine-neutral.
/// </summary>
public sealed record DatabaseBackupSource(
    DatabaseInstanceInfo Instance,
    string DatabaseName,
    IReadOnlyDictionary<string, string> Details);

/// <summary>The backup file in the staging directory, from both sides' point of view.</summary>
public sealed record StagedBackupFile(DatabaseServerPath ServerPath, AgentPath AgentPath, string FileName, long SizeBytes);

public enum BackupVerificationStatus
{
    NotRun = 0,
    Passed,
    Failed
}

/// <summary>The engine-side check of the backup file (SQL Server: <c>RESTORE VERIFYONLY</c>).</summary>
public sealed record BackupVerification(
    BackupVerificationStatus Status,
    string Method,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt)
{
    public static BackupVerification NotRun { get; } = new(BackupVerificationStatus.NotRun, "none", null, null);
}

public enum StagingCleanupStatus
{
    /// <summary>There was nothing to clean up (no file was created, or the file is handed over to the next stage).</summary>
    NotNeeded = 0,
    Deleted,

    /// <summary>The file was left in place on purpose (<see cref="StagingFailurePolicy.Keep"/>, or it is the handed-over artifact).</summary>
    Kept,

    /// <summary>The file should have been removed and could not be; it may still exist.</summary>
    Failed
}

public sealed record StagingCleanup(StagingCleanupStatus Status, string? ReasonCode = null)
{
    public static StagingCleanup NotNeeded { get; } = new(StagingCleanupStatus.NotNeeded);
}

public enum FullBackupStatus
{
    Succeeded = 1,
    Failed,
    Cancelled
}

/// <summary>
/// The outcome of <see cref="IDatabaseBackupProvider.ExecuteFullBackupAsync"/>. On success <see cref="StagedFile"/> is the verified
/// backup file, handed over to the caller (who now owns its cleanup). On failure or cancellation <see cref="Failure"/> says why and
/// <see cref="Cleanup"/> says what happened to any partial file. <see cref="Source"/>, <see cref="Options"/> and
/// <see cref="Warnings"/> are filled in as far as the execution got.
/// </summary>
public sealed record FullBackupResult
{
    public FullBackupResult(
        FullBackupStatus status,
        BackupFailure? failure,
        DatabaseBackupSource? source,
        BackupOptionsOutcome? options,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        DateTimeOffset? backupStartedAt,
        DateTimeOffset? backupFinishedAt,
        StagedBackupFile? stagedFile,
        BackupVerification verification,
        IEnumerable<BackupWarning> warnings,
        StagingCleanup cleanup)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown backup status.");
        }

        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(cleanup);

        if (status == FullBackupStatus.Succeeded)
        {
            if (failure is not null || stagedFile is null || source is null || options is null
                || verification.Status != BackupVerificationStatus.Passed)
            {
                throw new ArgumentException("A successful backup has a verified staged file, source and options and no failure.", nameof(status));
            }
        }
        else if (failure is null || stagedFile is not null
            || (status == FullBackupStatus.Cancelled) != (failure.Kind == BackupFailureKind.Cancelled))
        {
            throw new ArgumentException("A failed or cancelled backup has a matching failure and no handed-over staged file.", nameof(status));
        }

        Status = status;
        Failure = failure;
        Source = source;
        Options = options;
        StartedAt = startedAt;
        FinishedAt = finishedAt;
        BackupStartedAt = backupStartedAt;
        BackupFinishedAt = backupFinishedAt;
        StagedFile = stagedFile;
        Verification = verification;
        Warnings = warnings.ToArray();
        Cleanup = cleanup;
    }

    public FullBackupStatus Status { get; }
    public BackupFailure? Failure { get; }
    public DatabaseBackupSource? Source { get; }
    public BackupOptionsOutcome? Options { get; }

    /// <summary>Start and end of the whole provider execution (metadata read, backup, verification).</summary>
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset FinishedAt { get; }

    /// <summary>Start and end of the backup statement itself; null when it never started or never finished.</summary>
    public DateTimeOffset? BackupStartedAt { get; }
    public DateTimeOffset? BackupFinishedAt { get; }

    /// <summary>Only set on success: the verified backup file now owned by the caller.</summary>
    public StagedBackupFile? StagedFile { get; }

    public BackupVerification Verification { get; }
    public IReadOnlyList<BackupWarning> Warnings { get; }

    /// <summary>What happened to the staging file inside the provider (partial output of a failed backup, a failed verification, a cancellation).</summary>
    public StagingCleanup Cleanup { get; }
}
