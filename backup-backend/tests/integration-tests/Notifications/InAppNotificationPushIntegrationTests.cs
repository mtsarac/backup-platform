using System.Collections.Concurrent;
using System.Net;
using BackupPlatform.Api.Push;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Infrastructure.Notifications;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests.Notifications;

public sealed class InAppNotificationPushIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    private static NotificationEvent Event(string key) => new(Guid.NewGuid(), null, "security_event", NotificationPhase.Raised,
        NotificationSeverity.Critical, new NotificationSubject("account", null, "Security event"), null, key, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static HubConnection Client(Uri address, CookieContainer cookies, ConcurrentQueue<InAppNotificationMessage> received)
    {
        var connection = new HubConnectionBuilder().WithUrl(new Uri(address, "/hubs/notifications"), options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.Headers["Cookie"] = cookies.GetCookieHeader(address);
            options.HttpMessageHandlerFactory = _ => new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
            options.WebSocketConfiguration = socket => socket.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }).Build();
        connection.On<InAppNotificationMessage>("notificationReceived", message => received.Enqueue(message));
        return connection;
    }

    [Fact]
    public async Task Other_node_receives_signal_and_recovers_dropped_signals_and_reconnects()
    {
        await using var nodeA = await AgentWorld.CreateAsync(fixture, kestrel: true);
        await using var nodeB = await AgentWorld.CreateAsync(fixture, kestrel: true);
        var account = await nodeA.SeedUserAsync(platformAdmin: true);
        var cookies = new CookieContainer();
        await nodeB.SignInAsync(account, cookies);
        var received = new ConcurrentQueue<InAppNotificationMessage>();
        await using var client = Client(nodeB.ServerAddress!, cookies, received);
        await client.StartAsync();
        await Task.Delay(500);

        var first = Event("cross-node");
        await using (var context = fixture.CreatePlatformContext())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await UserNotificationWriter.RecordAsync(context, first, [account.UserId], false, false, null, default);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await Eventually.AssertAsync(() => received.Any(item => item.EventId == first.Id), 10_000);

        var missed = Event("dropped-signal");
        await using (var context = fixture.CreatePlatformContext())
        {
            context.NotificationEvents.Add(missed);
            context.UserNotifications.Add(new UserNotification(Guid.NewGuid(), missed.Id, account.UserId));
            await context.SaveChangesAsync();
        }
        await Eventually.AssertAsync(() => received.Any(item => item.EventId == missed.Id), 40_000);

        await client.StopAsync();
        var offline = Event("reconnect");
        await using (var context = fixture.CreatePlatformContext())
        {
            context.NotificationEvents.Add(offline);
            context.UserNotifications.Add(new UserNotification(Guid.NewGuid(), offline.Id, account.UserId));
            await context.SaveChangesAsync();
        }
        await client.StartAsync();
        await Eventually.AssertAsync(() => received.Any(item => item.EventId == offline.Id), 10_000);
    }

    [Fact]
    public async Task Revoked_membership_and_session_block_push_without_refreshing_session_activity()
    {
        await using var node = await AgentWorld.CreateAsync(fixture, kestrel: true);
        var setup = await node.CreateTenantAsync(false);
        await using (var reset = fixture.CreatePlatformContext())
            await reset.UserSessions.Where(row => row.UserId == setup.Account.UserId).ExecuteDeleteAsync();
        var cookies = new CookieContainer();
        await node.SignInAsync(setup.Account, cookies);
        var received = new ConcurrentQueue<InAppNotificationMessage>();
        await using var client = Client(node.ServerAddress!, cookies, received);
        await client.StartAsync();
        await using var context = fixture.CreatePlatformContext();
        var session = await context.UserSessions.AsNoTracking().Where(row => row.UserId == setup.Account.UserId)
            .OrderByDescending(row => row.CreatedAt).FirstAsync();
        await context.TenantMemberships.Where(row => row.UserId == setup.Account.UserId && row.TenantId == setup.Tenant.Id).ExecuteDeleteAsync();
        var tenantEvent = new NotificationEvent(Guid.NewGuid(), setup.Tenant.Id, "security_event", NotificationPhase.Raised,
            NotificationSeverity.Critical, new NotificationSubject("account", null, "Private"), null, "revoked-membership", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        context.NotificationEvents.Add(tenantEvent);
        context.UserNotifications.Add(new UserNotification(Guid.NewGuid(), tenantEvent.Id, setup.Account.UserId));
        await context.SaveChangesAsync();
        var fanout = node.Factory.Services.GetRequiredService<InAppNotificationFanout>();
        await fanout.ReconcileAsync(default);
        Assert.DoesNotContain(received, item => item.EventId == tenantEvent.Id);
        Assert.Equal(session.LastActivityAt, (await context.UserSessions.AsNoTracking().SingleAsync(row => row.Id == session.Id)).LastActivityAt);

        await context.UserSessions.Where(row => row.Id == session.Id).ExecuteDeleteAsync();
        var afterLogout = Event("revoked-session");
        context.NotificationEvents.Add(afterLogout);
        context.UserNotifications.Add(new UserNotification(Guid.NewGuid(), afterLogout.Id, setup.Account.UserId));
        await context.SaveChangesAsync();
        await fanout.ReconcileAsync(default);
        Assert.DoesNotContain(received, item => item.EventId == afterLogout.Id);
    }
}
