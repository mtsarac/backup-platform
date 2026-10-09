namespace BackupPlatform.BackupProviders.Abstractions.Database;

/// <summary>What a restore does to the target database. Mirrors the platform's restore mode without depending on the domain.</summary>
public enum DatabaseRestoreMode
{
    /// <summary>Create a database that does not exist yet; an existing one is a failed pre-check.</summary>
    NewDatabase = 1,
    /// <summary>Overwrite an existing database. The only mode whose statement may carry the engine's overwrite option.</summary>
    ReplaceExistingDatabase = 2
}

/// <summary>
/// The stages of one restore execution, in order. A database provider reports <see cref="PreparingTarget"/>, <see cref="Restoring"/> and
/// <see cref="Finalizing"/>; the pipeline around it reports the remaining stages with the same vocabulary, so one progress stream describes the
/// whole execution. <see cref="Completed"/>, <see cref="Failed"/> and <see cref="Cancelled"/> are terminal.
/// </summary>
public enum RestoreExecutionStage
{
    Preparing = 0,
    DownloadingArtifact,
    VerifyingChecksum,
    InspectingBackup,
    PreparingTarget,
    Restoring,
    Finalizing,
    Completed,
    Failed,
    Cancelled
}

/// <summary>A best-effort restore progress report. Reporting never influences execution.</summary>
public sealed record RestoreProgress
{
    public RestoreProgress(RestoreExecutionStage stage, double? percent, long? bytes, long? totalBytes, DateTimeOffset timestamp)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown restore stage.");
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

    public RestoreExecutionStage Stage { get; }
    public double? Percent { get; }
    public long? Bytes { get; }
    public long? TotalBytes { get; }
    public DateTimeOffset Timestamp { get; }
}

/// <summary>The kind of backup set (<c>RESTORE HEADERONLY</c> BackupType). Only <see cref="Database"/> (a full backup) can be restored by this version.</summary>
public enum RestoreBackupKind
{
    Database = 1,
    Differential,
    TransactionLog,
    /// <summary>File, partial or any other backup type this version does not interpret.</summary>
    Other
}

/// <summary>
/// The metadata of a backup set as the engine reports it (<c>RESTORE HEADERONLY</c>), mapped to a typed model. Nothing here is a raw row or a
/// provider type. <see cref="SourceServerVersion"/> is the engine version that wrote the backup (major.minor.build).
/// Header dates are source-server wall-clock values with unspecified timezone, not instants in the restore target's timezone.
/// </summary>
public sealed record RestoreBackupHeader(
    string DatabaseName,
    RestoreBackupKind Kind,
    Version? SourceServerVersion,
    string? SourceServerName,
    int? CompatibilityLevel,
    string? Collation,
    DateTime? BackupStartedAt,
    DateTime? BackupFinishedAt,
    int Position,
    long BackupSizeBytes,
    bool IsCopyOnly,
    bool HasChecksums,
    bool IsCompressed,
    bool IsEncrypted,
    Guid? FamilyGuid);

public enum RestoreFileKind
{
    Data = 1,
    Log,
    /// <summary>Legacy full-text catalog.</summary>
    FullText,
    /// <summary>FILESTREAM container or memory-optimized data: a directory, not a file. Not restorable by this version.</summary>
    FileStream,
    Other
}

/// <summary>One logical file of the backed-up database (<c>RESTORE FILELISTONLY</c>). <see cref="SourcePhysicalName"/> is where it lived on the SOURCE server: informational, never a restore destination.</summary>
public sealed record RestoreBackupFile(string LogicalName, string SourcePhysicalName, RestoreFileKind Kind, string? FileGroupName, long SizeBytes);

/// <summary>What was read from a backup file: its header, its logical files and whether the engine's own verification ran and passed.</summary>
public sealed record RestoreBackupInspection(
    RestoreBackupHeader Header,
    IReadOnlyList<RestoreBackupFile> Files,
    bool VerifyOnlyPassed,
    DateTimeOffset? VerifyStartedAt,
    DateTimeOffset? VerifyFinishedAt);

