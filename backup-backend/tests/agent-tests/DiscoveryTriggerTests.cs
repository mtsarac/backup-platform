using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Discovery;

namespace BackupPlatform.AgentTests;

/// <summary>A heartbeat answer asking for a scan starts it in the background, with the same guarantees as a capability sync.</summary>
public sealed class DiscoveryTriggerTests
{
    private sealed class RecordingRunner : IDiscoveryScanRunner
    {
        private int _runs;

        public Func<AgentLocalIdentity, CancellationToken, Task> OnRun { get; set; } = static (_, _) => Task.CompletedTask;

        public int Runs => Volatile.Read(ref _runs);

        public Task RunAsync(AgentLocalIdentity identity, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            return OnRun(identity, cancellationToken);
        }
    }

    private static AgentBackendResult<AgentHeartbeatResponse> Acknowledged(string? discoveryScanReason) =>
        AgentBackendResult<AgentHeartbeatResponse>.Success(new AgentHeartbeatResponse
        {
            ServerTime = DateTimeOffset.UnixEpoch,
            HeartbeatIntervalSeconds = 30,
            OfflineTimeoutSeconds = 90,
            DiscoveryScanReason = discoveryScanReason,
        });

    [Fact]
    public async Task Heartbeats_without_a_scan_reason_never_start_a_scan()
    {
        var runner = new RecordingRunner();
        await using var h = new ConnectivityHarness(random: 0) { Discovery = runner };
        await h.StartAndDrainAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(125));

        Assert.True(h.Client.Heartbeats.Count >= 4);
        Assert.Equal(0, runner.Runs);
    }

    [Fact]
    public async Task A_scan_reason_starts_the_scan_with_the_current_identity()
    {
        var runner = new RecordingRunner();
        AgentLocalIdentity? scannedWith = null;
        runner.OnRun = (identity, _) =>
        {
            scannedWith = identity;
            return Task.CompletedTask;
        };
        await using var h = new ConnectivityHarness(random: 0) { Discovery = runner };
        h.Client.OnHeartbeat = (n, _) => Acknowledged(n == 2 ? DiscoveryScanReasons.Initial : null);
        await h.StartAndDrainAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(125));

        Assert.Equal(1, runner.Runs);
        Assert.Equal(h.Store.Identity!.Current.CredentialId, scannedWith!.Current.CredentialId);
    }

    [Fact]
    public async Task A_scan_reason_without_a_runner_is_ignored()
    {
        await using var h = new ConnectivityHarness(random: 0);
        h.Client.OnHeartbeat = (_, _) => Acknowledged(DiscoveryScanReasons.Manual);

        await h.StartAndDrainAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(65));

        Assert.True(h.Client.Heartbeats.Count >= 2);
        Assert.Equal(0, h.Snapshot.ConsecutiveHeartbeatFailures);
    }

    [Fact]
    public async Task A_failing_scan_is_logged_and_never_stops_or_delays_heartbeats()
    {
        var runner = new RecordingRunner { OnRun = static (_, _) => throw new InvalidOperationException("scan exploded") };
        await using var h = new ConnectivityHarness(random: 0) { Discovery = runner };
        h.Client.OnHeartbeat = (_, _) => Acknowledged(DiscoveryScanReasons.Periodic);
        await h.StartAndDrainAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(125));

        Assert.True(h.Client.Heartbeats.Count >= 4);
        Assert.Equal(h.Client.Heartbeats.Count, runner.Runs);
        Assert.All(h.Client.Heartbeats.Zip(h.Client.Heartbeats.Skip(1), (a, b) => (b.At - a.At).TotalSeconds), gap => Assert.Equal(30, gap));
        Assert.Equal(0, h.Snapshot.ConsecutiveHeartbeatFailures);
        Assert.Contains(h.Logs.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error && e.Message.Contains("discovery scan", StringComparison.Ordinal));
        Assert.DoesNotContain(TestIdentity.Secret, h.Logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_slow_scan_neither_delays_heartbeats_nor_runs_twice_at_once()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingRunner { OnRun = (_, _) => release.Task };
        await using var h = new ConnectivityHarness(random: 0) { Discovery = runner };
        h.Client.OnHeartbeat = (_, _) => Acknowledged(DiscoveryScanReasons.Periodic);
        await h.StartAndDrainAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(95));

        Assert.True(h.Client.Heartbeats.Count >= 3);
        Assert.Equal(1, runner.Runs);

        release.SetResult();
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));

        Assert.Equal(2, runner.Runs);
    }

    [Fact]
    public async Task Shutdown_cancels_a_scan_that_is_still_running()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingRunner
        {
            OnRun = async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    throw;
                }
            },
        };
        await using var h = new ConnectivityHarness(random: 0) { Discovery = runner };
        h.Client.OnHeartbeat = (_, _) => Acknowledged(DiscoveryScanReasons.Periodic);
        await h.StartAndDrainAsync();
        Assert.Equal(1, runner.Runs);

        await h.StopAsync();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
