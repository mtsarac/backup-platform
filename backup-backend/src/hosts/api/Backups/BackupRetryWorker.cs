using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Application.Backups;

namespace BackupPlatform.Api.Backups;

/// <summary>
/// Turns due retries into queued attempts. The schedule is durable (a RetryPending row with NextAttemptAt); this loop only
/// notices it, so a restart loses nothing and a late scan merely starts a retry late. Any number of workers or backend instances may run
/// it: the store serialises per agent and the unique attempt number rejects a duplicate.
/// </summary>
public sealed class BackupRetryWorker(IServiceScopeFactory scopes, BackupExecutionOptions options, TimeProvider time, ILogger<BackupRetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.RetryScanInterval, time);
        try
        {
            do
            {
                try { await ScanAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogError(exception, "Unable to start due backup retries."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>One scan. Public so hosts and tests can drive it without waiting for the timer.</summary>
    public async Task<int> ScanAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<IBackupRunStore>()
            .StartDueRetriesAsync(time.GetUtcNow(), options.RetryScanBatchSize, cancellationToken);
        var push = scope.ServiceProvider.GetRequiredService<IAgentPushDispatcher>();
        foreach (var retry in started)
        {
            try { await push.TrySendPollHintAsync(retry.AgentId, "WorkAvailable", CancellationToken.None); }
            catch (Exception exception) { logger.LogWarning(exception, "Backup retry hint delivery failed; durable polling remains authoritative."); }
        }
        return started.Count;
    }
}
