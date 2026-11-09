using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Backup;

/// <summary>
/// Removes the <c>.partial</c> upload files a killed agent left on its local volumes. A file goes only when it is of the platform's managed grammar, its attempt is
/// named by its own key, the backend confirms that attempt is terminal (a paused attempt may still resume it), it is old enough and nobody has it open.
/// </summary>
public sealed partial class LocalPartialCleaner(
    LocalVolumes volumes, Func<Guid, CancellationToken, Task<bool>> isAttemptTerminal, TimeProvider time, ILogger<LocalPartialCleaner> logger)
{
    private const int MaxDeletesPerSweep = 1000;

    /// <summary>Returns the number of files removed. A volume that cannot be listed is skipped; the next sweep retries.</summary>
    public async Task<int> RunAsync(TimeSpan minimumAge, CancellationToken cancellationToken)
    {
        var deleted = 0;
        var terminal = new Dictionary<Guid, bool>();
        foreach (var volume in volumes.All())
        {
            try
            {
                var candidates = new List<(StorageObjectKey Key, Guid AttemptId)>();
                await foreach (var entry in volume.EnumerateAsync(null, time.GetUtcNow() - minimumAge, cancellationToken).ConfigureAwait(false))
                {
                    if (entry.Kind != StorageEntryKind.UnpublishedUpload || entry.InUse != false || candidates.Count >= MaxDeletesPerSweep) continue;
                    if (!ManagedBackupLayout.TryParseStorageKey(entry.Key.Value, out var layout) || layout is not { IsPartial: true }) continue;
                    candidates.Add((entry.Key, layout.AttemptId));
                }
                foreach (var (key, attemptId) in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!terminal.TryGetValue(attemptId, out var isTerminal))
                        terminal[attemptId] = isTerminal = await isAttemptTerminal(attemptId, cancellationToken).ConfigureAwait(false);
                    if (!isTerminal) continue;
                    if (await volume.DeleteAsync(key, cancellationToken).ConfigureAwait(false)) { deleted++; LogDeleted(logger, attemptId); }
                }
            }
            catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException)
            {
                LogFailed(logger, error.GetType().Name);
            }
        }
        return deleted;
    }

    [LoggerMessage(EventId = 114, Level = LogLevel.Information, Message = "Removed a leftover partial upload of the finished backup attempt {AttemptId}.")]
    private static partial void LogDeleted(ILogger logger, Guid attemptId);

    [LoggerMessage(EventId = 115, Level = LogLevel.Warning, Message = "Local partial-upload clean-up problem: ExceptionType={ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
