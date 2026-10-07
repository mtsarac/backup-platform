using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.AgentTests;

public sealed class BackupTaskConnectivityTests
{
    [Fact]
    public async Task Safety_polling_finds_claims_without_push_hints_and_connectivity_continues_during_backup()
    {
        await using var harness = new BackupTaskHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Pipeline.Execute = async (request, _, token) =>
        {
            await harness.Time.ObserveAsync("backup worker", release.Task, token);
            return harness.Success(request);
        };
        var envelope = harness.Envelope();
        var backend = new FakeBackendClient(harness.Time)
        {
            OnPoll = (_, _, _) => AgentBackendResult<AgentPollResponse>.Success(new AgentPollResponse { Tasks = [envelope] })
        };
        var push = new FakePushChannel(harness.Time);
        var options = new AgentConnectivityOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(5), PollInterval = TimeSpan.FromSeconds(5),
            MinimumPollSpacing = TimeSpan.FromSeconds(1), PollWhilePushConnected = true
        };
        var connectivity = new AgentConnectivityService(harness.Store, backend, push,
            new AgentSoftwareInfo("1.2.3", "linux", null, null), new TestCapabilitySource(), options,
            new AgentBackoff(options.ReconnectBaseDelay, options.ReconnectMaxDelay, () => 0.5), harness.Time,
            NullLogger<AgentConnectivityService>.Instance, harness.Worker);
        using var stopping = new CancellationTokenSource();
        harness.Start();
        var running = connectivity.RunAsync(stopping.Token);
        try
        {
            await harness.Time.DrainAsync();
            Assert.Single(harness.Pipeline.Calls);
            await harness.Time.AdvanceAsync(TimeSpan.FromSeconds(20));
            Assert.True(backend.Polls.Count >= 3 && backend.Heartbeats.Count >= 3);
            Assert.Equal(AgentChannelState.Connected, connectivity.Current.State);
            Assert.Single(harness.Pipeline.Calls);
            Assert.Empty(harness.Client.Completions);
            release.SetResult();
            await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "provider terminal result");
            Assert.Equal("Succeeded", Assert.Single(harness.Client.Completions).Status);
        }
        finally
        {
            await stopping.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
