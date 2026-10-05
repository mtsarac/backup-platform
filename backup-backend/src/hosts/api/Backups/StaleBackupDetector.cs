using BackupPlatform.Application.Backups;

namespace BackupPlatform.Api.Backups;

/// <summary>Expired leases are terminal AgentLost outcomes; polling never automatically creates a replacement attempt.</summary>
public sealed class StaleBackupDetector(IServiceScopeFactory scopes, TimeProvider time, ILogger<StaleBackupDetector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IBackupRunStore>().ExpireStaleAsync(time.GetUtcNow(), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogError(exception, "Unable to expire stale backup leases."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