/// <summary>A logical file and its destination on the TARGET server, generated from the target's own directories.</summary>
public sealed record RestoreFileMapping(string LogicalName, RestoreFileKind Kind, string SourcePhysicalName, DatabaseServerPath TargetPath, long SizeBytes, string? FileGroupName = null);

/// <summary>The target server facts a plan was made against.</summary>
public sealed record RestoreTargetInfo(
    DatabaseInstanceInfo Instance,
    DatabaseServerPath DefaultDataDirectory,
    DatabaseServerPath DefaultLogDirectory,
    bool DatabaseExists,
    string? DatabaseState);

/// <summary>Where every logical file of a backup will go on the target. Destinations are unique and never taken from the source server's paths.</summary>
public sealed record RestorePlan
{
    public RestorePlan(IReadOnlyList<RestoreFileMapping> mappings, RestoreTargetInfo target, IEnumerable<BackupWarning>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(target);
        if (mappings.Count == 0)
        {
            throw new ArgumentException("A restore plan maps at least one file.", nameof(mappings));
        }

        if (mappings.Select(mapping => mapping.TargetPath.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mappings.Count
            || mappings.Select(mapping => mapping.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mappings.Count)
        {
            throw new ArgumentException("Restore destinations and logical names must be unique.", nameof(mappings));
        }

        Mappings = mappings;
        Target = target;
        Warnings = warnings?.ToArray() ?? [];
    }

    public IReadOnlyList<RestoreFileMapping> Mappings { get; }
    public RestoreTargetInfo Target { get; }
    public IReadOnlyList<BackupWarning> Warnings { get; }
}

public enum RestoreFailureKind
{
    PreCheckFailed = 1,
    /// <summary>The backup file cannot be read or is not a valid backup (HEADERONLY / FILELISTONLY / VERIFYONLY failed).</summary>
    BackupInvalid,
    SqlRestoreFailed,
    Cancelled
}

/// <summary>Why an operation did not succeed. <see cref="ReasonCode"/> is stable and machine-readable; <see cref="Message"/> is a fixed user-safe sentence.</summary>
public sealed record RestoreFailure
{
    public RestoreFailure(RestoreFailureKind kind, RestoreExecutionStage stage, string reasonCode, string message, BackupFailureCause cause = BackupFailureCause.Unclassified)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown failure kind.");
        }

        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown restore stage.");
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

    public RestoreFailureKind Kind { get; }
    public RestoreExecutionStage Stage { get; }
    public string ReasonCode { get; }
    public string Message { get; }
    public BackupFailureCause Cause { get; }
}

/// <summary>The condition the target database was left in. Never inferred from success or cancellation.</summary>
public enum DatabaseRestoreTargetState
{
    /// <summary>The target was not changed.</summary>
    NotModified = 1,
    /// <summary>The restored database is ONLINE.</summary>
    Online,
    /// <summary>The target was changed and the restore did not complete (RESTORING, damaged or missing; a replaced database's old contents are gone).</summary>
    PartiallyRestored,
    /// <summary>The provider could not establish the condition of the target.</summary>
    Unknown
}

public enum DatabaseRestoreStatus
{
    Succeeded = 1,
    Failed,
    Cancelled
}

// ---- requests -------------------------------------------------------------------------------------------------------------------

/// <summary>What a restore intends to do, expressed so a provider can check the target without changing it.</summary>
public sealed record RestorePreCheckRequest
{
    public RestorePreCheckRequest(string databaseName, DatabaseRestoreMode mode, bool terminateActiveConnections, long? backupSizeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown restore mode.");
        }

        if (backupSizeBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(backupSizeBytes), backupSizeBytes, "The backup size is not negative.");
        }

        DatabaseName = databaseName;
        Mode = mode;
        TerminateActiveConnections = terminateActiveConnections;
        BackupSizeBytes = backupSizeBytes;
    }

    public string DatabaseName { get; }
    public DatabaseRestoreMode Mode { get; }

    /// <summary>Whether the user allowed disconnecting sessions of an existing target (SINGLE_USER ... ROLLBACK IMMEDIATE). Without it, active sessions fail the pre-check.</summary>
    public bool TerminateActiveConnections { get; }

    /// <summary>The backup file's size, for the space check; null skips it.</summary>
    public long? BackupSizeBytes { get; }
}

