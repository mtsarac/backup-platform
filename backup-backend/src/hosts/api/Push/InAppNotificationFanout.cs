using System.Collections.Concurrent;
using System.Threading.Channels;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.Api.Push;

public sealed record InAppNotificationMessage(Guid Id, Guid EventId, string Type, string SubjectName, DateTimeOffset OccurredAt, DateTimeOffset? ReadAt);

public sealed class InAppNotificationFanout(
    DbContextOptions<PlatformDbContext> options,
    IHubContext<InAppNotificationHub> hub,
    TimeProvider time,
    ILogger<InAppNotificationFanout> logger) : BackgroundService
{
    private const int PageSize = 100;
    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    private sealed class Connection(HubCallerContext context, UserId userId, Guid sessionId)
    {
        public HubCallerContext Context { get; } = context;
        public UserId UserId { get; } = userId;
        public Guid SessionId { get; } = sessionId;
        public HashSet<Guid> Seen { get; } = [];
    }

    public async Task<bool> RegisterAsync(HubCallerContext context, UserId userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var connection = new Connection(context, userId, sessionId);
        if (!await IsActiveAsync(connection, cancellationToken)) return false;
        _connections[context.ConnectionId] = connection;
        _signals.Writer.TryWrite(true);
        return true;
    }

    public void Unregister(string connectionId) => _connections.TryRemove(connectionId, out _);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var listener = ListenAsync(listenerCancellation.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try { await _signals.Reader.WaitToReadAsync(timeout.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
                while (_signals.Reader.TryRead(out _)) { }
                await ReconcileAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            listenerCancellation.Cancel();
            await listener;
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var context = PlatformDbContext.CreateForPlatformOperations(options);
                var settings = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString() ?? throw new InvalidOperationException("The platform database connection is missing.")) { Pooling = false, Multiplexing = false };
                await using var connection = new NpgsqlConnection(settings.ConnectionString);
                connection.Notification += (_, _) => _signals.Writer.TryWrite(true);
                await connection.OpenAsync(cancellationToken);
                await using (var command = new NpgsqlCommand("LISTEN user_notifications", connection)) await command.ExecuteNonQueryAsync(cancellationToken);
                _signals.Writer.TryWrite(true);
                while (!cancellationToken.IsCancellationRequested) await connection.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Notification listener disconnected ({ExceptionType}); reconciliation remains active.", exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var connection in _connections.Values)
            {
                try { await ReconcileConnectionAsync(connection, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { logger.LogWarning("Notification reconciliation failed ({ExceptionType}); it will retry.", exception.GetType().Name); }
            }
        }
        finally { _reconcileGate.Release(); }
    }

    private async Task ReconcileConnectionAsync(Connection connection, CancellationToken cancellationToken)
    {
        if (!await IsActiveAsync(connection, cancellationToken))
        {
            Unregister(connection.Context.ConnectionId);
            connection.Context.Abort();
            return;
        }
        var present = new HashSet<Guid>();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        for (var offset = 0; ; offset += PageSize)
        {
            var page = await (from item in context.UserNotifications.AsNoTracking()
                join notificationEvent in context.NotificationEvents.AsNoTracking() on item.EventId equals notificationEvent.Id
                where item.UserId == connection.UserId
                orderby item.Id
                select new { Message = new InAppNotificationMessage(item.Id, item.EventId, notificationEvent.Type, notificationEvent.SubjectName, notificationEvent.OccurredAt, item.ReadAt), notificationEvent.TenantId })
                .Skip(offset).Take(PageSize).ToListAsync(cancellationToken);
            foreach (var item in page)
            {
                present.Add(item.Message.Id);
                if (connection.Seen.Contains(item.Message.Id)) continue;
                if (!await IsActiveAsync(connection, cancellationToken))
                {
                    Unregister(connection.Context.ConnectionId);
                    connection.Context.Abort();
                    return;
                }
                if (item.TenantId is not null && !await context.Users.AsNoTracking().AnyAsync(user => user.Id == connection.UserId &&
                    (user.Role == UserRole.PlatformAdmin || context.TenantMemberships.Any(membership => membership.UserId == user.Id && membership.TenantId == item.TenantId)), cancellationToken))
                    continue;
                await hub.Clients.Client(connection.Context.ConnectionId).SendAsync("notificationReceived", item.Message, cancellationToken);
                connection.Seen.Add(item.Message.Id);
            }
            if (page.Count < PageSize) break;
        }
        connection.Seen.IntersectWith(present);
    }

    private async Task<bool> IsActiveAsync(Connection connection, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var session = await context.UserSessions.AsNoTracking().SingleOrDefaultAsync(
            row => row.Id == connection.SessionId && row.UserId == connection.UserId, cancellationToken);
        return session is { AuthenticationState: SessionAuthenticationState.FullyAuthenticated } && session.IsActive(time.GetUtcNow());
    }
}
