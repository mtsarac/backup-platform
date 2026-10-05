using System.Diagnostics;
using System.Runtime.ExceptionServices;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Capabilities;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.MSSQL.Backup;

/// <summary>Timing knobs of an execution; the defaults are the production values, tests shorten them.</summary>
internal sealed record SqlServerExecutionSettings(TimeSpan StagingVisibilityTimeout, TimeSpan ProgressInterval)
{
    public static SqlServerExecutionSettings Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
}

/// <summary>The outcome of one <c>RESTORE VERIFYONLY</c>: passed, or failed with a stable reason and a fixed user-safe message.</summary>
internal sealed record SqlServerVerifyOutcome(bool Passed, string? ReasonCode, string? Message, DateTimeOffset StartedAt, DateTimeOffset FinishedAt);

/// <summary>
/// One verified full backup: pre-conditions, <c>BACKUP DATABASE</c>, <c>RESTORE VERIFYONLY</c>, and the cleanup of what a failed
/// or cancelled run leaves behind. Everything runs on one dedicated, non-pooled connection that is always disposed before the
/// staged file is touched: closing the connection kills any statement still running, so no backup is orphaned on the server.
/// Expected failures and cancellation are returned as a <see cref="FullBackupResult"/>; only argument errors throw.
/// <para>
/// Fast-fail watchdog: the pre-check cannot always prove that the SQL service account can write to the staging directory or that
/// the agent sees the same directory (a least-privilege login cannot run the probes). So while BACKUP runs, the agent watches for
/// the staged file; if it has not appeared within <see cref="SqlServerExecutionSettings.StagingVisibilityTimeout"/> while the
/// statement is still running, the statement is cancelled and the execution fails with <c>staging-mapping-mismatch</c> instead of
/// writing a backup nobody can find. A BACKUP that fails at once while opening the device fails with <c>staging-write-failed</c>.
/// </para>
/// </summary>
internal sealed partial class SqlServerFullBackupExecutor(ILogger logger, TimeProvider time, SqlServerExecutionSettings settings)
{
    public async Task<FullBackupResult> ExecuteAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        FullBackupRequest request,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        var reporter = new BackupProgressReporter(progress, time, logger);
        var state = new FullBackupExecutionState();
        var agent = new AgentStagingDirectory(request.Staging.AgentDirectory);

        try
        {
            await RunAsync(connection, credential, request, agent, reporter, state, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Cancel(reporter.CurrentStage);
        }
        catch (Exception exception)
        {
            LogUnexpectedFailure(logger, exception, reporter.CurrentStage);
            state.Fail(BackupFailureKind.SqlBackupFailed, reporter.CurrentStage, SqlServerBackupReasons.UnexpectedError,
                "An unexpected error stopped the backup. Details are in the agent log.");
        }

        // The connection is closed here (disposed inside RunAsync), so SQL Server no longer writes the file.
        var action = StagingCleanupPolicy.Decide(state.End, request.OnFailure, state.BackupIssued);
        var existed = action != StagingCleanupAction.None && agent.FileExists(request.StagedFileName);
        var deleted = action == StagingCleanupAction.Delete && existed && agent.TryDeleteFile(request.StagedFileName);
        var (cleanup, warning) = StagingCleanupPolicy.Describe(action, existed, deleted);
        if (warning is not null)
        {
            state.AddWarning(warning);
        }

        return state.ToResult(startedAt, time.GetUtcNow(), cleanup);
    }

