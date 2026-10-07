namespace BackupPlatform.BackupProviders.Abstractions.Database;

/// <summary>
/// An engine's restore surface, narrow on purpose: engine-specific detail (default directories, file naming, session handling, the
/// overwrite option) stays in the provider and surfaces through typed results and pre-check items. The runtime hosting the engine is not
/// part of this contract. The calling pipeline stages the backup file where the SERVER can read it and drives the four steps in order:
/// <see cref="PreCheckRestoreAsync"/> (target only), <see cref="InspectBackupAsync"/> (needs the file), <see cref="PlanRestoreAsync"/>
/// (backup against target) and <see cref="ExecuteRestoreAsync"/>. Pre-check results use the platform's Passed / Warning / Failed items.
/// </summary>
public interface IDatabaseRestoreProvider
{
    DatabaseEngineId Engine { get; }

    /// <summary>
    /// Checks everything that does not need the backup file: the server is reachable, the login authenticates and may restore, the target
    /// database state fits the mode, active sessions, protected databases, and the target's default directories. Expected problems are
    /// failed items, not exceptions; nothing on the target is changed. Cancellation surfaces as <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<BackupPreCheckReport> PreCheckRestoreAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        RestorePreCheckRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the staged backup with the engine itself: header, logical files and (optionally) the engine's verification. A file the server
    /// cannot open or that is not a valid backup is a <see cref="RestoreInspectionResult.Failure"/>, never an exception. Changes nothing.
    /// </summary>
    Task<RestoreInspectionResult> InspectBackupAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        RestoreInspectionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Compares the inspected backup with the target (backup type, version direction, existing database and its relation to the backup,
    /// file types) and, when nothing fails, maps every logical file to a destination generated from the TARGET's directories. Changes nothing.
    /// </summary>
    Task<RestorePlanResult> PlanRestoreAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        RestorePlanRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs the restore. Expected failures and caller cancellation are returned as a <see cref="DatabaseRestoreResult"/> (never thrown);
    /// cancellation stops the running engine statement and the result states what the target was left as. Whatever session change was made
    /// to the target (single-user access) is undone before returning whenever the engine allows it. Only argument errors throw.
    /// <paramref name="progress"/> is best-effort and can never change the outcome.
    /// </summary>
    Task<DatabaseRestoreResult> ExecuteRestoreAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        DatabaseRestoreRequest request,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken);
}
