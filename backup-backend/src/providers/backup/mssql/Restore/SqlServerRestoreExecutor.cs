using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.MSSQL.Restore;

/// <summary>Timing knobs of a restore execution; the defaults are the production values, tests may shorten them.</summary>
internal sealed record SqlServerRestoreSettings(TimeSpan AccessChangeTimeout)
{
    /// <summary>
    /// The longest an <c>ALTER DATABASE ... SET SINGLE_USER / MULTI_USER</c> may take. <c>ROLLBACK IMMEDIATE</c> waits for the open transactions of
    /// the disconnected sessions to roll back, which can take minutes, so it is longer than an ordinary command timeout; it is still finite.
    /// </summary>
    public static SqlServerRestoreSettings Default { get; } = new(TimeSpan.FromMinutes(5));
}

/// <summary>
/// One restore: re-check of the target, optional session handling for a replacement, <c>RESTORE DATABASE</c>, and the honest report of what
/// the target was left as. Everything runs on one dedicated, non-pooled connection (<see cref="SqlServerConnectionStringFactory"/> never pools),
/// so closing it ends any statement still running on it. Expected failures and caller cancellation are returned as a
/// <see cref="DatabaseRestoreResult"/>; only argument errors throw, and they throw before any connection is opened.
/// <list type="number">
/// <item><b>PreparingTarget.</b> The target is looked at again right before it is changed (a restore can wait minutes behind a download):
/// a new database whose name is taken now fails with <c>target-database-exists</c> and nothing is touched, and so does a protected target.
/// A replacement with sessions connected and <c>TerminateActiveConnections</c> allowed switches the database to SINGLE_USER (rolling their work
/// back); without that permission, or without sessions, the access mode is never changed.</item>
/// <item><b>Restoring.</b> The statement runs under the caller's token and the request's finite timeout; cancelling the token cancels the
/// statement on the server (the technique of <see cref="SqlServerSession.ExecuteAsync"/>). Percent comes from <c>STATS = 1</c>.</item>
/// <item><b>Finalizing.</b> A restored database that came back in a restricted access mode (the backup was taken from one) is switched to
/// MULTI_USER, so a success always means ONLINE and MULTI_USER.</item>
/// <item><b>Always.</b> If the SINGLE_USER switch was made and the restore did not succeed, MULTI_USER is restored: on the same connection, or on a
/// new one when that is gone, and also after a failure, a cancellation or a timeout. A database that is RESTORING cannot be altered (error 5052) and is
/// only reported. Then the target is read once more on a fresh connection and <see cref="SqlServerRestoreOutcomeRules"/> turns the observation into the
/// reported <see cref="DatabaseRestoreTargetState"/>. A partially restored database is never dropped.</item>
/// </list>
/// </summary>
internal sealed partial class SqlServerRestoreExecutor(ILogger logger, TimeProvider time, SqlServerRestoreSettings settings)
{
    public async Task<DatabaseRestoreResult> ExecuteAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        DatabaseRestoreRequest request,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        var statements = Prepare(request);
        var startedAt = time.GetUtcNow();
        var reporter = new SqlServerRestoreProgressReporter(progress, time, logger);
        var run = new RestoreRun();