public sealed record RestoreInspectionRequest
{
    public static readonly TimeSpan DefaultVerifyTimeout = TimeSpan.FromHours(2);

    public RestoreInspectionRequest(DatabaseServerPath serverFile, bool runVerifyOnly, TimeSpan? verifyTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(serverFile);
        var timeout = verifyTimeout ?? DefaultVerifyTimeout;
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(verifyTimeout), timeout, "Timeouts must be between 1 second and 7 days.");
        }

        ServerFile = serverFile;
        RunVerifyOnly = runVerifyOnly;
        VerifyTimeout = TimeSpan.FromSeconds(Math.Ceiling(timeout.TotalSeconds));
    }

    /// <summary>The backup file as the database server sees it.</summary>
    public DatabaseServerPath ServerFile { get; }

    /// <summary>Run the engine's own verification (<c>RESTORE VERIFYONLY</c>) on the file now, even if the backup was verified when it was taken.</summary>
    public bool RunVerifyOnly { get; }

    public TimeSpan VerifyTimeout { get; }
}

/// <summary>Success carries the inspection, failure carries why the file is unusable. Exactly one of them is set.</summary>
public sealed record RestoreInspectionResult
{
    public RestoreInspectionResult(RestoreBackupInspection? inspection, RestoreFailure? failure, IEnumerable<BackupWarning>? warnings = null)
    {
        if ((inspection is null) == (failure is null))
        {
            throw new ArgumentException("An inspection either succeeds with an inspection or fails with a failure.");
        }

        Inspection = inspection;
        Failure = failure;
        Warnings = warnings?.ToArray() ?? [];
    }

    public RestoreBackupInspection? Inspection { get; }
    public RestoreFailure? Failure { get; }
    public IReadOnlyList<BackupWarning> Warnings { get; }
}

public sealed record RestorePlanRequest(string DatabaseName, DatabaseRestoreMode Mode, bool TerminateActiveConnections, RestoreBackupInspection Inspection);

/// <summary>The target-compatibility checks and, when none failed, the file plan.</summary>
public sealed record RestorePlanResult
{
    public RestorePlanResult(BackupPreCheckReport report, RestorePlan? plan)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.CanProceed != (plan is not null))
        {
            throw new ArgumentException("A plan exists exactly when the checks did not fail.", nameof(plan));
        }

        Report = report;
        Plan = plan;
    }

    public BackupPreCheckReport Report { get; }
    public RestorePlan? Plan { get; }
}

public sealed record DatabaseRestoreRequest
{
    public static readonly TimeSpan DefaultRestoreTimeout = TimeSpan.FromHours(12);

    public DatabaseRestoreRequest(
        string databaseName,
        DatabaseServerPath serverFile,
        DatabaseRestoreMode mode,
        bool terminateActiveConnections,
        RestoreBackupInspection inspection,
        RestorePlan plan,
        TimeSpan? restoreTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(serverFile);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(plan);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown restore mode.");
        }

        var timeout = restoreTimeout ?? DefaultRestoreTimeout;
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(restoreTimeout), timeout, "Timeouts must be between 1 second and 7 days.");
        }

        DatabaseName = databaseName;
        ServerFile = serverFile;
        Mode = mode;
        TerminateActiveConnections = terminateActiveConnections;
        Inspection = inspection;
        Plan = plan;
        RestoreTimeout = TimeSpan.FromSeconds(Math.Ceiling(timeout.TotalSeconds));
    }

    public string DatabaseName { get; }
    public DatabaseServerPath ServerFile { get; }
    public DatabaseRestoreMode Mode { get; }
    public bool TerminateActiveConnections { get; }
    public RestoreBackupInspection Inspection { get; }
    public RestorePlan Plan { get; }
    public TimeSpan RestoreTimeout { get; }
}

