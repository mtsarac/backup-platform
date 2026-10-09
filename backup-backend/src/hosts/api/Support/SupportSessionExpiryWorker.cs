using BackupPlatform.Application.Support;

namespace BackupPlatform.Api.Support;

/// <summary>
/// Records the expiry (and its audit) of sessions whose window has passed. This is bookkeeping, not enforcement: every support request is judged against the
/// server clock at the moment it arrives, so an expired session stops working at its expiry instant whether or not this worker has run. It never cancels running
/// downloads or restores.
/// </summary>
public sealed class SupportSessionExpiryWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<SupportSessionExpiryWorker> logger) : BackgroundService
{
    private const int Batch = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ISupportAccessStore>().ExpireDueAsync(time.GetUtcNow(), Batch, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError("Support session expiry or its audit persistence failed with {ExceptionType}", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
