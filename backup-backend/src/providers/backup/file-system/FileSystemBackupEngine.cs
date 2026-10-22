using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.SnapshotProviders.Abstractions;

namespace BackupPlatform.BackupProviders.FileSystem;

public sealed record FileSystemRoot(string RootDirectory, SnapshotKind? SnapshotKind = null, string? SnapshotSource = null,
    string SnapshotRelativePath = ".", string? LvmSize = null);

/// <summary>Runs configured file-source references through the existing artifact, encryption, replica and restore lifecycle.</summary>
public sealed class FileSystemBackupEngine(IReadOnlyDictionary<string, FileSystemRoot> roots, FileSystemBackupProvider archives, TimeProvider time)
    : IDatabaseBackupProvider, IDatabaseRestoreProvider
{
    public DatabaseEngineId Engine { get; } = new("file-system");
    private DatabaseInstanceInfo Instance => new(Engine, Environment.MachineName, null, "1", "File archive", OperatingSystem.IsWindows() ? "Windows" : "Linux");
    private static readonly DatabaseCapabilitySet Capabilities = new(new[] { "file-system.backup.full", "file-system.restore.full", "file-system.restore.precheck", "file-system.restore.discovery" }
        .Select(id => new DatabaseCapability(new(id), DatabaseCapabilitySupport.Supported, "supported", "Configured file-source archives are supported.")));

    public Task<DatabaseInstanceDiscovery> DiscoverAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, CancellationToken cancellationToken)
    {
        RequireLocal(connection, credential);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DatabaseInstanceDiscovery(Instance, Capabilities, roots.Select(pair => new DatabaseInfo(pair.Key, false,
            Directory.Exists(pair.Value.RootDirectory) ? DatabaseAvailability.Online : DatabaseAvailability.Unavailable, Capabilities)).ToArray()));
    }

    public Task<BackupPreCheckReport> PreCheckFullBackupAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, FullBackupPreCheckRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var report = CheckSource(connection, credential, request.DatabaseName);
        var items = report.Items.ToList();
        if (request.Staging is not { Kind: StagingMappingKind.SameHost } staging || !new AgentStagingDirectory(staging.AgentDirectory).Inspect().CanWrite)
            items.Add(new("file-system.staging", BackupPreCheckStatus.Failed, "staging-invalid", "File backups require writable same-host staging.", BackupFailureCause.StagingInvalid));
        foreach (var capability in request.RequestedCapabilities)
            if (Capabilities.SupportOf(capability) != DatabaseCapabilitySupport.Supported)
                items.Add(new(BackupPreCheckIds.RequestedCapability(capability), BackupPreCheckStatus.Failed, "unsupported", "The requested file-backup capability is unavailable.", BackupFailureCause.Unsupported));
        return Task.FromResult(new BackupPreCheckReport(items));
    }

    public async Task<FullBackupResult> ExecuteFullBackupAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, FullBackupRequest request,
        IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        string? archivePath = null;
        try
        {
            RequireLocal(connection, credential);
            if (request.Staging.Kind != StagingMappingKind.SameHost || !roots.TryGetValue(request.DatabaseName, out var root)) throw new ArgumentException("Unknown file source or staging mapping.");
            var selection = request.Options.Files ?? throw new ArgumentException("File selection is required.");
            if (!Enum.TryParse<FileSymlinkPolicy>(selection.SymlinkPolicy, out var links) || !Enum.IsDefined(links)) throw new ArgumentException("Invalid symbolic link policy.");
            var source = new FileSystemBackupSource(root.RootDirectory, selection.Includes, selection.Excludes, links, selection.Paths,
                root.SnapshotKind, root.SnapshotSource, root.SnapshotRelativePath, root.LvmSize, request.DatabaseName, request.Options.Compression != BackupOptionMode.Off);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(request.BackupTimeout);
            var (manifest, temporary) = await archives.CreateBackupAsync(source, request.Staging.AgentDirectory.Value,
                new InlineProgress<long>(bytes => progress?.Report(new(BackupExecutionStage.BackingUp, null, bytes, null, time.GetUtcNow()))), deadline.Token).ConfigureAwait(false);
            archivePath = temporary;
            var finalPath = Path.Combine(request.Staging.AgentDirectory.Value, request.StagedFileName);
            File.Move(archivePath, finalPath);
            archivePath = finalPath;
            var backedUp = time.GetUtcNow();
            progress?.Report(new(BackupExecutionStage.Verifying, null, null, null, backedUp));
            deadline.CancelAfter(request.VerifyTimeout);
            await archives.InspectAsync(archivePath, deadline.Token).ConfigureAwait(false);
            var finished = time.GetUtcNow();
            var staged = new StagedBackupFile(request.Staging.ServerDirectory.Combine(request.StagedFileName), new(archivePath), request.StagedFileName, new FileInfo(archivePath).Length);
            return new(FullBackupStatus.Succeeded, null, new(Instance, request.DatabaseName, new Dictionary<string, string>
            {
                ["archive-format"] = "file-manifest-zip-v1", ["source-kind"] = "file-system", ["file-count"] = manifest.Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["symlink-policy"] = selection.SymlinkPolicy, ["locked-file-policy"] = "Fail", ["snapshot"] = root.SnapshotKind?.ToString() ?? "None"
            }), new(request.Options, new(true, true, source.Compress)), started, finished, started, backedUp, staged,
                new(BackupVerificationStatus.Passed, "manifest-sha256-readback", backedUp, finished),
                root.SnapshotKind is null ? [new("file-system.live-source", "Live files are checked for changes; select a configured snapshot source for a point-in-time view.")] : [], StagingCleanup.NotNeeded);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException or System.Text.Json.JsonException)
        {
            var cancelled = error is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var cleanup = Cleanup(archivePath, cancelled || request.OnFailure == StagingFailurePolicy.Delete);
            var failure = new BackupFailure(cancelled ? BackupFailureKind.Cancelled : BackupFailureKind.VerificationFailed,
                BackupExecutionStage.BackingUp, cancelled ? "cancelled" : "file-backup-failed", "The file backup did not complete; original source files were not changed.",
                error is ArgumentException ? BackupFailureCause.InvalidConfiguration : error is OperationCanceledException ? BackupFailureCause.Timeout : BackupFailureCause.StagingInvalid);
            return new(cancelled ? FullBackupStatus.Cancelled : FullBackupStatus.Failed, failure, null, null, started, time.GetUtcNow(), started, null, null, BackupVerification.NotRun, [], cleanup);
        }
    }

    public Task<BackupPreCheckReport> PreCheckRestoreAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, RestorePreCheckRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(request.Mode == DatabaseRestoreMode.IsolatedVerification
            ? Failed("unsupported", "File archives are verified by their manifest; database verification mode does not apply.") : CheckSource(connection, credential, request.DatabaseName));
    }

    public async Task<RestoreInspectionResult> InspectBackupAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, RestoreInspectionRequest request, CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        try
        {
            RequireLocal(connection, credential);
            var path = request.AgentFile?.Value ?? throw new ArgumentException("Agent staging is required.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(request.VerifyTimeout);
            var manifest = await archives.InspectAsync(path, deadline.Token, request.RunVerifyOnly).ConfigureAwait(false);
            var header = new RestoreBackupHeader(manifest.SourceReference, RestoreBackupKind.Database, null, null, null, null, manifest.CreatedAt.UtcDateTime,
                manifest.CreatedAt.UtcDateTime, 1, manifest.Files.Sum(entry => entry.SizeBytes), true, true, true, false, null);
            return new(new(header, [], request.RunVerifyOnly, request.RunVerifyOnly ? started : null, request.RunVerifyOnly ? time.GetUtcNow() : null,
                new Dictionary<string, string> { ["archive-format"] = "file-manifest-zip-v1", ["file-count"] = manifest.Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
            return new(null, new(RestoreFailureKind.BackupInvalid, RestoreExecutionStage.InspectingBackup, "file-archive-invalid", "The file archive or its manifest is invalid."));
        }
    }

    public Task<RestorePlanResult> PlanRestoreAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, RestorePlanRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var report = request.Mode == DatabaseRestoreMode.IsolatedVerification ? Failed("unsupported", "Database verification mode is unavailable for file archives.")
            : CheckSource(connection, credential, request.DatabaseName);
        return Task.FromResult(new RestorePlanResult(report, report.CanProceed ? new([], new(Instance, null, null, false, null)) : null));
    }

    public async Task<DatabaseRestoreResult> ExecuteRestoreAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, DatabaseRestoreRequest request,
        IProgress<RestoreProgress>? progress, CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        try
        {
            RequireLocal(connection, credential);
            if (request.Mode == DatabaseRestoreMode.IsolatedVerification || !roots.TryGetValue(request.DatabaseName, out var root)) throw new ArgumentException("Unknown restore source reference.");
            var path = request.AgentFile?.Value ?? throw new ArgumentException("Agent staging is required.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(request.RestoreTimeout);
            await archives.RestoreAsync(path, root.RootDirectory, request.Mode == DatabaseRestoreMode.ReplaceExistingDatabase, request.Files?.Paths,
                new InlineProgress<long>(bytes => progress?.Report(new(RestoreExecutionStage.Restoring, null, bytes, null, time.GetUtcNow()))), deadline.Token,
                request.Files?.RelativeDirectory ?? "").ConfigureAwait(false);
            return new(DatabaseRestoreStatus.Succeeded, null, DatabaseRestoreTargetState.Online, "Restored", started, time.GetUtcNow(), started, time.GetUtcNow(), [], []);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException or AggregateException or System.Text.Json.JsonException)
        {
            var cancelled = error is OperationCanceledException && cancellationToken.IsCancellationRequested;
            return new(cancelled ? DatabaseRestoreStatus.Cancelled : DatabaseRestoreStatus.Failed,
                new(cancelled ? RestoreFailureKind.Cancelled : RestoreFailureKind.SqlRestoreFailed, RestoreExecutionStage.Restoring,
                    cancelled ? "cancelled" : "file-restore-failed", "File restore did not complete; inspect retained rollback data if rollback was incomplete."),
                error is AggregateException ? DatabaseRestoreTargetState.Unknown : DatabaseRestoreTargetState.NotModified, null, started, time.GetUtcNow(), started, null, [], []);
        }
    }

    private BackupPreCheckReport CheckSource(DatabaseConnectionSettings connection, DatabaseCredential credential, string reference)
    {
        try
        {
            RequireLocal(connection, credential);
            if (!roots.TryGetValue(reference, out var root) || !Path.IsPathFullyQualified(root.RootDirectory) || !Directory.Exists(root.RootDirectory))
                return Failed("file-source-unavailable", "The configured file source is unavailable.");
            using var entries = Directory.EnumerateFileSystemEntries(root.RootDirectory).GetEnumerator();
            _ = entries.MoveNext();
            return new([new("file-system.source", BackupPreCheckStatus.Passed, "available", "The configured file source is available.")]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failed("file-source-unavailable", "The configured file source cannot be accessed.");
        }
    }

    private static void RequireLocal(DatabaseConnectionSettings connection, DatabaseCredential credential)
    {
        if (connection.Endpoint.Host != "localhost" || connection.Endpoint.Port is not null || credential is not IntegratedDatabaseCredential)
            throw new ArgumentException("File sources use local agent references without database credentials.");
    }
    private static BackupPreCheckReport Failed(string code, string message) => new([new("file-system.source", BackupPreCheckStatus.Failed, code, message, BackupFailureCause.InvalidConfiguration)]);
    private static StagingCleanup Cleanup(string? path, bool delete)
    {
        if (path is null || !File.Exists(path)) return StagingCleanup.NotNeeded;
        if (!delete) return new(StagingCleanupStatus.Kept);
        try { File.Delete(path); return new(StagingCleanupStatus.Deleted); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(StagingCleanupStatus.Failed, "staging-delete-failed"); }
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
