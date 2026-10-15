using System.Globalization;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql.Connectivity;
using BackupPlatform.BackupProviders.PostgreSql.Tools;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.PostgreSql.Backup;

internal sealed class PostgreSqlFullBackupExecutor(PostgreSqlBackupProvider provider, PostgreSqlClientTools tools, ILogger logger, TimeProvider time)
{
    public async Task<FullBackupResult> ExecuteAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, FullBackupRequest request,
        IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        DateTimeOffset? backupStarted = null;
        DateTimeOffset? backupFinished = null;
        DatabaseBackupSource? source = null;
        BackupOptionsOutcome? options = null;
        var verification = BackupVerification.NotRun;
        var warnings = new List<BackupWarning>();
        var stage = BackupExecutionStage.Preparing;
        var staging = new AgentStagingDirectory(request.Staging.AgentDirectory);
        var owned = false;
        BackupFailure? failure = null;
        StagedBackupFile? stagedFile = null;
        Report(stage);
        try
        {
            var required = new List<DatabaseCapabilityId>();
            if (request.Options.Checksum == BackupOptionMode.Required) required.Add(PostgreSqlCapabilityIds.BackupChecksum);
            var checks = await provider.PreCheckFullBackupAsync(connection, credential,
                new FullBackupPreCheckRequest(request.DatabaseName, required, request.Staging), cancellationToken).ConfigureAwait(false);
            if (!checks.CanProceed)
            {
                var item = checks.Items.First(item => item.Status == BackupPreCheckStatus.Failed);
                throw Stop(BackupFailureKind.PreCheckFailed, item.ReasonCode, item.Message, item.Cause);
            }
            warnings.AddRange(checks.Items.Where(item => item.Status == BackupPreCheckStatus.Warning).Select(item => new BackupWarning(item.ReasonCode, item.Message)));
            var target = PostgreSqlConnectionTarget.Create(connection, credential);
            PostgreSqlBackupFacts facts;
            await using (var session = await PostgreSqlSession.OpenAsync(target, request.DatabaseName, logger, cancellationToken).ConfigureAwait(false))
                facts = await PostgreSqlBackupFacts.ReadAsync(session, request.DatabaseName, cancellationToken).ConfigureAwait(false);
            var dump = (await tools.ResolveAsync(PostgreSqlClientTools.PgDump, cancellationToken).ConfigureAwait(false)).Tool!;
            var restore = (await tools.ResolveAsync(PostgreSqlClientTools.PgRestore, cancellationToken).ConfigureAwait(false)).Tool!;
            var checksum = request.Options.Checksum != BackupOptionMode.Off && facts.Server.DataChecksums;
            var compression = request.Options.Compression != BackupOptionMode.Off;
            options = new BackupOptionsOutcome(request.Options, new AppliedFullBackupOptions(request.Options.CopyOnly != BackupOptionMode.Off, checksum, compression));
            if (request.Options.Checksum == BackupOptionMode.IfSupported && !checksum)
                warnings.Add(new BackupWarning(PostgreSqlWarningCodes.ChecksumUnavailable,
                    "Cluster page checksums are disabled. The custom archive is still fully read-verified and SHA-256 is always computed by the pipeline."));
            source = new DatabaseBackupSource(PostgreSqlBackupProvider.Instance(connection, facts.Server), facts.Database.Name,
                new Dictionary<string, string>
                {
                    ["archive-format"] = "custom", ["client-version"] = dump.VersionText,
                    ["encoding"] = facts.Database.Encoding, ["collation"] = facts.Database.Collation ?? string.Empty,
                    ["data-checksums"] = facts.Server.DataChecksums ? "on" : "off",
                    ["database-size-bytes"] = facts.Database.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                    ["extensions"] = string.Join(", ", facts.Extensions)
                });
            var path = staging.FilePath(request.StagedFileName);
            if (staging.EntryExists(request.StagedFileName))
                throw Stop(BackupFailureKind.PreCheckFailed, PostgreSqlReasons.StagedFileExists,
                    "An entry already exists with the staged archive name; it was not touched.", BackupFailureCause.StagingInvalid);
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 81920
            };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            // Stream stdout into an exclusively created file: pg_dump -f would silently truncate an existing path.
            await using (var file = new FileStream(path, fileOptions))
            {
                owned = true;
                cancellationToken.ThrowIfCancellationRequested();
                stage = BackupExecutionStage.BackingUp;
                Report(stage);
                backupStarted = time.GetUtcNow();
                var tables = 0;
                var result = await PostgreSqlProcessRunner.RunAsync(new PostgreSqlProcessSpec(dump.Path,
                    ["--format=custom", "--compress=" + (compression ? "6" : "0"), "--blobs", "--no-password", "--verbose",
                        "--lock-wait-timeout=" + ((long)connection.Options.CommandTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                        "--dbname=" + target.ToConninfo(request.DatabaseName)],
                    new Dictionary<string, string> { ["PGPASSWORD"] = target.Password }, request.BackupTimeout, Output: file),
                    line =>
                    {
                        if (line.Contains("dumping contents of table", StringComparison.Ordinal))
                        {
                            tables++;
                            Report(stage, facts.TableCount > 0 ? Math.Min(99, tables * 100.0 / facts.TableCount) : null, file.Length);
                        }
                    }, null, cancellationToken).ConfigureAwait(false);
                CheckResult(result, PostgreSqlToolKind.Dump, BackupFailureKind.SqlBackupFailed);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
                backupFinished = time.GetUtcNow();
                Report(stage, 100, file.Length);
            }
            cancellationToken.ThrowIfCancellationRequested();
            stage = BackupExecutionStage.Verifying;
            Report(stage);
            var verifyStarted = time.GetUtcNow();
            // --list alone only reads the TOC. Reading the full SQL stream forces every compressed data/large-object block to be decoded too.
            var verified = await PostgreSqlProcessRunner.RunAsync(new PostgreSqlProcessSpec(restore.Path,
                ["--exit-on-error", "--file=-", path], new Dictionary<string, string>(), request.VerifyTimeout), null, null, cancellationToken).ConfigureAwait(false);
            verification = new BackupVerification(verified.ExitCode == 0 && !verified.TimedOut ? BackupVerificationStatus.Passed : BackupVerificationStatus.Failed,
                "pg_restore-full-read", verifyStarted, time.GetUtcNow());
            CheckResult(verified, PostgreSqlToolKind.Verify, BackupFailureKind.VerificationFailed);
            var size = staging.FileLength(request.StagedFileName);
            if (size is not > 0) throw Stop(BackupFailureKind.VerificationFailed, PostgreSqlReasons.StagedFileMissing, "The staged archive is missing or empty.", BackupFailureCause.StagingInvalid);
            stagedFile = new StagedBackupFile(new DatabaseServerPath(path), new AgentPath(path), request.StagedFileName, size.Value);
            Report(stage, 100, size);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = new BackupFailure(BackupFailureKind.Cancelled, stage, "cancelled", "The backup was cancelled.");
        }
        catch (BackupStopped exception) { failure = exception.Failure; }
        catch (DatabaseProviderException exception)
        {
            var classified = PostgreSqlFailures.FromConnection(exception);
            failure = new BackupFailure(BackupFailureKind.SqlBackupFailed, stage, classified.ReasonCode, classified.Message, classified.Cause);
        }
        catch (Exception exception)
        {
            logger.LogWarning("PostgreSQL backup stopped: Stage={Stage} ExceptionType={ExceptionType}", stage, exception.GetType().Name);
            var stagingError = exception is IOException or UnauthorizedAccessException;
            failure = new BackupFailure(stage == BackupExecutionStage.Verifying ? BackupFailureKind.VerificationFailed : BackupFailureKind.SqlBackupFailed,
                stage, stagingError ? PostgreSqlReasons.StagingWriteFailed : PostgreSqlReasons.UnexpectedError,
                stagingError ? "The agent could not create or write the staged archive; any existing file was not touched."
                    : "The PostgreSQL backup could not complete. Check the agent configuration and client tools.",
                stagingError ? BackupFailureCause.StagingInvalid : BackupFailureCause.Unclassified);
        }
        var cleanup = StagingCleanup.NotNeeded;
        if (failure is not null && owned)
        {
            var keep = stage == BackupExecutionStage.Verifying && request.OnFailure == StagingFailurePolicy.Keep && failure.Kind != BackupFailureKind.Cancelled;
            cleanup = new StagingCleanup(keep ? StagingCleanupStatus.Kept : staging.TryDeleteFile(request.StagedFileName) ? StagingCleanupStatus.Deleted : StagingCleanupStatus.Failed);
            if (cleanup.Status == StagingCleanupStatus.Failed)
                warnings.Add(new BackupWarning(PostgreSqlWarningCodes.StagingCleanupFailed, "The partial staged archive could not be removed."));
        }
        return new FullBackupResult(failure is null ? FullBackupStatus.Succeeded : failure.Kind == BackupFailureKind.Cancelled ? FullBackupStatus.Cancelled : FullBackupStatus.Failed,
            failure, source, options, started, time.GetUtcNow(), backupStarted, backupFinished, failure is null ? stagedFile : null, verification, warnings, cleanup);

