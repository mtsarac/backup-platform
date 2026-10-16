using System.Globalization;
using System.Text.Json;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql.Connectivity;
using BackupPlatform.BackupProviders.PostgreSql.Tools;

namespace BackupPlatform.BackupProviders.PostgreSql.Restore;

internal static class PostgreSqlRestoreExecutor
{
    private static readonly TimeSpan MaintenanceTimeout = TimeSpan.FromSeconds(30);

    public static async Task<DatabaseRestoreResult> ExecuteAsync(PostgreSqlRestoreProvider provider, DatabaseConnectionSettings connection,
        DatabaseCredential credential, DatabaseRestoreRequest request, IProgress<RestoreProgress>? progress, CancellationToken cancellationToken)
    {
        if (request.Mode == DatabaseRestoreMode.IsolatedVerification)
            return await ExecuteVerificationAsync(provider, connection, credential, request, progress, cancellationToken).ConfigureAwait(false);
        var started = provider.Clock.GetUtcNow();
        DateTimeOffset? restoreStarted = null, restoreFinished = null;
        var warnings = new List<BackupWarning>();
        var scratch = $"bp_restore_{Guid.NewGuid():N}";
        var retired = $"bp_old_{Guid.NewGuid():N}";
        PostgreSqlConnectionTarget? target = null;
        PostgreSqlSession? session = null;
        uint? scratchOid = null;
        uint? previousOid = null;
        var creationAttempted = false;
        var publicationAttempted = false;
        var published = false;
        var stage = RestoreExecutionStage.PreparingTarget;
        RestoreFailure? failure = null;
        var targetState = DatabaseRestoreTargetState.NotModified;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.RestoreTimeout);
        try
        {
            var token = deadline.Token;
            token.ThrowIfCancellationRequested();
            PostgreSqlRestoreProvider.ValidateName(request.DatabaseName, request.Mode);
            var path = PostgreSqlRestoreProvider.AgentFile(request.AgentFile);
            var tool = await provider.RestoreToolAsync(token).ConfigureAwait(false);
            // Re-read the actual staged archive, rather than trusting a supplied plan or stale inspection.
            var inspection = await PostgreSqlArchive.ReadAsync(path, tool, true, request.RestoreTimeout, provider.Clock, token).ConfigureAwait(false);
            target = PostgreSqlConnectionTarget.Create(connection, credential);
            session = await PostgreSqlSession.OpenAsync(target, "postgres", provider.Logger, token).ConfigureAwait(false);
            await session.ExecuteAsync("SET standard_conforming_strings = on; SET statement_timeout = '30s'", MaintenanceTimeout, token).ConfigureAwait(false);
            // Surviving pg_restore children only know the scratch name. Only this session can publish, so parent death cannot overwrite a target.
            await session.ExecuteAsync("SELECT pg_advisory_lock(hashtextextended(@name, 710842))", MaintenanceTimeout, token, ("name", request.DatabaseName)).ConfigureAwait(false);
            var items = new List<BackupPreCheckItem>();
            await PostgreSqlRestoreProvider.CheckTargetAsync(session, request.DatabaseName, request.Mode, request.TerminateActiveConnections, items, token).ConfigureAwait(false);
            await PostgreSqlRestoreProvider.CheckArchiveAsync(session, inspection, tool, request.Mode, items, token).ConfigureAwait(false);
            previousOid = await DatabaseOidAsync(session, request.DatabaseName, token).ConfigureAwait(false);
            var sql = inspection.EngineDetails![PostgreSqlArchive.DatabaseSqlKey].Split('\n');
            Report(RestoreExecutionStage.PreparingTarget);
            creationAttempted = true;
            using (var creation = new CancellationTokenSource(MaintenanceTimeout))
            {
                await session.ExecuteAsync(PostgreSqlArchive.RewriteDatabaseStatement(sql[0], inspection.Header.DatabaseName, scratch), MaintenanceTimeout, creation.Token).ConfigureAwait(false);
                scratchOid = await DatabaseOidAsync(session, scratch, creation.Token).ConfigureAwait(false);
                await MarkAsync(session, scratch, "scratch", request.DatabaseName, scratch, creation.Token).ConfigureAwait(false);
            }
            stage = RestoreExecutionStage.Restoring;
            restoreStarted = provider.Clock.GetUtcNow();
            Report(stage);
            await RunPgRestoreAsync(tool, target, scratch, path, ["--exit-on-error"], request.RestoreTimeout, token).ConfigureAwait(false);
            restoreFinished = provider.Clock.GetUtcNow();
            // pg_restore without --create deliberately targets scratch. Apply the archived database owner, ACL and settings explicitly.
            foreach (var statement in sql.Skip(1).Where(statement => !statement.StartsWith("COMMENT ON DATABASE ", StringComparison.Ordinal)))
                await session.ExecuteAsync(PostgreSqlArchive.RewriteDatabaseStatement(statement, inspection.Header.DatabaseName, scratch), MaintenanceTimeout, token).ConfigureAwait(false);
            await session.ExecuteAsync($"ALTER DATABASE {Q(scratch)} ALLOW_CONNECTIONS false", MaintenanceTimeout, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            stage = RestoreExecutionStage.Finalizing;
            Report(stage);
            publicationAttempted = true;
            // Publication is a bounded, non-cancellable transaction. A lost acknowledgement is resolved by database OID, never by deleting either name.
            using var publication = new CancellationTokenSource(MaintenanceTimeout);
            var commands = new List<string> { "BEGIN", "SET LOCAL lock_timeout = '10s'" };
            if (previousOid is not null)
            {
                commands.Add($"ALTER DATABASE {Q(request.DatabaseName)} ALLOW_CONNECTIONS false");
                if (request.TerminateActiveConnections)
                    commands.Add($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = {Literal(request.DatabaseName)} AND pid <> pg_backend_pid()");
                commands.Add($"ALTER DATABASE {Q(request.DatabaseName)} RENAME TO {Q(retired)}");
                commands.Add($"COMMENT ON DATABASE {Q(retired)} IS {Literal(Marker("previous", request.DatabaseName, scratch))}");
            }
            commands.Add($"ALTER DATABASE {Q(scratch)} RENAME TO {Q(request.DatabaseName)}");
            commands.Add($"ALTER DATABASE {Q(request.DatabaseName)} ALLOW_CONNECTIONS true");
            commands.Add($"COMMENT ON DATABASE {Q(request.DatabaseName)} IS NULL");
            foreach (var comment in sql.Skip(1).Where(statement => statement.StartsWith("COMMENT ON DATABASE ", StringComparison.Ordinal)))
                commands.Add(PostgreSqlArchive.RewriteDatabaseStatement(comment, inspection.Header.DatabaseName, request.DatabaseName));
            commands.Add("COMMIT");
            await session.ExecuteAsync(string.Join(';', commands), MaintenanceTimeout, publication.Token).ConfigureAwait(false);
            published = true;
        }
        catch (OperationCanceledException)
        {
            failure = cancellationToken.IsCancellationRequested
                ? new RestoreFailure(RestoreFailureKind.Cancelled, stage, "cancelled", "The restore was cancelled before publication.")
                : new RestoreFailure(RestoreFailureKind.SqlRestoreFailed, stage, PostgreSqlReasons.RestoreTimeout, "The restore exceeded its time limit.", BackupFailureCause.Timeout);
        }
        catch (Exception exception) when (PostgreSqlRestoreProvider.IsExpected(exception))
        {
            failure = PostgreSqlRestoreProvider.Failure(exception, stage == RestoreExecutionStage.PreparingTarget ? RestoreFailureKind.PreCheckFailed : RestoreFailureKind.SqlRestoreFailed, stage);
        }
        finally
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }

        if (!published && publicationAttempted && target is not null)
        {
            using var recovery = new CancellationTokenSource(MaintenanceTimeout);
            try
            {
                await using var check = await PostgreSqlSession.OpenAsync(target, "postgres", provider.Logger, recovery.Token).ConfigureAwait(false);
                var actual = await DatabaseOidAsync(check, request.DatabaseName, recovery.Token).ConfigureAwait(false);
                if (actual == scratchOid && scratchOid is not null) { published = true; failure = null; }
                else if (actual != previousOid) targetState = DatabaseRestoreTargetState.Unknown;
            }
            catch (Exception exception) when (PostgreSqlRestoreProvider.IsExpected(exception) || exception is OperationCanceledException)
            {
                targetState = DatabaseRestoreTargetState.Unknown;
            }
            if (!published) warnings.Add(new BackupWarning(PostgreSqlWarningCodes.RestoredDataRetained,
                "Publication was interrupted. Recovery databases were retained; inspect their operation comments and database identities before taking manual action."));
        }
        else if (!published && scratchOid is not null && target is not null)
        {
            using var cleanup = new CancellationTokenSource(MaintenanceTimeout);
            try
            {
                await using var clean = await PostgreSqlSession.OpenAsync(target, "postgres", provider.Logger, cleanup.Token).ConfigureAwait(false);
                // Only this attempt's known OID may be removed; never reap a database discovered after a restart.
                if (await DatabaseOidAsync(clean, scratch, cleanup.Token).ConfigureAwait(false) == scratchOid)
                {
                    await clean.ExecuteAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND datid::bigint = @oid",
                        MaintenanceTimeout, cleanup.Token, ("name", scratch), ("oid", (long)scratchOid.Value)).ConfigureAwait(false);
                    await clean.ExecuteAsync($"DROP DATABASE {Q(scratch)}", MaintenanceTimeout, cleanup.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (PostgreSqlRestoreProvider.IsExpected(exception) || exception is OperationCanceledException)
            {
                warnings.Add(new BackupWarning(PostgreSqlWarningCodes.TargetCleanupFailed,
                    "The failed attempt's identifiable scratch database could not be removed. An administrator must inspect it before manual cleanup."));
            }
        }
        else if (!published && creationAttempted)
        {
            warnings.Add(new BackupWarning(PostgreSqlWarningCodes.TargetCleanupFailed,
                "Scratch database creation had an uncertain outcome. Identifiable bp_restore_ databases are retained for administrator inspection, never blindly deleted."));
        }
        if (published)
        {
            if (previousOid is not null)
                warnings.Add(new BackupWarning(PostgreSqlWarningCodes.RestoredDataRetained,
                    "The previous database is retained under a bp_old_ recovery name with connections disabled. Inspect its operation comment and validate the published restore before manual removal."));
            if (previousOid is not null && request.TerminateActiveConnections)
                warnings.Add(new BackupWarning(PostgreSqlWarningCodes.ConnectionsTerminated, "Existing target connections were terminated as explicitly permitted."));
            Report(RestoreExecutionStage.Completed);
            return new DatabaseRestoreResult(DatabaseRestoreStatus.Succeeded, null, DatabaseRestoreTargetState.Online, "ONLINE", started,
                provider.Clock.GetUtcNow(), restoreStarted, restoreFinished, [], warnings, previousOid is not null ? retired : null);
        }
        failure ??= new RestoreFailure(RestoreFailureKind.SqlRestoreFailed, stage, PostgreSqlReasons.RestoreFailed, "The restore did not complete.");
        return new DatabaseRestoreResult(failure.Kind == RestoreFailureKind.Cancelled ? DatabaseRestoreStatus.Cancelled : DatabaseRestoreStatus.Failed,
            failure, targetState, previousOid is not null && targetState == DatabaseRestoreTargetState.NotModified ? "UNCHANGED" : null,
            started, provider.Clock.GetUtcNow(), restoreStarted, restoreFinished, [], warnings);

        void Report(RestoreExecutionStage current)
        {
            try { progress?.Report(new RestoreProgress(current, current == RestoreExecutionStage.Completed ? 100 : null, null, null, provider.Clock.GetUtcNow())); }
            catch { /* Reporting cannot change data safety or the execution outcome. */ }
        }
    }

    /// <summary>
    /// Restores into a disposable <c>bp_verify_*</c> database with the least privilege that works: the database is created directly under its final name
    /// (nothing is scratched, renamed or published), and <c>--no-owner --no-acl</c> make every object belong to the restoring login, so CREATEDB suffices.
    /// A failure leaves the database in place for the caller to drop; the result says whether it may exist.
    /// </summary>
    private static async Task<DatabaseRestoreResult> ExecuteVerificationAsync(PostgreSqlRestoreProvider provider, DatabaseConnectionSettings connection,
        DatabaseCredential credential, DatabaseRestoreRequest request, IProgress<RestoreProgress>? progress, CancellationToken cancellationToken)
    {
        var started = provider.Clock.GetUtcNow();
        DateTimeOffset? restoreStarted = null, restoreFinished = null;
        PostgreSqlConnectionTarget? target = null;
        PostgreSqlSession? session = null;
        var creationAttempted = false;
        var created = false;
        var stage = RestoreExecutionStage.PreparingTarget;
        RestoreFailure? failure = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.RestoreTimeout);
        try
        {
            var token = deadline.Token;
            token.ThrowIfCancellationRequested();
            PostgreSqlRestoreProvider.ValidateName(request.DatabaseName, request.Mode);
            var path = PostgreSqlRestoreProvider.AgentFile(request.AgentFile);
            var tool = await provider.RestoreToolAsync(token).ConfigureAwait(false);
            var inspection = await PostgreSqlArchive.ReadAsync(path, tool, true, request.RestoreTimeout, provider.Clock, token).ConfigureAwait(false);
            target = PostgreSqlConnectionTarget.Create(connection, credential);
            session = await PostgreSqlSession.OpenAsync(target, "postgres", provider.Logger, token).ConfigureAwait(false);
            await session.ExecuteAsync("SET standard_conforming_strings = on; SET statement_timeout = '30s'", MaintenanceTimeout, token).ConfigureAwait(false);
            // Same key as a normal restore to this name: two operations never create or fill one database at once.
            await session.ExecuteAsync("SELECT pg_advisory_lock(hashtextextended(@name, 710842))", MaintenanceTimeout, token, ("name", request.DatabaseName)).ConfigureAwait(false);
            var items = new List<BackupPreCheckItem>();
            await PostgreSqlRestoreProvider.CheckTargetAsync(session, request.DatabaseName, request.Mode, false, items, token).ConfigureAwait(false);
            await PostgreSqlRestoreProvider.CheckArchiveAsync(session, inspection, tool, request.Mode, items, token).ConfigureAwait(false);
            var create = PostgreSqlArchive.RewriteVerificationCreateStatement(inspection.EngineDetails![PostgreSqlArchive.DatabaseSqlKey].Split('\n')[0],
                inspection.Header.DatabaseName, request.DatabaseName);
            Report(RestoreExecutionStage.PreparingTarget);
            creationAttempted = true;
            using (var creation = new CancellationTokenSource(MaintenanceTimeout))
            {
                await session.ExecuteAsync(create, MaintenanceTimeout, creation.Token).ConfigureAwait(false);
                created = true;
                await MarkAsync(session, request.DatabaseName, "verification", request.DatabaseName, request.DatabaseName, creation.Token).ConfigureAwait(false);
            }
            stage = RestoreExecutionStage.Restoring;
            restoreStarted = provider.Clock.GetUtcNow();
            Report(stage);
            // Tablespaces are a placement detail of the source server; a CREATEDB-only login may not use them.
            await RunPgRestoreAsync(tool, target, request.DatabaseName, path, ["--exit-on-error", "--no-owner", "--no-acl", "--no-tablespaces"], request.RestoreTimeout, token).ConfigureAwait(false);
            restoreFinished = provider.Clock.GetUtcNow();
        }
        catch (OperationCanceledException)
        {
            failure = cancellationToken.IsCancellationRequested
                ? new RestoreFailure(RestoreFailureKind.Cancelled, stage, "cancelled", "The verification restore was cancelled.")
                : new RestoreFailure(RestoreFailureKind.SqlRestoreFailed, stage, PostgreSqlReasons.RestoreTimeout, "The restore exceeded its time limit.", BackupFailureCause.Timeout);
        }
        catch (Exception exception) when (PostgreSqlRestoreProvider.IsExpected(exception))
        {
            failure = PostgreSqlRestoreProvider.Failure(exception, stage == RestoreExecutionStage.PreparingTarget ? RestoreFailureKind.PreCheckFailed : RestoreFailureKind.SqlRestoreFailed, stage);
        }
        finally
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }

        if (failure is null)
        {
            Report(RestoreExecutionStage.Completed);
            return new DatabaseRestoreResult(DatabaseRestoreStatus.Succeeded, null, DatabaseRestoreTargetState.Online, "ONLINE", started,
                provider.Clock.GetUtcNow(), restoreStarted, restoreFinished, [], []);
        }

        var targetState = created ? DatabaseRestoreTargetState.PartiallyRestored
            : !creationAttempted ? DatabaseRestoreTargetState.NotModified
            : await ProbeAfterUncertainCreateAsync(provider, target, request.DatabaseName).ConfigureAwait(false);
        return new DatabaseRestoreResult(failure.Kind == RestoreFailureKind.Cancelled ? DatabaseRestoreStatus.Cancelled : DatabaseRestoreStatus.Failed,
            failure, targetState, null, started, provider.Clock.GetUtcNow(), restoreStarted, restoreFinished, [], []);

        void Report(RestoreExecutionStage current)
        {
            try { progress?.Report(new RestoreProgress(current, current == RestoreExecutionStage.Completed ? 100 : null, null, null, provider.Clock.GetUtcNow())); }
            catch { /* Reporting cannot change data safety or the execution outcome. */ }
        }
    }

