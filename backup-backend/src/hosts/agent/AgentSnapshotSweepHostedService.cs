using BackupPlatform.SnapshotProviders.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Host;

/// <summary>
/// Removes native snapshots (LVM, Btrfs, ZFS) a crashed agent or a failed rollback left behind, once at start and then hourly. Providers skip snapshots
/// a running backup still owns. Housekeeping: a failed sweep is logged by exception type only and never takes the agent down.
/// </summary>
internal sealed partial class AgentSnapshotSweepHostedService(
    IReadOnlyList<NativeSnapshotProvider> providers,
    string snapshotDirectory,
    TimeProvider time,
    ILogger<AgentSnapshotSweepHostedService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SweepAsync(stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await SweepAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        foreach (var provider in providers)
        {
            try
            {
                var removed = await provider.SweepLeftoversAsync(snapshotDirectory, stoppingToken).ConfigureAwait(false);
                if (removed > 0) LogRemoved(logger, provider.Kind, removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogSweepFailed(logger, provider.Kind, exception.GetType().Name);
            }
        }
    }

    [LoggerMessage(EventId = 130, Level = LogLevel.Information, Message = "Removed leftover {Kind} snapshots: Count={Count}")]
    private static partial void LogRemoved(ILogger logger, SnapshotKind kind, int count);

    [LoggerMessage(EventId = 131, Level = LogLevel.Warning, Message = "Leftover {Kind} snapshot sweep failed: ExceptionType={ExceptionType}")]
    private static partial void LogSweepFailed(ILogger logger, SnapshotKind kind, string exceptionType);
}