        BackupStopped Stop(BackupFailureKind kind, string reason, string message, BackupFailureCause cause) => new(new BackupFailure(kind, stage, reason, message, cause));
        void CheckResult(PostgreSqlProcessResult result, PostgreSqlToolKind kind, BackupFailureKind failureKind)
        {
            if (result.ExitCode == 0 && !result.TimedOut) return;
            if (result.TimedOut) throw Stop(failureKind, kind == PostgreSqlToolKind.Dump ? PostgreSqlReasons.DumpTimeout : PostgreSqlReasons.VerifyTimeout,
                "The PostgreSQL client operation exceeded its time limit.", BackupFailureCause.Timeout);
            var classified = PostgreSqlToolFailureClassifier.Classify(kind, result.ErrorTail);
            logger.LogWarning("PostgreSQL client failed: Tool={Tool} ExitCode={ExitCode} Reason={Reason}", kind, result.ExitCode, classified.ReasonCode);
            throw Stop(failureKind, classified.ReasonCode, classified.Message, classified.Cause);
        }
        void Report(BackupExecutionStage current, double? percent = null, long? bytes = null)
        {
            try { progress?.Report(new BackupProgress(current, percent, bytes, null, time.GetUtcNow())); }
            catch (Exception) { /* Progress cannot change the backup outcome. */ }
        }
    }

    private sealed class BackupStopped(BackupFailure failure) : Exception(failure.Message)
    {
        public BackupFailure Failure { get; } = failure;
    }
}
