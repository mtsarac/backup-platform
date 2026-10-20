using System.Threading.Channels;
using BackupPlatform.Application.Storage;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Storage;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Api.Storage;

public sealed class StorageCapacityPeriodicProbeWorker(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<StorageCapacityPeriodicProbeWorker> logger,
    TimeSpan? interval = null) : BackgroundService, IStorageCapacityProbeTrigger
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(23);

    private readonly Channel<(TenantId TenantId, Guid ConnectionId)> _channel =
        Channel.CreateBounded<(TenantId, Guid)>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    public void EnqueueProbe(TenantId tenantId, Guid connectionId)
    {
        _channel.Writer.TryWrite((tenantId, connectionId));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            ProcessQueueAsync(stoppingToken),
            ProcessPeriodicAsync(stoppingToken)
        );
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var probeService = scope.ServiceProvider.GetRequiredService<StorageCapacityProbeService>();
                        await probeService.ProbeAsync(item.TenantId, item.ConnectionId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to execute queued capacity probe for tenant {TenantId}, connection {ConnectionId}", item.TenantId, item.ConnectionId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ProcessPeriodicAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval ?? DefaultInterval, time);
        try
        {
            do
            {
                try
                {
                    await RunPeriodicSweepAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Periodic storage capacity probe pass failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task DrainQueueAsync(CancellationToken cancellationToken = default)
    {
        while (_channel.Reader.TryRead(out var item))
        {
            await using var scope = scopes.CreateAsyncScope();
            var probeService = scope.ServiceProvider.GetRequiredService<StorageCapacityProbeService>();
            await probeService.ProbeAsync(item.TenantId, item.ConnectionId, cancellationToken);
        }
    }

    public async Task<int> RunPeriodicSweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<PlatformDbContext>>();
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        var probeService = scope.ServiceProvider.GetRequiredService<StorageCapacityProbeService>();

        var now = time.GetUtcNow();
        var cutoff = now - StaleThreshold;

        var connections = await db.StorageConnections.AsNoTracking()
            .Where(c => c.Enabled)
            .Select(c => new { c.TenantId, c.Id })
            .ToListAsync(cancellationToken);

        if (connections.Count == 0) return 0;

        var probedCount = 0;
        foreach (var conn in connections)
        {
            try
            {
                var latestSnapshotTime = await db.StorageCapacitySnapshots.AsNoTracking()
                    .Where(s => s.TenantId == conn.TenantId && s.ConnectionId == conn.Id)
                    .OrderByDescending(s => s.ReportedAt)
                    .ThenByDescending(s => s.Id)
                    .Select(s => (DateTimeOffset?)s.ReportedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (latestSnapshotTime == null || latestSnapshotTime.Value < cutoff)
                {
                    await probeService.ProbeAsync(conn.TenantId, conn.Id, cancellationToken);
                    probedCount++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Periodic capacity probe failed for tenant {TenantId}, connection {ConnectionId}", conn.TenantId, conn.Id);
            }
        }

        return probedCount;
    }
}
