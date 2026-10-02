using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using BackupPlatform.Agents;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Api.Push;
using BackupPlatform.Api.Security;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Protocol;
using BackupPlatform.Domain.Identity;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

/// <summary>A SignalR client shaped like a real agent: header authentication, WebSockets only, no automatic reconnect.</summary>
internal sealed class TestHubClient : IAsyncDisposable
{
    private readonly ConcurrentQueue<AgentServerHello> _hellos = new();
    private readonly ConcurrentQueue<AgentPollHint> _hints = new();
    private readonly ConcurrentQueue<AgentProtocolNotice> _notices = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TestHubClient(HubConnection connection)
    {
        Connection = connection;
        connection.On<AgentServerHello>(AgentPushChannelContract.ServerHelloMethod, message => _hellos.Enqueue(message));
        connection.On<AgentPollHint>(AgentPushChannelContract.PollHintMethod, message => _hints.Enqueue(message));
        connection.On<AgentProtocolNotice>(AgentPushChannelContract.ProtocolNoticeMethod, message => _notices.Enqueue(message));
        connection.Closed += _ =>
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        };
    }

    public HubConnection Connection { get; }
    public IReadOnlyCollection<AgentServerHello> Hellos => _hellos;
    public IReadOnlyCollection<AgentPollHint> Hints => _hints;
    public IReadOnlyCollection<AgentProtocolNotice> Notices => _notices;
    public Task Closed => _closed.Task;

    public static TestHubClient Create(
        Uri server, string? authorization, string? protocol = "1.0", string? cookie = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server, AgentPushChannelContract.HubPath), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                if (authorization is not null)
                {
                    options.Headers["Authorization"] = authorization;
                }
                if (protocol is not null)
                {
                    options.Headers[AgentProtocolHeader.Name] = protocol;
                }
                if (cookie is not null)
                {
                    options.Headers["Cookie"] = cookie;
                }
                options.HttpMessageHandlerFactory = _ => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                options.WebSocketConfiguration = socket => socket.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            })
            .Build();
        return new TestHubClient(connection);
    }

    public static TestHubClient For(AgentWorld world, EnrolledAgent agent, string? protocol = "1.0") =>
        Create(world.ServerAddress!, AgentAuthorizationScheme.FormatHeaderValue(agent.CredentialId, agent.Secret), protocol);

    public async Task StartAsync() => await Connection.StartAsync();

    public async Task StartAndAwaitHelloAsync()
    {
        await StartAsync();
        await Eventually.AssertAsync(() => _hellos.Count > 0);
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}

internal static class Eventually
{
    public static async Task AssertAsync(Func<bool> condition, int timeoutMilliseconds = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected condition was not reached in time.");
            }
            await Task.Delay(25);
        }
    }
}

