using BackupPlatform.Application.Downloads;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.Api.Downloads;

/// <summary>Listens for PostgreSQL share revocation notifications and periodically reconciles active share downloads.</summary>
public sealed class ShareRevocationCancellationWorker(
    ActiveDownloadRegistry registry,
    DbContextOptions<PlatformDbContext> dbOptions,
    ShareRevocationOptions options,
    ILogger<ShareRevocationCancellationWorker> logger) : BackgroundService
{
    public const string ChannelName = "backup_share_revoked";
    private NpgsqlConnection? _initialConnection;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.EnableListenNotify)
        {
            try
            {
                _initialConnection = await OpenListenerConnectionAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Initial share revocation listener connection failed with {ExceptionType}; background loop will retry.", exception.GetType().Name);
            }
        }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new List<Task>(2) { ReconcileLoopAsync(stoppingToken) };
        if (options.EnableListenNotify)
        {
            tasks.Add(ListenLoopAsync(stoppingToken));
        }
        await Task.WhenAll(tasks);
    }

    public async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        var activeShareIds = registry.GetActiveShareIds();
        if (activeShareIds.Count == 0) return;

        await using var context = PlatformDbContext.CreateForPlatformOperations(dbOptions);
        var unrevokedShareIds = await context.BackupShares
            .AsNoTracking()
            .Where(s => activeShareIds.Contains(s.Id) && s.RevokedAtUtc == null)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        var valid = unrevokedShareIds.ToHashSet();
        foreach (var shareId in activeShareIds)
        {
            if (!valid.Contains(shareId))
            {
                registry.CancelShare(shareId);
            }
        }
    }

    private async Task ReconcileLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ReconciliationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ReconcileOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning("Share revocation reconciliation failed with {ExceptionType}.", exception.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ListenLoopAsync(CancellationToken stoppingToken)
    {
        var connection = Interlocked.Exchange(ref _initialConnection, null);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    connection ??= await OpenListenerConnectionAsync(stoppingToken);
                    await ReconcileOnceAsync(stoppingToken);
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        await connection.WaitAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning("Share revocation LISTEN loop interrupted ({ExceptionType}); reconnecting.", exception.GetType().Name);
                    if (connection is not null)
                    {
                        await connection.DisposeAsync();
                        connection = null;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    private async Task<NpgsqlConnection> OpenListenerConnectionAsync(CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(dbOptions);
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("PostgreSQL connection string is not configured.");
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            connection.Notification += OnNotification;
            await using var command = new NpgsqlCommand($"LISTEN {ChannelName};", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs args)
    {
        if (string.Equals(args.Channel, ChannelName, StringComparison.Ordinal)
            && Guid.TryParse(args.Payload, out var shareId))
        {
            registry.CancelShare(shareId);
        }
    }

    public override void Dispose()
    {
        Interlocked.Exchange(ref _initialConnection, null)?.Dispose();
        base.Dispose();
    }
}
