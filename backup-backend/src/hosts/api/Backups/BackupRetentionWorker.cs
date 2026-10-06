using BackupPlatform.Application.Backups;

namespace BackupPlatform.Api.Backups;

/// <summary>
/// Drives the backend-owned retention lifecycle: expiring backups and physically deleting due replicas, sweeping storages for orphans and
/// abandoned uploads, and pruning old schedule occurrences. Every decision is made by the database inside the services it calls, so this class
/// only notices that time has passed: a restart loses nothing, a late tick merely acts late, and any number of backend nodes may run it.
/// The three loops are independent so a slow storage sweep can never delay deletions that are due.
/// </summary>
public sealed class BackupRetentionWorker(IServiceScopeFactory scopes, BackupRetentionOptions options, TimeProvider time, ILogger<BackupRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        var loops = new List<Task>
        {
            RunLoopAsync("retention cycle", options.ScanInterval, token => RunRetentionOnceAsync(token), stoppingToken),
            RunLoopAsync("schedule occurrence pruning", options.ScheduleOccurrencePruneInterval, token => RunPruneOnceAsync(token), stoppingToken)
        };
        if (options.StorageSweepEnabled) loops.Add(RunLoopAsync("storage sweep", options.StorageSweepInterval, token => RunStorageSweepOnceAsync(token), stoppingToken));
        await Task.WhenAll(loops);
    }

    /// <summary>Once at startup, then on every tick. A failed iteration is logged and the loop carries on; only shutdown ends it.</summary>
    private async Task RunLoopAsync(string name, TimeSpan interval, Func<CancellationToken, Task> iteration, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            do
            {
                try { await iteration(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogError(exception, "The backup {Loop} failed; it will run again on its next tick.", name); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>One retention cycle in its own scope. Public so hosts and tests can drive it without waiting for the timer.</summary>
    public async Task<RetentionCycleResult> RunRetentionOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<BackupRetentionService>().RunCycleAsync(cancellationToken);
        if (result != new RetentionCycleResult(0, 0, 0, 0, 0, 0))
        {
            logger.LogInformation("Backup retention cycle: examined {Examined} artifacts, marked {Marked} replicas for deletion, claimed {Claimed}, deleted {Deleted}, failed {Failed}, lost lease {LeaseLost}.",
                result.ArtifactsExamined, result.ReplicasMarkedForDeletion, result.DeletionsClaimed, result.Deleted, result.Failed, result.LeaseLost);
        }
        return result;
    }

    /// <summary>One storage sweep in its own scope.</summary>
    public async Task<StorageSweepReport> RunStorageSweepOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var report = await scope.ServiceProvider.GetRequiredService<BackupStorageSweeper>().SweepAsync(cancellationToken);
        if (report.OrphansDeleted + report.PartialUploadsDeleted + report.Failed + report.StoragesUnavailable != 0)
        {
            logger.LogInformation("Backup storage sweep: examined {Examined} entries, deleted {Orphans} orphans and {Partials} abandoned uploads, {Failed} failures, {Unavailable} storages unavailable, truncated {Truncated}.",
                report.Examined, report.OrphansDeleted, report.PartialUploadsDeleted, report.Failed, report.StoragesUnavailable, report.Truncated);
        }
        return report;
    }

    /// <summary>One occurrence-pruning pass in its own scope; returns the rows removed.</summary>
    public async Task<int> RunPruneOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var pruned = await scope.ServiceProvider.GetRequiredService<BackupRetentionService>().PruneScheduleOccurrencesAsync(cancellationToken);
        if (pruned != 0) logger.LogInformation("Pruned {Count} decided schedule occurrences.", pruned);
        return pruned;
    }
}
