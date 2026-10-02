using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.AgentTests;

/// <summary>The connectivity service with the real SignalR channel against a real Kestrel hub, in real time with tiny delays.</summary>
public sealed class EndToEndConnectivityTests
{
    private static async Task<(AgentConnectivityService Service, FakeBackendClient Client, CancellationTokenSource Cts, Task Run)> StartAsync(TestHubServer server)
    {
        var options = new AgentConnectivityOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            PollInterval = TimeSpan.FromSeconds(5),
            PushFallbackDelay = TimeSpan.FromSeconds(2),
            ReconnectBaseDelay = TimeSpan.FromMilliseconds(20),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(200),
            MinimumPollSpacing = TimeSpan.FromSeconds(1),
        };
        var logs = new CaptureLoggerProvider();
        var factory = logs.CreateFactory();
        var backoff = new AgentBackoff(options.ReconnectBaseDelay, options.ReconnectMaxDelay);
        var client = new FakeBackendClient(TimeProvider.System);
        var channel = new SignalRAgentPushChannel(AgentBackendEndpoint.ForLoopbackTesting(server.BaseUri), backoff, TimeProvider.System, factory);
        var service = new AgentConnectivityService(
            new FakeCredentialStore(TestIdentity.Create()), client, channel,
            new AgentSoftwareInfo("1.0.0", "linux", null, null), new NullCapabilityRevisionSource(), options, backoff,
            TimeProvider.System, factory.CreateLogger<AgentConnectivityService>());
        var cts = new CancellationTokenSource();
        var run = service.RunAsync(cts.Token);
        await Wait.UntilAsync(() => service.Current.State == AgentChannelState.Connected, "push channel connected");
        return (service, client, cts, run);
    }

    [Fact]
    public async Task Push_connected_stays_idle_and_a_poll_hint_triggers_exactly_one_poll()
    {
        await using var server = await TestHubServer.StartAsync();
        var (service, client, cts, run) = await StartAsync(server);
        try
        {
            await Wait.UntilAsync(() => client.Polls.Count == 1, "catch-up poll");
            Assert.False(service.Current.PollingActive);

            await server.SendPollHintAsync();
            await Wait.UntilAsync(() => client.Polls.Count == 2, "poll after hint");

            await Task.Delay(1_500);
            Assert.Equal(2, client.Polls.Count);
            Assert.False(service.Current.PollingActive);
            Assert.NotEmpty(client.Heartbeats);
        }
        finally
        {
            await cts.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Server_outage_activates_polling_and_recovery_returns_to_push_only()
    {
        await using var server = await TestHubServer.StartAsync();
        var (service, client, cts, run) = await StartAsync(server);
        try
        {
            await Wait.UntilAsync(() => client.Polls.Count == 1, "catch-up poll");

            await server.StopAsync();
            await Wait.UntilAsync(() => service.Current.State == AgentChannelState.Connecting, "outage detected");
            await Wait.UntilAsync(() => service.Current.PollingActive, "polling fallback active");

            await server.RestartAsync();
            await Wait.UntilAsync(() => service.Current.State == AgentChannelState.Connected && !service.Current.PollingActive, "push restored, polling idle");

            await Task.Delay(300); // let a catch-up poll triggered by the reconnect finish
            var polls = client.Polls.Count;
            await Task.Delay(1_500);
            Assert.Equal(polls, client.Polls.Count);
        }
        finally
        {
            await cts.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Graceful_shutdown_closes_the_connection_quickly()
    {
        await using var server = await TestHubServer.StartAsync();
        var (service, _, cts, run) = await StartAsync(server);
        await Wait.UntilAsync(() => server.State.Connections.Count == 1, "server sees the agent");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(AgentChannelState.Stopped, service.Current.State);
        await Wait.UntilAsync(() => server.State.Connections.IsEmpty, "server sees the agent leave");
    }
}