        SqlServerSession? session = null;
        try
        {
            try
            {
                session = await SqlServerSession.OpenAsync(connection, credential, logger, cancellationToken).ConfigureAwait(false);
            }
            catch (DatabaseProviderException exception)
            {
                run.ConnectFailed = true;
                run.Fail(SqlServerRestoreFailureClassifier.FromConnection(exception), reporter.CurrentStage);
            }

            if (session is not null)
            {
                await StepsAsync(session, statements, request, reporter, run, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Cancelled = true;
        }
        catch (DatabaseProviderException exception)
        {
            run.Fail(SqlServerRestoreFailureClassifier.FromConnection(exception), reporter.CurrentStage);
        }
        catch (Exception exception)
        {
            LogUnexpectedFailure(logger, exception, reporter.CurrentStage);
            run.Fail(
                new SqlServerRestoreFailureInfo(RestoreFailureKind.SqlRestoreFailed, SqlServerRestoreReasons.UnexpectedError,
                    "An unexpected error stopped the restore. Details are in the agent log.", BackupFailureCause.Unclassified),
                reporter.CurrentStage);
        }
        finally
        {
            // Whatever happened above, a database this provider put into SINGLE_USER must not stay there.
            if (run.AccessChanged && !run.RestoreSucceeded)
            {
                await RestoreMultiUserAsync(session, connection, credential, request, statements, run).ConfigureAwait(false);
            }

            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        var after = await ObserveAfterAsync(connection, credential, request, run).ConfigureAwait(false);
        var outcome = SqlServerRestoreOutcomeRules.Determine(new SqlServerRestoreOutcomeInput(
            request.Mode, run.RestoreIssued, run.RestoreSucceeded, run.AccessChanged, run.Before, after.Observable, after.Database));

        var warnings = new List<BackupWarning>(run.Warnings);
        warnings.AddRange(outcome.Warnings);

        DatabaseRestoreStatus status;
        RestoreFailure? failure;
        if (outcome.TargetState == DatabaseRestoreTargetState.Online)
        {
            // The statement completed and the database is ONLINE and MULTI_USER: a cancellation that arrived afterwards changes nothing.
            status = DatabaseRestoreStatus.Succeeded;
            failure = null;
        }
        else if (run.Cancelled)
        {
            status = DatabaseRestoreStatus.Cancelled;
            failure = new RestoreFailure(RestoreFailureKind.Cancelled, reporter.CurrentStage, SqlServerRestoreReasons.RestoreCancelled, "The restore was cancelled.");
        }
        else
        {
            status = DatabaseRestoreStatus.Failed;
            failure = run.Failure
                ?? new RestoreFailure(RestoreFailureKind.SqlRestoreFailed, RestoreExecutionStage.Finalizing, SqlServerRestoreReasons.TargetNotOnline,
                    "The restore finished but the database is not ONLINE and MULTI_USER, so it was not reported as restored.");
        }

        return new DatabaseRestoreResult(
            status, failure, outcome.TargetState, outcome.DatabaseState, startedAt, time.GetUtcNow(),
            run.RestoreStartedAt, run.RestoreFinishedAt, request.Plan.Mappings, warnings);
    }

    /// <summary>Builds every statement up front, so an argument error (control characters, an unmapped file) throws before any connection is opened.</summary>
    private static RestoreStatements Prepare(DatabaseRestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var header = request.Inspection.Header;
        if (header.Kind != RestoreBackupKind.Database)
        {
            throw new ArgumentException("Only a full database backup can be restored.", nameof(request));
        }

        var files = request.Inspection.Files;
        var mappings = request.Plan.Mappings;
        if (mappings.Any(mapping => mapping.Kind is not (RestoreFileKind.Data or RestoreFileKind.Log))
            || files.Count != mappings.Count
            || !files.All(file => mappings.Count(mapping =>
                string.Equals(mapping.LogicalName, file.LogicalName, StringComparison.OrdinalIgnoreCase) && mapping.Kind == file.Kind) == 1))
        {
            // RESTORE puts a file without a MOVE back at its SOURCE path, so a plan that does not cover exactly the backup's files is refused.
            throw new ArgumentException("The plan must map every data and log file of the backup exactly once and no other file.", nameof(request));
        }

        var replace = request.Mode == DatabaseRestoreMode.ReplaceExistingDatabase;
        return new RestoreStatements(
            SqlServerRestoreCommandBuilder.BuildRestoreDatabase(request.DatabaseName, request.ServerFile, mappings, replace, header.HasChecksums, stats: true),
            SqlServerRestoreCommandBuilder.BuildSetSingleUser(request.DatabaseName),
            SqlServerRestoreCommandBuilder.BuildSetMultiUser(request.DatabaseName));
    }

    private async Task StepsAsync(
        SqlServerSession session,
        RestoreStatements statements,
        DatabaseRestoreRequest request,
        SqlServerRestoreProgressReporter reporter,
        RestoreRun run,
        CancellationToken cancellationToken)
    {
        reporter.EnterStage(RestoreExecutionStage.PreparingTarget);
        var replace = request.Mode == DatabaseRestoreMode.ReplaceExistingDatabase;

        // The target as it is NOW, not as it was when the pre-check looked.
        var info = await SqlServerDiscovery.FindDatabaseAsync(session, request.DatabaseName, cancellationToken).ConfigureAwait(false);
        var observed = await SqlServerRestoreTargetReader.ObserveAsync(session, request.DatabaseName, cancellationToken).ConfigureAwait(false);
        var inAvailabilityGroup = replace && info is not null
            ? await SqlServerRestoreTargetReader.OptionalAsync(
                async () => (bool?)await SqlServerRestoreTargetReader.IsInAvailabilityGroupAsync(session, request.DatabaseName, cancellationToken).ConfigureAwait(false), null).ConfigureAwait(false)
            : null;
        var mirrored = replace && info is not null
            ? await SqlServerRestoreTargetReader.OptionalAsync(
                async () => (bool?)await SqlServerRestoreTargetReader.IsMirroredAsync(session, request.DatabaseName, cancellationToken).ConfigureAwait(false), null).ConfigureAwait(false)
            : null;

        var facts = new SqlServerRestoreTargetFacts(null, null, null, info, null, inAvailabilityGroup, mirrored, null);
        var protection = SqlServerRestoreChecks.CheckProtected(request.DatabaseName, request.Mode, facts);
        var exists = SqlServerRestoreChecks.CheckExists(request.Mode, info);
        foreach (var item in new[] { protection, exists })
        {
            if (item is { Status: BackupPreCheckStatus.Failed })
            {
                run.Fail(new SqlServerRestoreFailureInfo(RestoreFailureKind.PreCheckFailed, item.ReasonCode, item.Message, item.Cause), reporter.CurrentStage);
                return;
            }
        }

        run.Before = replace ? observed : null;

        if (replace && request.TerminateActiveConnections && observed is { State: SqlServerDatabaseState.Online })
        {
            // Unknown sessions count as "maybe some": the caller allowed disconnecting them, and SINGLE_USER is also what makes the exclusive access certain.
            var sessions = await SqlServerRestoreTargetReader.OptionalAsync(
                async () => (int?)await SqlServerRestoreTargetReader.CountSessionsAsync(session, request.DatabaseName, cancellationToken).ConfigureAwait(false), null).ConfigureAwait(false);
            if (sessions is null or > 0)
            {
                run.AccessChanged = true;
                if (!await ExecuteStatementAsync(session, statements.SetSingleUser, settings.AccessChangeTimeout, null, SqlServerRestoreStatement.AccessChange, reporter, run, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (sessions > 0)
                {
                    run.Warnings.Add(new BackupWarning(SqlServerRestoreWarningCodes.SessionsDisconnected,
                        $"{sessions} session(s) connected to the database were disconnected and their open transactions rolled back so the restore could take the database."));
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        reporter.EnterStage(RestoreExecutionStage.Restoring);
        cancellationToken.ThrowIfCancellationRequested();
        run.RestoreIssued = true;
        run.RestoreStartedAt = time.GetUtcNow();
        if (!await ExecuteStatementAsync(session, statements.Restore, request.RestoreTimeout, reporter.OnInfoMessage, SqlServerRestoreStatement.Restore, reporter, run, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        run.RestoreFinishedAt = time.GetUtcNow();
        run.RestoreSucceeded = true;
        reporter.ReportPercent(100);

        // From here the restore is done: a late cancellation must not stop the finishing touch, and its failure cannot undo the restore.
        reporter.EnterStage(RestoreExecutionStage.Finalizing);
        await NormalizeAccessAsync(session, request, statements, run).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one statement. Returns true when it completed. A failed statement is classified and recorded (the run stops); caller cancellation surfaces as
    /// <see cref="OperationCanceledException"/>. The guard of a new-database restore (<see cref="SqlServerRestoreCommandBuilder.TargetExistsErrorNumber"/>)
    /// means the RESTORE itself never ran.
    /// </summary>
    private async Task<bool> ExecuteStatementAsync(
        SqlServerSession session,
        string sql,
        TimeSpan timeout,
        Action<SqlInfoMessageEventArgs>? onInfoMessage,
        SqlServerRestoreStatement statement,
        SqlServerRestoreProgressReporter reporter,
        RestoreRun run,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.ExecuteAsync(sql, timeout, onInfoMessage, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqlException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var numbers = SqlServerRestoreFailureClassifier.ErrorNumbers(exception);
            LogStatementFailed(logger, exception, statement.ToString(), string.Join(',', numbers));
            if (numbers.Contains(SqlServerRestoreCommandBuilder.TargetExistsErrorNumber))
            {
                run.RestoreIssued = false;
            }

            run.Fail(SqlServerRestoreFailureClassifier.Classify(statement, numbers), reporter.CurrentStage);
            return false;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>
    /// A restored database takes the access mode of the database that was backed up. A backup taken from a SINGLE_USER or RESTRICTED_USER database
    /// would otherwise leave an unusable "success", so it is switched to MULTI_USER and the run says so. Best effort: what cannot be switched is reported
    /// by the final observation, never hidden.
    /// </summary>
    private async Task NormalizeAccessAsync(SqlServerSession session, DatabaseRestoreRequest request, RestoreStatements statements, RestoreRun run)
    {
        try
        {
            var current = await SqlServerRestoreTargetReader.ObserveAsync(session, request.DatabaseName, CancellationToken.None).ConfigureAwait(false);
            if (current is { State: SqlServerDatabaseState.Online, UserAccess: not SqlServerUserAccess.MultiUser })
            {
                await session.ExecuteAsync(statements.SetMultiUser, settings.AccessChangeTimeout, onInfoMessage: null, CancellationToken.None).ConfigureAwait(false);
                run.Warnings.Add(new BackupWarning(SqlServerRestoreWarningCodes.UserAccessNormalized,
                    "The restored database came back in a restricted access mode (the backup was taken from one); it was switched to MULTI_USER."));
            }
        }
        catch (Exception exception) when (exception is SqlException or DatabaseProviderException)
        {
            LogFinishingStepFailed(logger, exception, "normalize-access");
        }
    }

    /// <summary>
    /// Undoes the SINGLE_USER switch after a restore that did not succeed. Tries the connection the restore ran on and, if that fails (it can be gone
    /// after a lost connection), a fresh one. Never throws and ignores the caller's token: a cancelled run must still leave the database usable.
    /// </summary>
    private async Task RestoreMultiUserAsync(
        SqlServerSession? existing,
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        DatabaseRestoreRequest request,
        RestoreStatements statements,
        RestoreRun run)
    {
        if (existing is not null && await TryMultiUserAsync(existing, request, statements, run).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await using var fresh = await SqlServerSession.OpenAsync(connection, credential, logger, CancellationToken.None).ConfigureAwait(false);
            await TryMultiUserAsync(fresh, request, statements, run).ConfigureAwait(false);
        }
        catch (DatabaseProviderException exception)
        {
            LogFinishingStepFailed(logger, exception, "undo-single-user-connect");
        }
    }

    private async Task<bool> TryMultiUserAsync(SqlServerSession session, DatabaseRestoreRequest request, RestoreStatements statements, RestoreRun run)
    {
        try
        {
            var current = await SqlServerRestoreTargetReader.ObserveAsync(session, request.DatabaseName, CancellationToken.None).ConfigureAwait(false);

            // Nothing to undo when the database is gone or already open to everyone; a RESTORING database cannot be altered (5052) and the
            // final observation reports it.
            if (current is null or { State: SqlServerDatabaseState.Restoring } or { UserAccess: SqlServerUserAccess.MultiUser })
            {
                return true;
            }

            await session.ExecuteAsync(statements.SetMultiUser, settings.AccessChangeTimeout, onInfoMessage: null, CancellationToken.None).ConfigureAwait(false);
            if (run.Before is { UserAccess: not SqlServerUserAccess.MultiUser and not SqlServerUserAccess.Unknown } before)
            {
                run.Warnings.Add(new BackupWarning(SqlServerRestoreWarningCodes.UserAccessChanged,
                    $"The database was in {(before.UserAccess == SqlServerUserAccess.SingleUser ? "SINGLE_USER" : "RESTRICTED_USER")} mode before the restore and is MULTI_USER now."));
            }

            return true;
        }
        catch (Exception exception) when (exception is SqlException or DatabaseProviderException)
        {
            LogFinishingStepFailed(logger, exception, "undo-single-user");
            return false;
        }
    }

    /// <summary>Reads the target on a connection of its own, after the restore connection is closed; a failure to read is reported as "not observable", never as a guess.</summary>
    private async Task<(bool Observable, SqlServerObservedDatabase? Database)> ObserveAfterAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        DatabaseRestoreRequest request,
        RestoreRun run)
    {
        if (run.ConnectFailed)
        {
            return (false, null);
        }

        try
        {
            await using var fresh = await SqlServerSession.OpenAsync(connection, credential, logger, CancellationToken.None).ConfigureAwait(false);
            return (true, await SqlServerRestoreTargetReader.ObserveAsync(fresh, request.DatabaseName, CancellationToken.None).ConfigureAwait(false));
        }
        catch (DatabaseProviderException exception)
        {
            LogFinishingStepFailed(logger, exception, "observe-target");
            return (false, null);
        }
    }

    private sealed record RestoreStatements(string Restore, string SetSingleUser, string SetMultiUser);

    private sealed class RestoreRun
    {
        public RestoreFailure? Failure { get; private set; }
        public bool Cancelled { get; set; }
        public bool ConnectFailed { get; set; }
        public bool AccessChanged { get; set; }
        public bool RestoreIssued { get; set; }
        public bool RestoreSucceeded { get; set; }
        public DateTimeOffset? RestoreStartedAt { get; set; }
        public DateTimeOffset? RestoreFinishedAt { get; set; }
        public SqlServerObservedDatabase? Before { get; set; }
        public List<BackupWarning> Warnings { get; } = [];

        /// <summary>The first failure wins: later steps only clean up.</summary>
        public void Fail(SqlServerRestoreFailureInfo info, RestoreExecutionStage stage) =>
            Failure ??= new RestoreFailure(info.Kind, stage, info.ReasonCode, info.Message, info.Cause);
    }

    [LoggerMessage(EventId = 42, Level = LogLevel.Warning, Message = "SQL Server restore statement failed: Statement={Statement} SqlErrorNumbers={SqlErrorNumbers}")]
    private static partial void LogStatementFailed(ILogger logger, Exception exception, string statement, string sqlErrorNumbers);

    [LoggerMessage(EventId = 43, Level = LogLevel.Error, Message = "Unexpected failure during a restore: Stage={Stage}")]
    private static partial void LogUnexpectedFailure(ILogger logger, Exception exception, RestoreExecutionStage stage);

    [LoggerMessage(EventId = 44, Level = LogLevel.Warning, Message = "A restore finishing step failed and was reported through the target state instead: Step={Step}")]
    private static partial void LogFinishingStepFailed(ILogger logger, Exception exception, string step);
}
