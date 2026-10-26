using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Backup;

/// <summary>
/// Removes staged backup files that a crashed agent process left in the staging directory. The agent is the only party that can see
/// that directory, and it is a directory an operator also owns, so a file is removed only when ALL of these hold: its name is exactly
/// one the platform generates (<see cref="ManagedBackupLayout.IsManagedArtifactName"/>), it is a regular file, it is older than the
/// configured minimum age, no attempt of this process holds it (<see cref="StagingUsageRegistry"/>) and nobody has it open. Everything
/// else, an operator's own <c>.bak</c> included, is never touched. Nothing here logs or reports a path.
/// </summary>
public sealed partial class StagingCleaner
{
    private readonly AgentStagingDirectory _directory;
    private readonly StagingUsageRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger<StagingCleaner> _logger;
    private readonly Func<Guid, CancellationToken, Task<bool>>? _canReleaseTransfer;
    private readonly IRetainedUploadAborter? _uploads;

    public StagingCleaner(AgentStagingDirectory directory, StagingUsageRegistry registry, TimeProvider time, ILogger<StagingCleaner> logger,
        Func<Guid, CancellationToken, Task<bool>>? canReleaseTransfer = null, IRetainedUploadAborter? uploads = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _directory = directory;
        _registry = registry;
        _time = time;
        _logger = logger;
        _canReleaseTransfer = canReleaseTransfer;
        _uploads = uploads;
    }

    /// <summary>
    /// Runs one sweep. Failures to list or delete are reported, never thrown. Cancellation is observed between files and surfaces as
    /// <see cref="OperationCanceledException"/>; files already deleted stay deleted. A disabled sweep does not touch the directory.
    /// </summary>
    public async Task<StagingCleanupReport> RunAsync(StagingCleanupOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return StagingCleanupReport.Empty;
        }

