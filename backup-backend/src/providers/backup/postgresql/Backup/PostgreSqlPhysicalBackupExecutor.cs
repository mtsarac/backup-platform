using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql.Connectivity;
using BackupPlatform.BackupProviders.PostgreSql.Discovery;
using BackupPlatform.BackupProviders.PostgreSql.Tools;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.PostgreSql.Backup;

internal sealed class PostgreSqlPhysicalBackupExecutor(PostgreSqlBackupProvider provider, PostgreSqlClientTools tools, ILogger logger, TimeProvider time, PostgreSqlBackupMode mode)
{
    public async Task<BackupPreCheckReport> PreCheckAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, FullBackupPreCheckRequest request, CancellationToken token)
    {
        var items = new List<BackupPreCheckItem>();
        var backup = await tools.ResolveAsync(PostgreSqlClientTools.PgBaseBackup, token).ConfigureAwait(false);
        var verify = await tools.ResolveAsync(PostgreSqlClientTools.PgVerifyBackup, token).ConfigureAwait(false);
        if (backup.Tool is null || verify.Tool is null)
        {
            items.Add(PostgreSqlFailures.Failed("postgres.physical.tools", "postgres.physical.tools_missing", "Physical backups require working pg_basebackup and pg_verifybackup client tools.", BackupFailureCause.Unsupported));
            return new BackupPreCheckReport(items);
        }
        try
        {
            var target = PostgreSqlConnectionTarget.Create(connection, credential);
            await using var session = await PostgreSqlSession.OpenAsync(target, request.DatabaseName, logger, token).ConfigureAwait(false);
            var server = await PostgreSqlDiscovery.ReadServerAsync(session, token).ConfigureAwait(false);
            items.Add(server.Major >= 13 && backup.Tool.Major == server.Major && verify.Tool.Major == server.Major
                ? PostgreSqlFailures.Passed("postgres.physical.version", "Physical tools match the PostgreSQL server major version.")
                : PostgreSqlFailures.Failed("postgres.physical.version", "postgres.physical.version_unsupported", "Manifest-verified physical backup requires PostgreSQL 13+ and matching-major pg_basebackup and pg_verifybackup.", BackupFailureCause.Unsupported));
            var allowed = await session.ScalarAsync("SELECT rolsuper OR rolreplication FROM pg_roles WHERE rolname = current_user", token).ConfigureAwait(false);
            items.Add(allowed is true
                ? PostgreSqlFailures.Passed(BackupPreCheckIds.BackupPermission, "The login has physical replication permission; pg_hba replication access is checked by pg_basebackup.")
                : PostgreSqlFailures.Failed(BackupPreCheckIds.BackupPermission, "postgres.physical.replication_required", "Physical cluster backup requires a replication-enabled login.", BackupFailureCause.PermissionDenied));
            var senders = await session.ScalarAsync("SELECT current_setting('max_wal_senders')::int", token).ConfigureAwait(false);
            if (senders is not int count || count < (mode == PostgreSqlBackupMode.Streaming ? 2 : 1))
                items.Add(PostgreSqlFailures.Failed("postgres.physical.wal", "postgres.physical.wal_senders", "Configure sufficient WAL senders: streaming base backups need two replication connections.", BackupFailureCause.InvalidConfiguration));
            foreach (var capability in request.RequestedCapabilities)
                if (capability != PostgreSqlCapabilityIds.BackupChecksum || !server.DataChecksums)
                    items.Add(PostgreSqlFailures.Failed(BackupPreCheckIds.RequestedCapability(capability), PostgreSqlReasons.RequiredOptionUnsupported, "The physical source does not provide the required capability.", BackupFailureCause.Unsupported));
            if (request.Staging is { } staging) PostgreSqlBackupChecks.CheckStaging(items, staging, null);
            items.Add(new BackupPreCheckItem("postgres.physical.scope", BackupPreCheckStatus.Warning, "postgres.physical.cluster_scope", "This backup contains the whole PostgreSQL cluster, including all databases and roles. Recovery is offline cluster recovery, not the logical database restore API."));
        }
        catch (DatabaseProviderException exception)
        {
            var failure = PostgreSqlFailures.FromConnection(exception);
            items.Add(PostgreSqlFailures.Failed(BackupPreCheckIds.ServerReachable, failure.ReasonCode, failure.Message, failure.Cause));
        }
        return new BackupPreCheckReport(items);
    }

    public async Task<FullBackupResult> ExecuteAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, FullBackupRequest request, IProgress<BackupProgress>? progress, CancellationToken token)
    {
        var started = time.GetUtcNow();
        var stage = BackupExecutionStage.Preparing;
        var staging = new AgentStagingDirectory(request.Staging.AgentDirectory);
        var work = Path.Combine(request.Staging.AgentDirectory.Value, ".physical-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(work, "data");
        var output = staging.FilePath(request.StagedFileName);
        var ownedOutput = false;
        var ownedWork = false;
        DatabaseBackupSource? source = null;
        BackupOptionsOutcome? outcome = null;
        StagedBackupFile? staged = null;
        BackupFailure? failure = null;
        var verification = BackupVerification.NotRun;
        var warnings = new List<BackupWarning>();
        DateTimeOffset? backupStarted = null, backupFinished = null;
        var cleanup = StagingCleanup.NotNeeded;
        try
        {
            var required = request.Options.Checksum == BackupOptionMode.Required ? new[] { PostgreSqlCapabilityIds.BackupChecksum } : [];
            var checks = await provider.PreCheckFullBackupAsync(connection, credential, new FullBackupPreCheckRequest(request.DatabaseName, required, request.Staging), token).ConfigureAwait(false);
            if (!checks.CanProceed)
            {
                var item = checks.Items.First(item => item.Status == BackupPreCheckStatus.Failed);
                throw new Stopped(new BackupFailure(BackupFailureKind.PreCheckFailed, stage, item.ReasonCode, item.Message, item.Cause));
            }
            warnings.AddRange(checks.Items.Where(item => item.Status == BackupPreCheckStatus.Warning).Select(item => new BackupWarning(item.ReasonCode, item.Message)));
            if (staging.EntryExists(request.StagedFileName)) throw new IOException("The staged destination exists.");
            var target = PostgreSqlConnectionTarget.Create(connection, credential);
            PostgreSqlServerInfo server;
            IReadOnlyList<object?[]> tablespaces;
            await using (var session = await PostgreSqlSession.OpenAsync(target, request.DatabaseName, logger, token).ConfigureAwait(false))
            {
                server = await PostgreSqlDiscovery.ReadServerAsync(session, token).ConfigureAwait(false);
                tablespaces = await session.QueryAsync("SELECT oid::text, pg_tablespace_location(oid) FROM pg_tablespace WHERE pg_tablespace_location(oid) <> '' ORDER BY oid", token).ConfigureAwait(false);
            }
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(work);
            else Directory.CreateDirectory(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ownedWork = true;
            var arguments = new List<string> { "--pgdata=" + data, "--format=plain", "--wal-method=" + (mode == PostgreSqlBackupMode.Streaming ? "stream" : "fetch"), "--checkpoint=fast", "--manifest-checksums=SHA256", "--no-password", "--dbname=" + target.ToConninfo(request.DatabaseName) };
            if (request.Options.Checksum == BackupOptionMode.Off) arguments.Add("--no-verify-checksums");
            foreach (var row in tablespaces)
            {
                var id = (string)row[0]!;
                if (!uint.TryParse(id, CultureInfo.InvariantCulture, out _)) throw new IOException("Invalid tablespace identity.");
                var location = (string)row[1]!;
                var mapped = Path.Combine(work, "tablespaces", id);
                Directory.CreateDirectory(Path.GetDirectoryName(mapped)!);
                arguments.Add("--tablespace-mapping=" + Escape(location) + "=" + Escape(mapped));
            }
            var backup = (await tools.ResolveAsync(PostgreSqlClientTools.PgBaseBackup, token).ConfigureAwait(false)).Tool!;
            var verify = (await tools.ResolveAsync(PostgreSqlClientTools.PgVerifyBackup, token).ConfigureAwait(false)).Tool!;
            stage = BackupExecutionStage.BackingUp;
            Report();
            backupStarted = time.GetUtcNow();
            var result = await PostgreSqlProcessRunner.RunAsync(new PostgreSqlProcessSpec(backup.Path, arguments, new Dictionary<string, string> { ["PGPASSWORD"] = target.Password }, request.BackupTimeout), null, null, token).ConfigureAwait(false);
            Check(result, BackupFailureKind.SqlBackupFailed, "postgres.physical.backup_failed");
            backupFinished = time.GetUtcNow();
            stage = BackupExecutionStage.Verifying;
            Report();
            var verifyStarted = time.GetUtcNow();
            var verified = await PostgreSqlProcessRunner.RunAsync(new PostgreSqlProcessSpec(verify.Path, [data], new Dictionary<string, string>(), request.VerifyTimeout), null, null, token).ConfigureAwait(false);
            verification = new BackupVerification(verified.ExitCode == 0 && !verified.TimedOut ? BackupVerificationStatus.Passed : BackupVerificationStatus.Failed, "pg_verifybackup-manifest-sha256-and-wal", verifyStarted, time.GetUtcNow());
            Check(verified, BackupFailureKind.VerificationFailed, "postgres.physical.verify_failed");
            foreach (var row in tablespaces)
            {
                var link = Path.Combine(data, "pg_tblspc", (string)row[0]!);
                if (new DirectoryInfo(link).LinkTarget is null) throw new IOException("The physical tablespace link is missing.");
                Directory.Delete(link);
                Directory.CreateSymbolicLink(link, Path.Combine("..", "..", "tablespaces", (string)row[0]!));
            }
            var compression = request.Options.Compression != BackupOptionMode.Off;
            outcome = new BackupOptionsOutcome(request.Options, new AppliedFullBackupOptions(true, server.DataChecksums && request.Options.Checksum != BackupOptionMode.Off, compression));
            source = new DatabaseBackupSource(PostgreSqlBackupProvider.Instance(connection, server), request.DatabaseName,
                new Dictionary<string, string> { ["archive-format"] = compression ? "physical-tar-gzip" : "physical-tar", ["backup-scope"] = "cluster", ["wal-method"] = mode == PostgreSqlBackupMode.Streaming ? "stream" : "fetch", ["restore-method"] = "offline-cluster", ["manifest-checksum"] = "SHA256", ["client-version"] = backup.VersionText });
            await File.WriteAllTextAsync(Path.Combine(work, "backup-platform.json"), JsonSerializer.Serialize(new { format = "postgres-physical-v1", serverMajor = server.Major, walMethod = mode.ToString(), tablespaces = tablespaces.Select(row => (string)row[0]!).ToArray() }), token).ConfigureAwait(false);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous | FileOptions.SequentialScan };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(output, options))
            {
                ownedOutput = true;
                if (compression)
                {
                    await using var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true);
                    await TarFile.CreateFromDirectoryAsync(work, gzip, includeBaseDirectory: false, token).ConfigureAwait(false);
                }
                else await TarFile.CreateFromDirectoryAsync(work, file, includeBaseDirectory: false, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
                file.Flush(true);
            }
            var length = new FileInfo(output).Length;
            if (length == 0) throw new IOException("The archive is empty.");
            staged = new StagedBackupFile(new DatabaseServerPath(output), new AgentPath(output), request.StagedFileName, length);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { failure = new BackupFailure(BackupFailureKind.Cancelled, stage, "cancelled", "The physical backup was cancelled."); }
        catch (Stopped exception) { failure = exception.Failure; }
        catch (DatabaseProviderException exception)
        {
            var classified = PostgreSqlFailures.FromConnection(exception);
            failure = new BackupFailure(BackupFailureKind.SqlBackupFailed, stage, classified.ReasonCode, classified.Message, classified.Cause);
        }
        catch (Exception exception)
        {
            logger.LogWarning("PostgreSQL physical backup stopped: Stage={Stage} ExceptionType={ExceptionType}", stage, exception.GetType().Name);
            failure = new BackupFailure(stage == BackupExecutionStage.Verifying ? BackupFailureKind.VerificationFailed : BackupFailureKind.SqlBackupFailed, stage, "postgres.physical.operation_failed", "The physical backup could not complete. Check replication access, client tools and staging space.");
        }
        finally
        {
            if (ownedWork)
            {
                try { Directory.Delete(work, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    warnings.Add(new BackupWarning(PostgreSqlWarningCodes.StagingCleanupFailed, "The private physical backup work directory could not be removed."));
                }
            }
        }
        if (failure is not null && ownedOutput)
            cleanup = new StagingCleanup(staging.TryDeleteFile(request.StagedFileName) ? StagingCleanupStatus.Deleted : StagingCleanupStatus.Failed);
        return new FullBackupResult(failure is null ? FullBackupStatus.Succeeded : failure.Kind == BackupFailureKind.Cancelled ? FullBackupStatus.Cancelled : FullBackupStatus.Failed,
            failure, source, outcome, started, time.GetUtcNow(), backupStarted, backupFinished, failure is null ? staged : null, verification, warnings, cleanup);

        void Check(PostgreSqlProcessResult result, BackupFailureKind kind, string reason)
        {
            if (result.ExitCode == 0 && !result.TimedOut) return;
            throw new Stopped(new BackupFailure(kind, stage, result.TimedOut ? "postgres.physical.timeout" : reason,
                result.TimedOut ? "The physical PostgreSQL client operation timed out." : "The physical PostgreSQL client operation failed. No artifact was published.", result.TimedOut ? BackupFailureCause.Timeout : BackupFailureCause.Unclassified));
        }
        void Report() => progress?.Report(new BackupProgress(stage, null, null, null, time.GetUtcNow()));
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("=", "\\=", StringComparison.Ordinal);
    private sealed class Stopped(BackupFailure failure) : Exception { public BackupFailure Failure { get; } = failure; }
}
