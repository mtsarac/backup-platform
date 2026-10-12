using System.Collections.Concurrent;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
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

    private readonly ConcurrentDictionary<Guid, Task> _runningTasks = new();
    private readonly CancellationTokenSource _workerStoppingCts = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    private DateTimeOffset _lastStaleCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;

    public int RunningCount => _runningTasks.Count;

    /// <summary>Signals the worker to perform an immediate repair scan pass.</summary>
    public void TriggerScan()
    {
        if (_wakeSignal.CurrentCount == 0)
        {
            try { _wakeSignal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    /// <summary>Waits for all tracked active repair tasks to complete. Useful for deterministic testing.</summary>
    public async Task WaitForRunningTasksAsync(CancellationToken cancellationToken = default)
    {
        var active = _runningTasks.Values.ToArray();
        if (active.Length > 0)
        {
            await Task.WhenAll(active).WaitAsync(cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _workerStoppingCts.Token);
        var combinedToken = linkedCts.Token;

        while (!combinedToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken: combinedToken);
            }
            catch (OperationCanceledException) when (combinedToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during automatic replica repair scan.");
            }

            try
            {
                await _wakeSignal.WaitAsync(DefaultScanInterval, combinedToken);
            }
            catch (OperationCanceledException) when (combinedToken.IsCancellationRequested) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _workerStoppingCts.Cancel();
        }
        catch (ObjectDisposedException) { }

        await base.StopAsync(cancellationToken);

        var active = _runningTasks.Values.ToArray();
        if (active.Length > 0)
        {
            logger.LogInformation("Waiting for {Count} active replica repairs to stop gracefully.", active.Length);
            try
            {
                await Task.WhenAll(active).WaitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                logger.LogWarning("Timed out or cancelled waiting for active repairs during worker shutdown.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while waiting for active repairs during worker shutdown.");
            }
        }
    }

    /// <summary>Performs one scan pass. Public so tests can drive it deterministically.</summary>
    public async Task<int> TickAsync(TenantId? tenantId = null, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IReplicaRepairStore>();

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
        while (!cancellationToken.IsCancellationRequested && !_workerStoppingCts.IsCancellationRequested)
        {
            if (tenantId == null)
            {
                var activeGlobal = await store.GetActiveRepairsCountAsync(tenantId: null, cancellationToken);
                if (activeGlobal >= MaxGlobalConcurrentRepairs || _runningTasks.Count >= MaxGlobalConcurrentRepairs)
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
            StartTrackedRepair(claimed);
        }

        return executed;
    }

    private void StartTrackedRepair(ReplicaRepairOperation claimed)
    {
        var operationId = claimed.Id;
        var task = Task.Run(async () =>
        {
            await using var taskScope = scopes.CreateAsyncScope();
            var repairService = taskScope.ServiceProvider.GetRequiredService<ReplicaRepairService>();
            try
            {
                await repairService.ExecuteRepairAsync(
                    claimed.TenantId,
                    claimed.ArtifactId,
                    claimed.Id,
                    admission: null,
                    externalToken: _workerStoppingCts.Token,
                    claimedOperation: claimed);
            }
            catch (OperationCanceledException) when (_workerStoppingCts.IsCancellationRequested)
            {
                logger.LogInformation("Repair operation {OperationId} stopped due to worker shutdown.", operationId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error executing repair {OperationId}.", operationId);
            }
        });

        _runningTasks[operationId] = task;
        _ = task.ContinueWith(_ => { _runningTasks.TryRemove(operationId, out Task? _); }, TaskScheduler.Default);
    }

    public override void Dispose()
    {
        try { _workerStoppingCts.Cancel(); } catch { }
        _workerStoppingCts.Dispose();
        _wakeSignal.Dispose();
        base.Dispose();
    }
}