/// <summary>
/// The outcome of <see cref="IDatabaseRestoreProvider.ExecuteRestoreAsync"/>. <see cref="TargetState"/> always says what the target database
/// was left as: a cancelled or failed restore never implies a healthy database.
/// </summary>
public sealed record DatabaseRestoreResult
{
    public DatabaseRestoreResult(
        DatabaseRestoreStatus status,
        RestoreFailure? failure,
        DatabaseRestoreTargetState targetState,
        string? databaseState,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        DateTimeOffset? restoreStartedAt,
        DateTimeOffset? restoreFinishedAt,
        IReadOnlyList<RestoreFileMapping> fileMappings,
        IEnumerable<BackupWarning> warnings)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown restore status.");
        }

        if (!Enum.IsDefined(targetState))
        {
            throw new ArgumentOutOfRangeException(nameof(targetState), targetState, "Unknown target state.");
        }

        ArgumentNullException.ThrowIfNull(fileMappings);
        ArgumentNullException.ThrowIfNull(warnings);
        if (status == DatabaseRestoreStatus.Succeeded)
        {
            if (failure is not null || targetState != DatabaseRestoreTargetState.Online)
            {
                throw new ArgumentException("A successful restore has no failure and an online target.", nameof(status));
            }
        }
        else if (failure is null || targetState == DatabaseRestoreTargetState.Online
            || (status == DatabaseRestoreStatus.Cancelled) != (failure.Kind == RestoreFailureKind.Cancelled))
        {
            throw new ArgumentException("A failed or cancelled restore has a matching failure and a target that is not an online restore.", nameof(status));
        }

        Status = status;
        Failure = failure;
        TargetState = targetState;
        DatabaseState = databaseState;
        StartedAt = startedAt;
        FinishedAt = finishedAt;
        RestoreStartedAt = restoreStartedAt;
        RestoreFinishedAt = restoreFinishedAt;
        FileMappings = fileMappings;
        Warnings = warnings.ToArray();
    }

    public DatabaseRestoreStatus Status { get; }
    public RestoreFailure? Failure { get; }
    public DatabaseRestoreTargetState TargetState { get; }

    /// <summary>The state the engine reports for the target database after the attempt (for example <c>ONLINE</c>, <c>RESTORING</c>); null when it could not be read.</summary>
    public string? DatabaseState { get; }

    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset FinishedAt { get; }

    /// <summary>Start and end of the restore statement itself; null when it never started or never finished.</summary>
    public DateTimeOffset? RestoreStartedAt { get; }
    public DateTimeOffset? RestoreFinishedAt { get; }
    public IReadOnlyList<RestoreFileMapping> FileMappings { get; }
    public IReadOnlyList<BackupWarning> Warnings { get; }
}

/// <summary>
/// Stable reason codes for the engine-independent restore conditions. A provider reports these exact codes (as
/// <see cref="BackupPreCheckItem.ReasonCode"/> or <see cref="RestoreFailure.ReasonCode"/>) so the agent pipeline can map them to the
/// platform's failure vocabulary without knowing the engine; provider-specific reasons stay the provider's own.
/// </summary>
public static class RestoreReasonCodes
{
    /// <summary>NewDatabase was requested and a database of that name already exists.</summary>
    public const string TargetDatabaseExists = "target-database-exists";

    /// <summary>ReplaceExistingDatabase was requested and no database of that name exists.</summary>
    public const string TargetDatabaseMissing = "target-database-missing";

    /// <summary>Sessions are connected to the target database and the user did not allow disconnecting them.</summary>
    public const string TargetInUse = "target-database-in-use";

    /// <summary>The target is a system database, or a database the engine does not allow replacing (mirroring, availability group).</summary>
    public const string TargetProtected = "target-database-protected";

    /// <summary>The backup is not a full database backup (differential, log, file/partial) or has more than one backup set.</summary>
    public const string BackupTypeUnsupported = "backup-type-unsupported";

    /// <summary>The backup was taken by a newer engine version than the target runs.</summary>
    public const string TargetVersionTooOld = "target-version-too-old";

    /// <summary>The target volume does not have room for the restored files.</summary>
    public const string InsufficientDiskSpace = "disk-space-insufficient";

    /// <summary>A logical file could not be mapped to a safe destination (unsupported file type, no usable target directory).</summary>
    public const string FileMappingFailed = "file-mapping-failed";
}
