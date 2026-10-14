using BackupPlatform.Notifications;

namespace BackupPlatform.Api.Notifications;

/// <summary>
/// Looks for conditions worth a notification (failed backups, stale backups, offline agents, critical quota, degraded replicas) and turns them into
/// durable events. It only READS the platform's state and WRITES notification rows; it is a separate loop from backup orchestration, so a slow database
/// or a broken notification setup can delay a notification but never a backup. Any number of nodes may run it: the engine serializes per tenant and
/// every event has a unique key. The same loop trims old history once an hour.
/// </summary>
public sealed class NotificationDetectionWorker(IServiceScopeFactory scopes, NotificationOptions options, TimeProvider time, ILogger<NotificationDetectionWorker> logger) : BackgroundService
{
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        using var timer = new PeriodicTimer(options.SweepInterval, time);
        try
        {
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogError("The notification sweep failed ({ExceptionType}); it runs again on its next tick.", exception.GetType().Name); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>One pass in its own scope. Public so hosts and tests can drive it without waiting for the timer.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<NotificationEngine>().SweepAsync(cancellationToken);
        var now = time.GetUtcNow();
        if (now >= _nextPrune)
        {
            _nextPrune = now.AddHours(1);
            var removed = await scope.ServiceProvider.GetRequiredService<INotificationStore>().PruneAsync(now.AddDays(-options.HistoryRetentionDays), cancellationToken);
            if (removed > 0) logger.LogInformation("Removed {Count} notification events past the {Days}-day retention.", removed, options.HistoryRetentionDays);
        }
        return events;
    }
}

/// <summary>
/// Sends due deliveries. Deliveries are durable rows claimed under a lease, so a restart or a crashed node loses nothing: its claimed deliveries
/// become due again when the lease lapses (delivery is at-least-once; senders carry an idempotency key). Retries, backoff and the final failure are
/// recorded per delivery; a down SMTP server or webhook never throws out of this loop.
/// </summary>
public sealed class NotificationDeliveryWorker(IServiceScopeFactory scopes, NotificationOptions options, TimeProvider time, ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    private readonly string _owner = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        using var timer = new PeriodicTimer(options.DeliveryPollInterval, time);
        try
        {
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogError("The notification delivery pass failed ({ExceptionType}); it runs again on its next tick.", exception.GetType().Name); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Drains what is due now, one batch at a time. Public so hosts and tests can drive it without waiting for the timer.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        int claimed;
        do
        {
            await using var scope = scopes.CreateAsyncScope();
            claimed = await scope.ServiceProvider.GetRequiredService<NotificationDeliveryProcessor>().ProcessAsync(_owner, cancellationToken);
            total += claimed;
        }
        while (claimed >= options.DeliveryBatchSize);
        return total;
    }
}