public sealed class AgentPushChannelIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static AgentRegistryAccess Registry(AgentWorld world) => new(world);

    private sealed class AgentRegistryAccess(AgentWorld world)
    {
        public AgentConnectionRegistry Registry => world.Factory.Services.GetRequiredService<AgentConnectionRegistry>();
        public IAgentPushDispatcher Dispatcher => world.Factory.Services.GetRequiredService<IAgentPushDispatcher>();
        public bool IsConnected(Guid agentId) => Registry.ConnectedAgents.Contains(new AgentId(agentId));
    }

    [Fact]
    public async Task ValidAgentConnectsIsMappedToItsPrincipalAndReceivesTheServerHello()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        await using var client = TestHubClient.For(world, agent);
        await client.StartAndAwaitHelloAsync();

        var hello = Assert.Single(client.Hellos);
        Assert.Equal(agent.AgentId, hello.AgentId);
        Assert.Equal("1.0", hello.Protocol.Current);
        Assert.Equal(30, hello.HeartbeatIntervalSeconds);
        Assert.True(Registry(world).IsConnected(agent.AgentId));
        Assert.Empty(client.Notices);

        var row = await world.ConnectivityOfAsync(agent.AgentId);
        Assert.NotNull(row!.LastConnectedAt);
        Assert.Equal(row.LastConnectedAt, row.LastSeenAt);
        var view = await world.AgentViewAsync(setup, agent.AgentId);
        Assert.Equal("online", view.GetProperty("connectivity").GetString());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, view.GetProperty("lastConnectedAt").ValueKind);
    }

    [Fact]
    public async Task EachAgentIsMappedToItsOwnIdentityAndPushIsDeliveredOnlyToTheTargetedAgent()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true);
        var a = await world.EnrollAsync(await world.CreateTenantAsync());
        var b = await world.EnrollAsync(await world.CreateTenantAsync());
        await using var clientA = TestHubClient.For(world, a);
        await using var clientB = TestHubClient.For(world, b);
        await clientA.StartAndAwaitHelloAsync();
        await clientB.StartAndAwaitHelloAsync();
        Assert.Equal(a.AgentId, clientA.Hellos.Single().AgentId);
        Assert.Equal(b.AgentId, clientB.Hellos.Single().AgentId);

        Assert.True(await Registry(world).Dispatcher.TrySendPollHintAsync(new AgentId(a.AgentId), "test", CancellationToken.None));
        await Eventually.AssertAsync(() => clientA.Hints.Count > 0);
        Assert.Equal("test", clientA.Hints.Single().Reason);
        await Task.Delay(300);
        Assert.Empty(clientB.Hints);
        Assert.False(await Registry(world).Dispatcher.TrySendPollHintAsync(AgentId.New(), "nobody", CancellationToken.None));
    }

    [Fact]
    public async Task BrowserSessionsAreRefusedByThePushChannel()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true);
        var setup = await world.CreateTenantAsync();
        var cookies = new CookieContainer();
        var session = await world.SignInAsync(setup.Account, cookies);
        using var control = await session.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        await using var client = TestHubClient.Create(world.ServerAddress!, authorization: null, cookie: cookies.GetCookieHeader(world.ServerAddress!));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(client.StartAsync);
        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        Assert.Empty(Registry(world).Registry.ConnectedAgents);
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("unknown-credential")]
    [InlineData("malformed")]
    [InlineData("bearer")]
    public async Task InvalidCredentialsAreRefusedByThePushChannel(string kind)
    {
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        var authorization = kind switch
        {
            "wrong-secret" => AgentAuthorizationScheme.FormatHeaderValue(agent.CredentialId, new string('Z', AgentSecretReference.SecretLength)),
            "unknown-credential" => AgentAuthorizationScheme.FormatHeaderValue(Guid.NewGuid(), agent.Secret),
            "malformed" => "BackupAgent nonsense",
            _ => $"Bearer {agent.Secret}"
        };
        await using var client = TestHubClient.Create(world.ServerAddress!, authorization);
        var failure = await Assert.ThrowsAsync<HttpRequestException>(client.StartAsync);
        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        Assert.DoesNotContain(agent.Secret, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokedAndDisabledAgentsCannotConnect()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true);
        var setup = await world.CreateTenantAsync();
        var revoked = await world.EnrollAsync(setup);
        var disabled = await world.EnrollAsync(setup);
        using (await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agents/{revoked.AgentId}/revoke")) { }
        await using (var context = fixture.CreatePlatformContext())
        {
            (await context.Agents.SingleAsync(row => row.Id == new AgentId(disabled.AgentId))).Disable();
            await context.SaveChangesAsync();
        }
        foreach (var agent in new[] { revoked, disabled })
        {
            await using var client = TestHubClient.For(world, agent);
            var failure = await Assert.ThrowsAsync<HttpRequestException>(client.StartAsync);
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }
        Assert.Empty(Registry(world).Registry.ConnectedAgents);
    }

    [Fact]
    public async Task RevokingAConnectedAgentDropsItsConnectionImmediately()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        await using var client = TestHubClient.For(world, agent);
        await client.StartAndAwaitHelloAsync();

        using var revoke = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agents/{agent.AgentId}/revoke");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        await client.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        await Eventually.AssertAsync(() => !Registry(world).IsConnected(agent.AgentId));
        await using var retry = TestHubClient.For(world, agent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<HttpRequestException>(retry.StartAsync)).StatusCode);
    }

    [Fact]
    public async Task ConnectionsOfAgentsThatLeaveTheActiveLifecycleAreDroppedByThePeriodicAudit()
    {
        await using var world = await AgentWorld.CreateAsync(fixture,
            connectivity: new AgentConnectivityOptions { ConnectionAuditInterval = TimeSpan.FromSeconds(1) }, kestrel: true);
        var setup = await world.CreateTenantAsync();
        var disabled = await world.EnrollAsync(setup);
        var healthy = await world.EnrollAsync(setup);
        await using var disabledClient = TestHubClient.For(world, disabled);
        await using var healthyClient = TestHubClient.For(world, healthy);
        await disabledClient.StartAndAwaitHelloAsync();
        await healthyClient.StartAndAwaitHelloAsync();

        await using (var context = fixture.CreatePlatformContext())
        {
            (await context.Agents.SingleAsync(row => row.Id == new AgentId(disabled.AgentId))).Disable();
            await context.SaveChangesAsync();
        }
        await disabledClient.Closed.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(Registry(world).IsConnected(healthy.AgentId));
        Assert.Equal(HubConnectionState.Connected, healthyClient.Connection.State);
    }

    [Fact]
    public async Task ANewConnectionReplacesTheOldOneAndTheReplacedConnectionCannotEvictItsSuccessor()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        await using var first = TestHubClient.For(world, agent);
        await using var second = TestHubClient.For(world, agent);
        await first.StartAndAwaitHelloAsync();
        await second.StartAndAwaitHelloAsync();

        await first.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HubConnectionState.Connected, second.Connection.State);
        // The first connection's disconnect callback has run (or will) without removing the second's mapping.
        await Task.Delay(300);
        Assert.Equal(second.Connection.ConnectionId, Registry(world).Registry.FindConnectionId(new AgentId(agent.AgentId)));
        Assert.Single(Registry(world).Registry.ConnectedAgents);
        Assert.True(await Registry(world).Dispatcher.TrySendPollHintAsync(new AgentId(agent.AgentId), "to-second", CancellationToken.None));
        await Eventually.AssertAsync(() => second.Hints.Count > 0);
        Assert.Empty(first.Hints);
    }

    [Fact]
    public async Task AnAgentCanDisconnectAndReconnectKeepingItsIdentity()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        await using var first = TestHubClient.For(world, agent);
        await first.StartAndAwaitHelloAsync();
        await first.Connection.StopAsync();
        await Eventually.AssertAsync(() => !Registry(world).IsConnected(agent.AgentId));

        await using var second = TestHubClient.For(world, agent);
        await second.StartAndAwaitHelloAsync();
        Assert.Equal(agent.AgentId, second.Hellos.Single().AgentId);
        Assert.NotEqual(first.Connection.ConnectionId, second.Connection.ConnectionId);
        Assert.True(Registry(world).IsConnected(agent.AgentId));
    }

    [Fact]
    public async Task ABackendRestartDropsConnectionsAndTheAgentReconnectsAsTheSameAgentWithNoRememberedConnectionState()
    {
        var clock = new MutableClock();
        TenantSetup setup;
        EnrolledAgent agent;
        await using (var first = await AgentWorld.CreateAsync(fixture, clock: clock, kestrel: true))
        {
            setup = await first.CreateTenantAsync();
            agent = await first.EnrollAsync(setup);
            await using var connected = TestHubClient.For(first, agent);
            await connected.StartAndAwaitHelloAsync();
            using var heartbeat = await first.HeartbeatAsync(agent);
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
            var closed = connected.Closed;
            await first.DisposeAsync();
            await closed.WaitAsync(TimeSpan.FromSeconds(15));
        }
        await using var restarted = await AgentWorld.CreateAsync(fixture, clock: new MutableClock(clock.GetUtcNow().AddSeconds(20)), kestrel: true);
        Assert.Empty(Registry(restarted).Registry.ConnectedAgents);
        await using var reconnected = TestHubClient.For(restarted, agent);
        await reconnected.StartAndAwaitHelloAsync();
        Assert.Equal(agent.AgentId, reconnected.Hellos.Single().AgentId);
        Assert.NotNull((await restarted.ConnectivityOfAsync(agent.AgentId))!.LastConnectedAt);
    }

    [Fact]
    public async Task ProtocolMustBeDeclaredAndSupportedToOpenThePushChannel()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        foreach (var protocol in new string?[] { null, "garbage", "2.0" })
        {
            await using var client = TestHubClient.For(world, agent, protocol);
            var failure = await Assert.ThrowsAsync<HttpRequestException>(client.StartAsync);
            Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        }
        Assert.Empty(Registry(world).Registry.ConnectedAgents);
        await using var good = TestHubClient.For(world, agent);
        await good.StartAndAwaitHelloAsync();
    }

    [Fact]
    public async Task AnAgentOnAnOlderSupportedMinorIsToldAnUpgradeIsAvailable()
    {
        var options = new AgentSecurityOptions
        {
            Protocol = new ProtocolNegotiationContract(new ProtocolVersion(1, 2),
                new ProtocolVersionRange(new ProtocolVersion(1, 0), new ProtocolVersion(1, 2)))
        };
        await using var world = await AgentWorld.CreateAsync(fixture, options, kestrel: true);
        var setup = await world.CreateTenantAsync();
        var oldAgent = await world.EnrollAsync(setup, protocolVersion: "1.0");
        var currentAgent = await world.EnrollAsync(setup, protocolVersion: "1.2");
        await using var older = TestHubClient.For(world, oldAgent, "1.0");
        await using var current = TestHubClient.For(world, currentAgent, "1.2");
        await older.StartAsync();
        await current.StartAsync();
        await Eventually.AssertAsync(() => older.Notices.Count > 0 && current.Hellos.Count > 0);
        var notice = Assert.Single(older.Notices);
        Assert.Equal("upgrade_available", notice.Kind);
        Assert.Equal("upgrade_agent", notice.Action);
        Assert.Equal("1.2", notice.Protocol.Current);
        await Task.Delay(300);
        Assert.Empty(current.Notices);
    }

    [Fact]
    public async Task ConnectAttemptsAreRateLimitedPerAgent()
    {
        await using var world = await AgentWorld.CreateAsync(fixture,
            limits: new AgentRateLimitOptions { ConnectPerAgent = 2 }, kestrel: true);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        await using var first = TestHubClient.For(world, agent);
        await first.StartAndAwaitHelloAsync();
        await using var second = TestHubClient.For(world, agent);
        var failure = await Assert.ThrowsAsync<HttpRequestException>(second.StartAsync);
        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal(HubConnectionState.Connected, first.Connection.State);
    }

    [Fact]
    public async Task OversizedHeartbeatBodiesAreRejectedOverRealSockets()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        using var tooLarge = await world.HeartbeatAsync(agent, new { agentVersion = "1.0.0", platform = "linux", padding = new string('p', 20_000) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        using var normal = await world.HeartbeatAsync(agent);
        Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
    }

    [Fact]
    public async Task PushConnectionsDoNotMakeAnAgentOnlineByThemselvesAfterTheHeartbeatTimeoutOnceTheConnectionIsGone()
    {
        var clock = new MutableClock();
        await using var world = await AgentWorld.CreateAsync(fixture, clock: clock, kestrel: true);
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        await using var client = TestHubClient.For(world, agent);
        await client.StartAndAwaitHelloAsync();
        await client.Connection.StopAsync();
        clock.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal("offline", (await world.AgentViewAsync(setup, agent.AgentId)).GetProperty("connectivity").GetString());
    }
}
