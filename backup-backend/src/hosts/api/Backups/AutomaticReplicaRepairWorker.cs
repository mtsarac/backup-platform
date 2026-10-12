using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Api.Backups;

public sealed class AutomaticReplicaRepairWorker(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<AutomaticReplicaRepairWorker> logger) : BackgroundService
{
    public const int MaxGlobalConcurrentRepairs = 4;
    public const int MaxTenantConcurrentRepairs = 2;
    private static readonly TimeSpan DefaultScanInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StaleCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMinutes(1);

    private DateTimeOffset _lastStaleCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(DefaultScanInterval, time);
        try
        {
            do
            {
                try
                {
                    await TickAsync(cancellationToken: stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during automatic replica repair scan.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Performs one scan pass. Public so tests can drive it deterministically.</summary>
    public async Task<int> TickAsync(TenantId? tenantId = null, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IReplicaRepairStore>();
        var repairService = scope.ServiceProvider.GetRequiredService<ReplicaRepairService>();

        // 1. Stale recovery scan
        if (now - _lastStaleCheck >= StaleCheckInterval)
        {
            try
            {
                var recovered = await store.RecoverStaleRepairsAsync(now, TimeSpan.FromSeconds(30), cancellationToken);
                if (recovered > 0)
                {
                    logger.LogInformation("Recovered {Count} stale replica repair operations.", recovered);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Stale repair recovery failed.");
            }
            _lastStaleCheck = now;
        }

        // 2. Discover missing replicas on recent artifacts
        if (now - _lastDiscovery >= DiscoveryInterval)
        {
            try
            {
                var discovered = await store.DiscoverEligibleRepairsAsync(now, 10, cancellationToken, tenantId);
                if (discovered.Count > 0)
                {
                    logger.LogInformation("Discovered and scheduled {Count} automatic replica repairs.", discovered.Count);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Discovery of eligible repairs failed.");
            }
            _lastDiscovery = now;
        }

        // 3. Claim and execute due repairs subject to concurrency limits
        var executed = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (tenantId == null)
            {
                var activeGlobal = await store.GetActiveRepairsCountAsync(tenantId: null, cancellationToken);
                if (activeGlobal >= MaxGlobalConcurrentRepairs)
                {
                    break;
                }
            }

            var claimed = await store.ClaimDueRepairAsync(
                $"auto-repair-{Environment.ProcessId}",
                time.GetUtcNow(),
                TimeSpan.FromMinutes(10),
                cancellationToken,
                tenantId);
            if (claimed is null)
            {
                break;
            }

            var activeTenant = await store.GetActiveRepairsCountAsync(claimed.TenantId, cancellationToken);
            if (activeTenant > MaxTenantConcurrentRepairs)
            {
                await store.ScheduleRetryAsync(
                    claimed.TenantId,
                    claimed.Id,
                    claimed.LeaseToken!.Value,
                    "tenant_concurrency_limit",
                    "Tenant repair concurrency limit reached; deferred.",
                    time.GetUtcNow().AddSeconds(15),
                    time.GetUtcNow(),
                    cancellationToken);
                continue;
            }

            executed++;

            _ = Task.Run(async () =>
            {
                try
                {
                    await repairService.ExecuteRepairAsync(
                        claimed.TenantId,
                        claimed.ArtifactId,
                        claimed.Id,
                        admission: null,
                        cancellationToken,
                        claimedOperation: claimed);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled error executing repair {OperationId}.", claimed.Id);
                }
            });
        }

        return executed;
    }
}
