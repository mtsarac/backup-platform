using BackupPlatform.Application.Backups;

namespace BackupPlatform.Api.Backups;

/// <summary>
/// Records the expiry (and its audit) of break-glass requests whose window has passed. Bookkeeping, not enforcement: approving, denying and using a request are judged
/// against the server clock when they happen, so an expired grant stops working at its expiry instant whether or not this worker has run.
/// </summary>
public sealed class BreakGlassExpiryWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<BreakGlassExpiryWorker> logger) : BackgroundService
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
                await scope.ServiceProvider.GetRequiredService<IArtifactBreakGlassStore>().ExpireDueAsync(time.GetUtcNow(), Batch, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError("Break-glass expiry or its audit persistence failed with {ExceptionType}", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
