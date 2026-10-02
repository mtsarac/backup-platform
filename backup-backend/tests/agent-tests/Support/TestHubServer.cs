using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BackupPlatform.Contracts.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.AgentTests.Support;

internal sealed record ReceivedConnection(string Path, string? Authorization, string? Protocol, string Query);

/// <summary>Shared by every incarnation of the server so tests can stop and restart it on the same port.</summary>
internal sealed class TestHubState
{
    public Guid HelloAgentId { get; set; } = TestIdentity.AgentId;

    public string? RequiredAuthorization { get; set; }

    /// <summary>When set, every request to the hub path is answered with this status before reaching SignalR.</summary>
    public int? RejectStatus { get; set; }

    public ConcurrentQueue<ReceivedConnection> Received { get; } = new();

    public ConcurrentDictionary<string, HubCallerContext> Connections { get; } = new();
}

internal sealed class TestAgentHub(TestHubState state) : Hub
{
    public override async Task OnConnectedAsync()
    {
        state.Connections[Context.ConnectionId] = Context;
        await Clients.Caller.SendAsync(AgentPushChannelContract.ServerHelloMethod, new AgentServerHello
        {
            AgentId = state.HelloAgentId,
            ServerTime = DateTimeOffset.UtcNow,
            Protocol = new AgentServerProtocol { Current = "1.0", MinimumSupported = "1.0", MaximumSupported = "1.0" },
            HeartbeatIntervalSeconds = 30,
        });
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        state.Connections.TryRemove(Context.ConnectionId, out _);
        return Task.CompletedTask;
    }
}

/// <summary>A real Kestrel server on loopback hosting a throwaway hub that speaks the agent push contract (no client-to-server methods).</summary>
internal sealed class TestHubServer : IAsyncDisposable
{
    private WebApplication? _app;

    private TestHubServer(int port) => Port = port;

    public int Port { get; }

    public TestHubState State { get; } = new();

    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    public static async Task<TestHubServer> StartAsync()
    {
        var server = new TestHubServer(FreePort());
        await server.StartAsync(CancellationToken.None);
        return server;
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, Port));
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddSingleton(State);
        builder.Services.AddSignalR();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(AgentPushChannelContract.HubPath, StringComparison.Ordinal))
            {
                State.Received.Enqueue(new ReceivedConnection(
                    context.Request.Path,
                    context.Request.Headers.Authorization.ToString() is { Length: > 0 } auth ? auth : null,
                    context.Request.Headers[AgentProtocolHeader.Name].ToString() is { Length: > 0 } protocol ? protocol : null,
                    context.Request.QueryString.Value ?? string.Empty));
                if (State.RejectStatus is { } status)
                {
                    context.Response.StatusCode = status;
                    return;
                }
                if (State.RequiredAuthorization is { } required && context.Request.Headers.Authorization != required)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            await next();
        });
        app.MapHub<TestAgentHub>(AgentPushChannelContract.HubPath, options => options.Transports = HttpTransportType.WebSockets);
        await app.StartAsync(cancellationToken);
        _app = app;
    }

    public async Task StopAsync()
    {
        if (_app is null)
        {
            return;
        }
        var app = _app;
        _app = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await app.StopAsync(timeout.Token);
        await app.DisposeAsync();
    }

    public Task RestartAsync() => StartAsync(CancellationToken.None);

    public async Task SendAsync(string method, object payload)
    {
        var hub = _app!.Services.GetRequiredService<IHubContext<TestAgentHub>>();
        await hub.Clients.All.SendAsync(method, payload);
    }

    public Task SendPollHintAsync(string reason = "work") =>
        SendAsync(AgentPushChannelContract.PollHintMethod, new AgentPollHint { Reason = reason });

    /// <summary>Server-side abort of every connection, as when a newer connection of the same agent replaces them.</summary>
    public void AbortAll()
    {
        foreach (var context in State.Connections.Values)
        {
            context.Abort();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
