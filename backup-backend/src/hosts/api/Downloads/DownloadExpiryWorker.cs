using BackupPlatform.Application.Downloads;

namespace BackupPlatform.Api.Downloads;

/// <summary>Abandoned browsers and crashed workers cannot keep a replica pinned indefinitely.</summary>
public sealed class DownloadExpiryWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<DownloadExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IDownloadStore>().ExpireAsync(time.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError("Download expiry or expiry audit persistence failed with {ExceptionType}", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