        if (_canReleaseTransfer is not null)
        {
            IReadOnlyList<StagingFileEntry> journals;
            try { journals = _directory.ListFiles(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                LogFailed(_logger, StagingCleanupReasons.ListFailed, 1);
                return StagingCleanupReport.Empty;
            }
            foreach (var file in journals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!file.Name.StartsWith("transfer-", StringComparison.Ordinal) || !file.Name.EndsWith(".json", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(file.Name[9..^5], "N", out var attemptId)) continue;
                try
                {
                    var retained = RetainedTransfer.Load(_directory, attemptId);
                    if (!ManagedBackupLayout.IsManagedArtifactName(retained.Artifact.FileName) ||
                        !ManagedBackupLayout.IsManagedArtifactName(retained.SourceFile.FileName) ||
                        _registry.IsHeld(retained.Artifact.FileName) || _registry.IsHeld(retained.SourceFile.FileName)) continue;
                    // The durable local journal remains pinned until the owning backend confirms the attempt is terminal and the remote parts of its uploads are aborted.
                    if (await _canReleaseTransfer(attemptId, cancellationToken).ConfigureAwait(false) &&
                        await AbortUploadsAsync(attemptId, retained, cancellationToken).ConfigureAwait(false))
                        retained.Release(_directory, attemptId);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    LogFailed(_logger, StagingCleanupReasons.DeleteFailed, 1);
                }
            }
        }
        return await Task.Run(() => Sweep(options, cancellationToken), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>True when nothing remote is left to abort. A failed abort keeps the journal, so the next sweep retries it; without an aborter an upload is never forgotten.</summary>
    private async Task<bool> AbortUploadsAsync(Guid attemptId, RetainedTransfer retained, CancellationToken cancellationToken)
    {
        foreach (var upload in retained.Uploads ?? [])
        {
            try
            {
                if (_uploads is null) return false;
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(TimeSpan.FromMinutes(2));
                await _uploads.AbortAsync(attemptId, upload, bounded.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(_logger, StagingCleanupReasons.AbortFailed, 1);
                return false;
            }
        }
        return true;
    }

    private StagingCleanupReport Sweep(StagingCleanupOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tally = new Tally();

        IReadOnlyList<StagingFileEntry> files;
        try
        {
            files = _directory.ListFiles();
        }
        catch (UnauthorizedAccessException)
        {
            tally.Fail(StagingCleanupReasons.ListAccessDenied);
            return Finish(tally);
        }
        catch (IOException)
        {
            tally.Fail(StagingCleanupReasons.ListFailed);
            return Finish(tally);
        }

        var youngerThan = _time.GetUtcNow() - options.MinimumAge;
        foreach (var file in files.OrderBy(file => file.LastWriteUtc).ThenBy(file => file.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            tally.Scanned++;

            if (!ManagedBackupLayout.IsManagedArtifactName(file.Name))
            {
                tally.Unrecognized++;
                continue;
            }

            if (file.LastWriteUtc > youngerThan)
            {
                tally.KeptYoung++;
                continue;
            }

            // Checked right before the delete, after everything above: the shorter the gap, the smaller the window for a new attempt to claim the name.
            if (_registry.IsHeld(file.Name) || RetainedTransfer.IsPinned(_directory, file.Name))
            {
                tally.KeptInUse++;
                continue;
            }

            switch (_directory.TryDeleteIdleFile(file.Name))
            {
                case StagingDeleteOutcome.Deleted:
                    tally.DeletedNames.Add(file.Name);
                    LogDeleted(_logger, file.Name, file.SizeBytes);
                    break;
                case StagingDeleteOutcome.InUse:
                    tally.KeptInUse++;
                    break;
                case StagingDeleteOutcome.AccessDenied:
                    tally.Fail(StagingCleanupReasons.DeleteAccessDenied);
                    break;
                case StagingDeleteOutcome.Failed:
                    tally.Fail(StagingCleanupReasons.DeleteFailed);
                    break;
                case StagingDeleteOutcome.NotFound:
                    break;
            }
        }

        return Finish(tally);
    }

    private StagingCleanupReport Finish(Tally tally)
    {
        var failures = tally.Failures
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new StagingCleanupFailure(entry.Key, entry.Value))
            .ToArray();
        var report = new StagingCleanupReport(
            tally.Scanned,
            tally.DeletedNames.Count,
            tally.KeptYoung,
            tally.KeptInUse,
            tally.Unrecognized,
            failures.Sum(failure => failure.Count),
            failures,
            tally.DeletedNames.ToArray());

        foreach (var failure in failures)
        {
            LogFailed(_logger, failure.Reason, failure.Count);
        }

        if (report.Deleted > 0 || report.Failed > 0)
        {
            LogFinished(_logger, report.Scanned, report.Deleted, report.KeptYoung, report.KeptInUse, report.Unrecognized, report.Failed);
        }
        else
        {
            LogNothingToDo(_logger, report.Scanned, report.KeptYoung, report.KeptInUse, report.Unrecognized);
        }

        return report;
    }

    [LoggerMessage(EventId = 110, Level = LogLevel.Information,
        Message = "Staging clean-up removed a leftover staged backup file: File={File} SizeBytes={SizeBytes}")]
    private static partial void LogDeleted(ILogger logger, string file, long sizeBytes);

    [LoggerMessage(EventId = 111, Level = LogLevel.Warning,
        Message = "Staging clean-up problem: Reason={Reason} Count={Count}")]
    private static partial void LogFailed(ILogger logger, string reason, int count);

    [LoggerMessage(EventId = 112, Level = LogLevel.Information,
        Message = "Staging clean-up finished: Scanned={Scanned} Deleted={Deleted} KeptYoung={KeptYoung} KeptInUse={KeptInUse} Unrecognized={Unrecognized} Failed={Failed}")]
    private static partial void LogFinished(ILogger logger, int scanned, int deleted, int keptYoung, int keptInUse, int unrecognized, int failed);

    [LoggerMessage(EventId = 113, Level = LogLevel.Debug,
        Message = "Staging clean-up found nothing to remove: Scanned={Scanned} KeptYoung={KeptYoung} KeptInUse={KeptInUse} Unrecognized={Unrecognized}")]
    private static partial void LogNothingToDo(ILogger logger, int scanned, int keptYoung, int keptInUse, int unrecognized);

    private sealed class Tally
    {
        public int Scanned { get; set; }
        public int KeptYoung { get; set; }
        public int KeptInUse { get; set; }
        public int Unrecognized { get; set; }
        public List<string> DeletedNames { get; } = [];
        public Dictionary<string, int> Failures { get; } = [];

        public void Fail(string reason) => Failures[reason] = Failures.GetValueOrDefault(reason) + 1;
    }
}