    /// <summary>Runs <c>RESTORE VERIFYONLY</c> against a file the server can see, on a fresh connection.</summary>
    public async Task<SqlServerVerifyOutcome> VerifyFileAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        DatabaseServerPath serverFile,
        bool checksumApplied,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var session = await SqlServerSession.OpenAsync(connection, credential, logger, cancellationToken).ConfigureAwait(false);
        return await VerifyAsync(session, serverFile, checksumApplied, timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        FullBackupRequest request,
        AgentStagingDirectory agent,
        BackupProgressReporter reporter,
        FullBackupExecutionState state,
        CancellationToken cancellationToken)
    {
        reporter.EnterStage(BackupExecutionStage.Preparing);

        SqlServerSession opened;
        try
        {
            opened = await SqlServerSession.OpenAsync(connection, credential, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseProviderException exception)
        {
            FailPreparing(state, exception);
            return;
        }

        await using var session = opened;

        SqlServerInstanceInfo instance;
        SqlServerDatabaseInfo? database;
        try
        {
            instance = await SqlServerDiscovery.ReadInstanceAsync(session, cancellationToken).ConfigureAwait(false);
            database = await SqlServerDiscovery.FindDatabaseAsync(session, request.DatabaseName, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseProviderException exception)
        {
            FailPreparing(state, exception);
            return;
        }

        if (database is null)
        {
            state.Fail(BackupFailureKind.PreCheckFailed, BackupExecutionStage.Preparing, SqlServerBackupReasons.DatabaseNotFound,
                "The database does not exist on this instance, or the login cannot see it.");
            return;
        }

        state.Source = SqlServerInstanceInfoMapping.ToBackupSource(instance, database);
        var capabilities = SqlServerCapabilityEvaluator.EvaluateDatabase(SqlServerCapabilityEvaluator.EvaluateInstance(instance), database);
        var resolution = SqlServerBackupOptionResolver.Resolve(request.Options, capabilities);
        state.Options = resolution.Outcome;
        state.AddWarnings(resolution.Warnings);

        if (database.State != SqlServerDatabaseState.Online)
        {
            state.Fail(BackupFailureKind.PreCheckFailed, BackupExecutionStage.Preparing, SqlServerBackupReasons.DatabaseNotOnline,
                "The database is not online and cannot be backed up.");
            return;
        }

        if (capabilities.SupportOf(SqlServerCapabilityIds.FullBackup) == DatabaseCapabilitySupport.Unsupported)
        {
            state.Fail(BackupFailureKind.PreCheckFailed, BackupExecutionStage.Preparing, SqlServerBackupReasons.BackupNotSupported,
                "A full backup of this database is not supported.");
            return;
        }

        if (resolution.UnsupportedRequired.Count > 0)
        {
            state.Fail(BackupFailureKind.PreCheckFailed, BackupExecutionStage.Preparing, SqlServerBackupReasons.RequiredOptionUnsupported,
                $"A required backup option is not supported here: {string.Join(", ", resolution.UnsupportedRequired)}.");
            return;
        }

        // Verification is part of every backup; a login that cannot run it would only find out after the whole backup.
        SqlServerBackupPermission verifyPermission;
        try
        {
            verifyPermission = await SqlServerDiscovery.ProbeVerifyPermissionAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseProviderException exception)
        {
            FailPreparing(state, exception);
            return;
        }

        if (verifyPermission == SqlServerBackupPermission.Denied)
        {
            state.Fail(BackupFailureKind.PreCheckFailed, BackupExecutionStage.Preparing, SqlServerBackupReasons.VerifyPermissionDenied,
                "The SQL Server login may not run RESTORE VERIFYONLY, which every backup needs (the server-level CREATE ANY DATABASE permission).");
            return;
        }

        if (verifyPermission == SqlServerBackupPermission.Unknown)
        {
            state.AddWarning(new BackupWarning(SqlServerBackupWarningCodes.VerifyPermissionUnknown,
                "SQL Server did not report whether the login may run RESTORE VERIFYONLY; the verification step will tell."));
        }

        if (!agent.Inspect().DirectoryExists)
        {
            state.Fail(BackupFailureKind.SqlBackupFailed, BackupExecutionStage.Preparing, SqlServerBackupReasons.StagingAgentDirectoryMissing,
                "The staging directory does not exist on the agent's side.");
            return;
        }

        if (agent.FileExists(request.StagedFileName))
        {
            state.Fail(BackupFailureKind.SqlBackupFailed, BackupExecutionStage.Preparing, SqlServerBackupReasons.StagedFileExists,
                "A file with the staged file name already exists in the staging directory; it was not touched.");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var serverFile = request.Staging.ServerDirectory.Combine(request.StagedFileName);
        if (!await BackUpAsync(session, request, agent, reporter, state, resolution, serverFile, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        reporter.EnterStage(BackupExecutionStage.Verifying);
        var outcome = await VerifyAsync(session, serverFile, resolution.Outcome.Applied.Checksum, request.VerifyTimeout, cancellationToken).ConfigureAwait(false);
        if (!outcome.Passed)
        {
            state.MarkVerification(BackupVerificationStatus.Failed, outcome.StartedAt, outcome.FinishedAt);
            state.Fail(BackupFailureKind.VerificationFailed, BackupExecutionStage.Verifying, outcome.ReasonCode!, outcome.Message!);
            return;
        }

        state.MarkVerification(BackupVerificationStatus.Passed, outcome.StartedAt, outcome.FinishedAt);
        if (agent.FileLength(request.StagedFileName) is not { } size)
        {
            state.Fail(BackupFailureKind.SqlBackupFailed, BackupExecutionStage.Verifying, SqlServerBackupReasons.StagedFileMissing,
                "The staged backup file disappeared before it could be handed over.");
            return;
        }

        state.StagedFile = new StagedBackupFile(serverFile, new AgentPath(agent.FilePath(request.StagedFileName)), request.StagedFileName, size);
    }

    /// <summary>Runs the BACKUP statement under the staging watchdog and progress timer. Returns true when it completed and the file is visible to the agent.</summary>
    private async Task<bool> BackUpAsync(
        SqlServerSession session,
        FullBackupRequest request,
        AgentStagingDirectory agent,
        BackupProgressReporter reporter,
        FullBackupExecutionState state,
        SqlServerOptionResolution resolution,
        DatabaseServerPath serverFile,
        CancellationToken cancellationToken)
    {
        reporter.EnterStage(BackupExecutionStage.BackingUp);
        var command = SqlServerBackupCommandBuilder.BuildFullBackup(
            request.DatabaseName, serverFile, resolution.Outcome.Applied, resolution.CompressionCapabilitySupported, stats: true);

        using var statementCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        state.BackupIssued = true;
        state.BackupStartedAt = time.GetUtcNow();
        var statement = session.ExecuteAsync(command, request.BackupTimeout, reporter.OnInfoMessage, statementCancellation.Token);

        var watchdogFired = false;
        var seenByAgent = false;
        var elapsed = Stopwatch.StartNew();
        using (var timer = new PeriodicTimer(settings.ProgressInterval))
        {
            while (await Task.WhenAny(statement, timer.WaitForNextTickAsync(CancellationToken.None).AsTask()).ConfigureAwait(false) != statement)
            {
                if (agent.FileLength(request.StagedFileName) is { } length)
                {
                    seenByAgent = true;
                    reporter.ReportBytes(length);
                }
                else if (!seenByAgent && !watchdogFired && elapsed.Elapsed >= settings.StagingVisibilityTimeout)
                {
                    watchdogFired = true;
                    LogStagingFileNotVisible(logger, (int)settings.StagingVisibilityTimeout.TotalSeconds);
                    await statementCancellation.CancelAsync().ConfigureAwait(false);
                }
            }
        }

        Exception? failure = null;
        try
        {
            await statement.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is null)
        {
            state.BackupFinishedAt = time.GetUtcNow();
            if (agent.FileLength(request.StagedFileName) is not { } size)
            {
                state.Fail(BackupFailureKind.SqlBackupFailed, BackupExecutionStage.BackingUp, SqlServerBackupReasons.StagingMappingMismatch, MappingMismatchMessage);
                state.AddWarning(new BackupWarning(SqlServerBackupWarningCodes.ServerFileMayRemain,
                    "A backup file may remain in the staging directory on the server side; the agent cannot see it and cannot remove it."));
                return false;
            }

            reporter.ReportPercent(100);
            reporter.ReportBytes(size);
            return true;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (watchdogFired)
        {
            state.Fail(BackupFailureKind.SqlBackupFailed, BackupExecutionStage.BackingUp, SqlServerBackupReasons.StagingMappingMismatch, MappingMismatchMessage);
            state.AddWarning(new BackupWarning(SqlServerBackupWarningCodes.ServerFileMayRemain,
                "A partial backup file may remain in the staging directory on the server side; the agent cannot see it and cannot remove it."));
            return false;
        }

        if (failure is SqlException sql)
        {
            var numbers = ErrorNumbers(sql);
            LogStatementFailed(logger, sql, "BACKUP", string.Join(',', numbers));
            var (reason, message) = SqlServerBackupFailureClassifier.Classify(SqlServerStatement.Backup, numbers);
            state.Fail(BackupFailureKind.SqlBackupFailed, BackupExecutionStage.BackingUp, reason, message);
            return false;
        }

        ExceptionDispatchInfo.Capture(failure).Throw();
        return false;
    }

    private async Task<SqlServerVerifyOutcome> VerifyAsync(
        SqlServerSession session,
        DatabaseServerPath serverFile,
        bool checksumApplied,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        try
        {
            await session.ExecuteAsync(SqlServerBackupCommandBuilder.BuildVerifyOnly(serverFile, checksumApplied), timeout, onInfoMessage: null, cancellationToken)
                .ConfigureAwait(false);
            return new SqlServerVerifyOutcome(true, null, null, startedAt, time.GetUtcNow());
        }
        catch (SqlException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var numbers = ErrorNumbers(exception);
            LogStatementFailed(logger, exception, "RESTORE VERIFYONLY", string.Join(',', numbers));
            var (reason, message) = SqlServerBackupFailureClassifier.Classify(SqlServerStatement.Verify, numbers);
            return new SqlServerVerifyOutcome(false, reason, message, startedAt, time.GetUtcNow());
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static void FailPreparing(FullBackupExecutionState state, DatabaseProviderException exception)
    {
        var reason = exception.Kind switch
        {
            DatabaseFailureKind.AuthenticationFailed => SqlServerBackupReasons.AuthenticationFailed,
            DatabaseFailureKind.PermissionDenied => SqlServerBackupReasons.PermissionDenied,
            _ => SqlServerBackupReasons.ServerUnavailable
        };
        state.Fail(BackupFailureKind.PreCheckFailed, BackupExecutionStage.Preparing, reason, exception.Message);
    }

    private static int[] ErrorNumbers(SqlException exception) => exception.Errors.Cast<SqlError>().Select(error => error.Number).ToArray();

    private const string MappingMismatchMessage =
        "The agent cannot see the file SQL Server is writing. Check that the server and agent staging directories are the same storage (the staging mapping).";

    [LoggerMessage(EventId = 22, Level = LogLevel.Warning, Message = "SQL Server statement failed: Statement={Statement} SqlErrorNumbers={SqlErrorNumbers}")]
    private static partial void LogStatementFailed(ILogger logger, Exception exception, string statement, string sqlErrorNumbers);

    [LoggerMessage(EventId = 23, Level = LogLevel.Error, Message = "Unexpected failure during a full backup: Stage={Stage}")]
    private static partial void LogUnexpectedFailure(ILogger logger, Exception exception, BackupExecutionStage stage);

    [LoggerMessage(EventId = 24, Level = LogLevel.Warning,
        Message = "The staged file did not become visible to the agent within {Seconds}s while BACKUP was running; cancelling the statement.")]
    private static partial void LogStagingFileNotVisible(ILogger logger, int seconds);
}
