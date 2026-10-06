using BackupPlatform.Agent.Core.Backup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Host;

/// <summary>
/// Sweeps the agent's staging directory for files a crashed process left behind: once shortly after start, then every
/// <see cref="StagingCleanupOptions.ScanInterval"/>. The sweep is housekeeping, so nothing it does may take the agent down: a failed
/// sweep is logged by its exception type only (never its message, which can name a path) and the next one runs on schedule.
/// </summary>
internal sealed partial class AgentStagingCleanupHostedService(
    StagingCleaner cleaner,
    StagingCleanupOptions options,
    TimeProvider time,
    ILogger<AgentStagingCleanupHostedService> logger) : BackgroundService
{
    /// <summary>Lets start-up finish (and an interrupted task be picked up again) before the first directory scan.</summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, time, stoppingToken).ConfigureAwait(false);
            await SweepAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(options.ScanInterval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await cleaner.RunAsync(options, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogSweepFailed(logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 120, Level = LogLevel.Information, Message = "Staging clean-up is disabled by configuration.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 121, Level = LogLevel.Warning, Message = "Staging clean-up sweep failed: ExceptionType={ExceptionType}")]
    private static partial void LogSweepFailed(ILogger logger, string exceptionType);
}
