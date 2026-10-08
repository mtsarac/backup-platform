using BackupPlatform.ManagedService.Quota;

namespace BackupPlatform.Api.Quota;

/// <summary>
/// Keeps the storage usage projection honest without anyone asking: it releases reservations that lapsed, computes the projection of tenants that
/// have none (this is how an upgrade backfills existing tenants, in small per-tenant transactions rather than one long migration), and
/// reconciles the projections that were verified longest ago. Every decision is made in the database by the service it calls, so a restart loses
/// nothing, a late tick merely acts late, and any number of nodes may run it. Correctness never depends on this loop: admission and reads
/// recompute a missing or stale projection themselves.
/// </summary>
public sealed class StorageUsageMaintenanceWorker(IServiceScopeFactory scopes, QuotaOptions options, TimeProvider time, ILogger<StorageUsageMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.MaintenanceEnabled)
        {
            return;
        }
        using var timer = new PeriodicTimer(options.MaintenanceInterval, time);
        try
        {
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "The storage usage maintenance pass failed; it will run again on its next tick.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One pass in its own scope. Public so hosts and tests can drive it without waiting for the timer.</summary>
    public async Task<StorageUsageMaintenanceReport> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var report = await scope.ServiceProvider.GetRequiredService<IStorageUsageService>().RunMaintenanceAsync(time.GetUtcNow(), cancellationToken);
        foreach (var error in report.Errors)
        {
            logger.LogError(error, "A storage usage maintenance step failed; the next pass repeats it.");
        }
        if (report.ReservationsExpired + report.ProjectionsInitialized + report.ProjectionsReconciled + report.DriftCorrected != 0)
        {
            logger.LogInformation("Storage usage maintenance: released {Expired} lapsed reservations, computed {Initialized} missing projections, reconciled {Reconciled} (corrected {Corrected}).",
                report.ReservationsExpired, report.ProjectionsInitialized, report.ProjectionsReconciled, report.DriftCorrected);
        }
        return report;
    }
}
