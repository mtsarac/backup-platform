using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.BackupProviders.MSSQL.Backup;

/// <summary>
/// What one execution has learned and decided so far. The primary outcome (a failure or a cancellation) is recorded here once and
/// is never replaced afterwards: cleanup problems and late observations only add warnings, so they cannot hide why the execution
/// stopped. <see cref="ToResult"/> is the single place the result is assembled.
/// </summary>
internal sealed class FullBackupExecutionState
{
    private const string VerificationMethod = "restore-verifyonly";

    private readonly List<BackupWarning> _warnings = [];

    public DatabaseBackupSource? Source { get; set; }
    public BackupOptionsOutcome? Options { get; set; }
    public DateTimeOffset? BackupStartedAt { get; set; }
    public DateTimeOffset? BackupFinishedAt { get; set; }
    public StagedBackupFile? StagedFile { get; set; }
    public BackupVerification Verification { get; set; } = BackupVerification.NotRun;
    public BackupFailure? Failure { get; private set; }

    /// <summary>True once the BACKUP statement was sent: from then on a file created by this execution may exist.</summary>
    public bool BackupIssued { get; set; }

    public IReadOnlyList<BackupWarning> Warnings => _warnings;

    public bool IsCancelled => Failure?.Kind == BackupFailureKind.Cancelled;

    public ExecutionEnd End =>
        Failure is null ? ExecutionEnd.Succeeded
        : Failure.Kind == BackupFailureKind.Cancelled ? ExecutionEnd.Cancelled
        : Failure.Kind == BackupFailureKind.VerificationFailed ? ExecutionEnd.VerificationFailed
        : ExecutionEnd.BackupFailed;

    public void AddWarnings(IEnumerable<BackupWarning> warnings) => _warnings.AddRange(warnings);

    public void AddWarning(BackupWarning warning) => _warnings.Add(warning);

    /// <summary>Records the primary outcome. The first one wins.</summary>
    public void Fail(BackupFailureKind kind, BackupExecutionStage stage, string reasonCode, string message) =>
        Failure ??= new BackupFailure(
            kind, stage, reasonCode, message,
            kind == BackupFailureKind.Cancelled ? BackupFailureCause.Unclassified : SqlServerFailureCauses.ForBackupReason(reasonCode));

    public void Cancel(BackupExecutionStage stage) =>
        Fail(BackupFailureKind.Cancelled, stage, "cancelled", "The backup was cancelled.");

    public void MarkVerification(BackupVerificationStatus status, DateTimeOffset startedAt, DateTimeOffset finishedAt) =>
        Verification = new BackupVerification(status, VerificationMethod, startedAt, finishedAt);

    public FullBackupResult ToResult(DateTimeOffset startedAt, DateTimeOffset finishedAt, StagingCleanup cleanup)
    {
        var status = Failure switch
        {
            null => FullBackupStatus.Succeeded,
            { Kind: BackupFailureKind.Cancelled } => FullBackupStatus.Cancelled,
            _ => FullBackupStatus.Failed
        };

        return new FullBackupResult(
            status, Failure, Source, Options, startedAt, finishedAt, BackupStartedAt, BackupFinishedAt, Failure is null ? StagedFile : null, Verification, _warnings, cleanup);
    }
}