    /// <summary>CREATE DATABASE was sent but never acknowledged: look, with a fresh connection, instead of guessing. Not reachable means unknown.</summary>
    private static async Task<DatabaseRestoreTargetState> ProbeAfterUncertainCreateAsync(PostgreSqlRestoreProvider provider, PostgreSqlConnectionTarget? target, string name)
    {
        if (target is null) return DatabaseRestoreTargetState.Unknown;
        using var probe = new CancellationTokenSource(MaintenanceTimeout);
        try
        {
            await using var check = await PostgreSqlSession.OpenAsync(target, "postgres", provider.Logger, probe.Token).ConfigureAwait(false);
            return await DatabaseOidAsync(check, name, probe.Token).ConfigureAwait(false) is null ? DatabaseRestoreTargetState.NotModified : DatabaseRestoreTargetState.PartiallyRestored;
        }
        catch (Exception exception) when (PostgreSqlRestoreProvider.IsExpected(exception) || exception is OperationCanceledException)
        {
            return DatabaseRestoreTargetState.Unknown;
        }
    }

    private static async Task RunPgRestoreAsync(PostgreSqlToolInfo tool, PostgreSqlConnectionTarget target, string database, string path, string[] options,
        TimeSpan timeout, CancellationToken token)
    {
        var result = await PostgreSqlProcessRunner.RunAsync(new PostgreSqlProcessSpec(tool.Path, [.. options, "--dbname", target.ToConninfo(database), "--", path],
            new Dictionary<string, string> { ["PGPASSWORD"] = target.Password, ["LC_ALL"] = "C", ["PGCLIENTENCODING"] = "UTF8" }, timeout), null, null, token).ConfigureAwait(false);
        if (result.TimedOut) throw PostgreSqlArchive.Failure(PostgreSqlReasons.RestoreTimeout, "The restore exceeded its time limit.", BackupFailureCause.Timeout);
        if (result.ExitCode != 0)
        {
            var classified = PostgreSqlToolFailureClassifier.Classify(PostgreSqlToolKind.Restore, result.ErrorTail);
            throw PostgreSqlArchive.Failure(classified.ReasonCode, classified.Message, classified.Cause);
        }
    }

    private static async Task<uint?> DatabaseOidAsync(PostgreSqlSession session, string name, CancellationToken token)
    {
        var value = await session.ScalarAsync("SELECT oid FROM pg_database WHERE datname = @name", token, ("name", name)).ConfigureAwait(false);
        return value is null ? null : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
    }
    private static Task MarkAsync(PostgreSqlSession session, string database, string phase, string target, string operation, CancellationToken token) =>
        session.ExecuteAsync($"COMMENT ON DATABASE {Q(database)} IS {Literal(Marker(phase, target, operation))}", MaintenanceTimeout, token);
    private static string Marker(string phase, string target, string operation) => JsonSerializer.Serialize(new { provider = "backup-platform", phase, target, operation });
    private static string Q(string name) => PostgreSqlIdentifiers.Quote(name);
    private static string Literal(string text) => "E'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal) + "'";
}
