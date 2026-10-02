using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using static BackupPlatform.AgentTests.Support.Failures;

namespace BackupPlatform.AgentTests;

public sealed class ConnectivityServiceTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static IReadOnlyList<double> Gaps(IReadOnlyList<DateTimeOffset> times) =>
        times.Zip(times.Skip(1), (a, b) => (b - a).TotalSeconds).ToList();

    // ---- startup ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Missing_identity_fails_startup_with_a_clear_error_and_makes_no_requests()
    {
        await using var h = new ConnectivityHarness();
        h.Store.Identity = null;

        h.Start();
        var error = await Assert.ThrowsAsync<AgentIdentityUnavailableException>(() => h.Run);

        Assert.Contains("Enroll", error.Message, StringComparison.Ordinal);
        Assert.Empty(h.Client.Heartbeats);
        Assert.Empty(h.Channel.ConnectTimes);
    }

    [Fact]
    public async Task Unusable_stored_identity_fails_startup_without_leaking_store_internals()
    {
        await using var h = new ConnectivityHarness();
        h.Store.LoadFailure = new AgentCredentialStoreException("The identity file is not protected.");

        h.Start();
        var error = await Assert.ThrowsAsync<AgentIdentityUnavailableException>(() => h.Run);

        Assert.Contains("not protected", error.Message, StringComparison.Ordinal);
        Assert.IsType<AgentCredentialStoreException>(error.InnerException);
    }

    // ---- hybrid channel --------------------------------------------------------------------------------------

    [Fact]
    public async Task Connected_push_channel_stops_polling_after_one_catch_up_poll()
    {
        await using var h = new ConnectivityHarness(random: 0);
        await h.StartAndSettleAsync();

        Assert.Equal(AgentChannelState.Connected, h.Snapshot.State);
        Assert.False(h.Snapshot.PollingActive);
        Assert.Single(h.Client.Polls);

        await h.AdvanceAsync(TimeSpan.FromMinutes(5), 2 * Second);

        Assert.Single(h.Client.Polls);
        Assert.Equal(AgentChannelState.Connected, h.Snapshot.State);
    }

    [Fact]
    public async Task Polling_starts_only_after_push_stayed_unavailable_longer_than_the_fallback_delay()
    {
        await using var h = new ConnectivityHarness();
        h.Channel.OnConnect = (_, _) => PushFailed();
        await h.StartAndSettleAsync();

        await h.AdvanceSecondsAsync(19);
        Assert.Empty(h.Client.Polls);
        Assert.False(h.Snapshot.PollingActive);
        Assert.Equal(AgentChannelState.Connecting, h.Snapshot.State);

        await h.AdvanceSecondsAsync(2);
        Assert.True(h.Snapshot.PollingActive);
        Assert.Single(h.Client.Polls);
    }

    [Fact]
    public async Task Zero_fallback_delay_polls_from_the_start()
    {
        await using var h = new ConnectivityHarness(new AgentConnectivityOptions { PushFallbackDelay = TimeSpan.Zero });
        h.Channel.OnConnect = (_, _) => PushFailed();

        await h.StartAndSettleAsync();

        Assert.True(h.Snapshot.PollingActive);
        Assert.Single(h.Client.Polls);
    }

    [Fact]
    public async Task Polling_respects_the_configured_interval_and_echoes_the_cursor()
    {
        await using var h = new ConnectivityHarness(random: 0);
        h.Channel.OnConnect = (_, _) => PushFailed();
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromMinutes(3), Second);

        var polls = h.Client.Polls;
        Assert.InRange(polls.Count, 5, 6);
        Assert.All(Gaps(polls.Select(p => p.At).ToList()), gap => Assert.InRange(gap, 29.4, 31.1));
        Assert.Null(polls[0].Cursor);
        Assert.Equal("cursor-1", polls[1].Cursor);
        Assert.Equal("cursor-2", polls[2].Cursor);
    }

    [Fact]
    public async Task Server_requested_poll_delay_lengthens_the_interval()
    {
        await using var h = new ConnectivityHarness(random: 0);
        h.Channel.OnConnect = (_, _) => PushFailed();
        h.Client.OnPoll = (_, _, _) => AgentBackendResult<AgentPollResponse>.Success(new AgentPollResponse { Cursor = "c", NextPollAfterSeconds = 90 });
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(100), Second);
        Assert.Single(h.Client.Polls);

        await h.AdvanceAsync(TimeSpan.FromSeconds(15), Second);
        Assert.Equal(2, h.Client.Polls.Count);
        Assert.InRange((h.Client.Polls[1].At - h.Client.Polls[0].At).TotalSeconds, 89.4, 91.1);
    }

    [Fact]
    public async Task Server_poll_delay_shorter_than_the_configured_interval_does_not_speed_polling_up()
    {
        await using var h = new ConnectivityHarness(random: 0);
        h.Channel.OnConnect = (_, _) => PushFailed();
        h.Client.OnPoll = (_, _, _) => AgentBackendResult<AgentPollResponse>.Success(new AgentPollResponse { Cursor = "c", NextPollAfterSeconds = 5 });
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(55), Second);

        Assert.Equal(2, h.Client.Polls.Count); // at about 20 s and 50 s, not every 5 s
    }

    [Fact]
    public async Task Rate_limited_poll_waits_at_least_the_retry_after_time()
    {
        await using var h = new ConnectivityHarness(random: 0);
        h.Channel.OnConnect = (_, _) => PushFailed();
        h.Client.OnPoll = (n, _, _) => n == 1
            ? Fail<AgentPollResponse>(AgentBackendFailureKind.RateLimited, TimeSpan.FromSeconds(120))
            : AgentBackendResult<AgentPollResponse>.Success(new AgentPollResponse { Cursor = "c" });
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(130), Second);
        Assert.Single(h.Client.Polls);
        Assert.Equal(1, h.Snapshot.ConsecutivePollFailures);

        await h.AdvanceAsync(TimeSpan.FromSeconds(20), Second);
        Assert.Equal(2, h.Client.Polls.Count);
        Assert.InRange((h.Client.Polls[1].At - h.Client.Polls[0].At).TotalSeconds, 119.4, 121.1);
        Assert.Equal(0, h.Snapshot.ConsecutivePollFailures);
    }

    [Fact]
    public async Task Transient_poll_errors_back_off_but_never_poll_faster_than_the_interval()
    {
        await using var h = new ConnectivityHarness(random: 0.9);
        h.Channel.OnConnect = (_, _) => PushFailed();
        h.Client.OnPoll = (_, _, _) => Fail<AgentPollResponse>(AgentBackendFailureKind.Transient);
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromMinutes(6), 2 * Second);

        var polls = h.Client.Polls;
        Assert.InRange(polls.Count, 3, 12);
        Assert.All(Gaps(polls.Select(p => p.At).ToList()), gap => Assert.True(gap >= 29.9, $"poll gap {gap}s is below the interval"));
    }

    [Fact]
    public async Task Restored_push_channel_returns_polling_to_idle()
    {
        await using var h = new ConnectivityHarness();
        // With half-ceiling delays the 8th attempt lands at about 61 s, well after polling became active at 20 s.
        h.Channel.OnConnect = (n, _) => n <= 7 ? PushFailed() : AgentPushConnectResult.Connected;
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(45), Second);
        Assert.True(h.Snapshot.PollingActive);

        await h.AdvanceAsync(TimeSpan.FromSeconds(30), Second);
        Assert.Equal(AgentChannelState.Connected, h.Snapshot.State);
        Assert.False(h.Snapshot.PollingActive);

        var pollsWhenRestored = h.Client.Polls.Count;
        await h.AdvanceAsync(TimeSpan.FromMinutes(5), 2 * Second);
        Assert.Equal(pollsWhenRestored, h.Client.Polls.Count);
    }

    [Fact]
    public async Task Connection_loss_activates_polling_after_the_fallback_delay_and_reconnection_stops_it()
    {
        await using var h = new ConnectivityHarness(random: 0);
        await h.StartAndSettleAsync();
        Assert.Single(h.Client.Polls);

        h.Channel.Listener!.OnReconnecting(null);
        await ConnectivityHarness.SettleAsync();
        Assert.Equal(AgentChannelState.Connecting, h.Snapshot.State);

        await h.AdvanceAsync(TimeSpan.FromSeconds(19), Second);
        Assert.False(h.Snapshot.PollingActive);
        await h.AdvanceAsync(TimeSpan.FromSeconds(2), Second);
        Assert.True(h.Snapshot.PollingActive);

        await h.AdvanceAsync(TimeSpan.FromSeconds(12), Second);
        Assert.Equal(2, h.Client.Polls.Count);

        h.Channel.Listener!.OnReconnected();
        await h.AdvanceAsync(TimeSpan.FromSeconds(3), Second);

        Assert.Equal(AgentChannelState.Connected, h.Snapshot.State);
        Assert.False(h.Snapshot.PollingActive);
        Assert.Equal(3, h.Client.Polls.Count); // one catch-up poll after the reconnect
        await h.AdvanceAsync(TimeSpan.FromMinutes(2), 2 * Second);
        Assert.Equal(3, h.Client.Polls.Count);
    }

    [Fact]
    public async Task Closed_push_channel_is_reconnected_with_backoff()
    {
        await using var h = new ConnectivityHarness();
        await h.StartAndSettleAsync();
        var firstListener = h.Channel.Listener;

        firstListener!.OnClosed(null);
        await h.AdvanceAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(250));

        Assert.Equal(2, h.Channel.ConnectTimes.Count);
        Assert.NotSame(firstListener, h.Channel.Listener);
        Assert.Equal(AgentChannelState.Connected, h.Snapshot.State);
    }

    [Fact]
    public async Task Poll_hint_triggers_a_poll_subject_to_the_minimum_spacing()
    {
        await using var h = new ConnectivityHarness(random: 0);
        await h.StartAndSettleAsync();
        Assert.Single(h.Client.Polls);

        h.Channel.Listener!.OnPollHint(new AgentPollHint { Reason = "work" });
        await h.AdvanceAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(100));
        Assert.Single(h.Client.Polls); // inside the 2 s spacing

        await h.AdvanceAsync(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(250));
        Assert.Equal(2, h.Client.Polls.Count);

        for (var i = 0; i < 20; i++)
        {
            h.Channel.Listener!.OnPollHint(new AgentPollHint { Reason = "burst" });
        }
        await h.AdvanceAsync(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250));

        var gaps = Gaps(h.Client.Polls.Select(p => p.At).ToList());
        Assert.Equal(3, h.Client.Polls.Count); // a burst of hints is one poll
        Assert.All(gaps, gap => Assert.True(gap >= 1.9, $"poll gap {gap}s is below the minimum spacing"));
    }

    [Fact]
    public async Task Reconnect_delays_grow_exponentially_and_stay_bounded()
    {
        await using var h = new ConnectivityHarness(random: 0.999);
        h.Channel.OnConnect = (_, _) => PushFailed();
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(400), Second);

        var gaps = Gaps(h.Channel.ConnectTimes);
        Assert.True(gaps.Count >= 10);
        double[] ceilings = [1, 2, 4, 8, 16, 32, 60];
        for (var i = 0; i < gaps.Count; i++)
        {
            var ceiling = ceilings[Math.Min(i, ceilings.Length - 1)];
            Assert.InRange(gaps[i], ceiling * 0.999 - 1.1, ceiling + 1.1);
        }
        Assert.Equal(h.Channel.ConnectTimes.Count - 1, gaps.Count);
        Assert.Equal(gaps.Count, h.Snapshot.ConsecutivePushFailures - 1);
    }

    // ---- authentication and protocol failures ----------------------------------------------------------------

    [Fact]
    public async Task Rejected_credential_pauses_everything_until_the_slow_retry_and_reloads_the_identity()
    {
        await using var h = new ConnectivityHarness(random: 0);
        var rotated = TestIdentity.Create(TestIdentity.RotatedSecret, credentialId: Guid.NewGuid());
        h.Channel.OnConnect = (_, identity) => identity.Current.Secret == TestIdentity.RotatedSecret
            ? AgentPushConnectResult.Connected
            : PushFailed(AgentBackendFailureKind.Unauthorized);
        h.Client.OnHeartbeat = (n, identity) => n == 1 || identity.Current.Secret == TestIdentity.RotatedSecret
            ? AgentBackendResult<AgentHeartbeatResponse>.Success(new AgentHeartbeatResponse { HeartbeatIntervalSeconds = 30 })
            : Fail<AgentHeartbeatResponse>(AgentBackendFailureKind.Unauthorized);
        await h.StartAndSettleAsync();

        Assert.Equal(AgentChannelState.Unauthorized, h.Snapshot.State);
        var loadsAfterStart = h.Store.LoadCount;
        h.Store.Identity = rotated;

        await h.AdvanceAsync(TimeSpan.FromSeconds(59), Second);
        Assert.Equal(AgentChannelState.Unauthorized, h.Snapshot.State);
        Assert.Single(h.Channel.ConnectTimes);
        Assert.Single(h.Client.Heartbeats);
        Assert.Empty(h.Client.Polls);
        Assert.Equal(loadsAfterStart, h.Store.LoadCount);

        await h.AdvanceAsync(TimeSpan.FromSeconds(5), Second);

        Assert.True(h.Store.LoadCount > loadsAfterStart);
        Assert.Equal(rotated.Current.CredentialId, h.Client.Heartbeats[^1].CredentialId);
        Assert.Equal(rotated.Current.CredentialId, h.Channel.LastIdentity!.Current.CredentialId);
        Assert.Equal(AgentChannelState.Connected, h.Snapshot.State);
    }

    [Fact]
    public async Task Credential_that_stays_rejected_is_retried_only_at_the_maximum_delay()
    {
        await using var h = new ConnectivityHarness(random: 0);
        h.Channel.OnConnect = (_, _) => PushFailed(AgentBackendFailureKind.Unauthorized);
        h.Client.OnHeartbeat = (n, _) => n == 1
            ? AgentBackendResult<AgentHeartbeatResponse>.Success(new AgentHeartbeatResponse { HeartbeatIntervalSeconds = 30 })
            : Fail<AgentHeartbeatResponse>(AgentBackendFailureKind.Unauthorized);
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromMinutes(5), 2 * Second);

        Assert.Equal(AgentChannelState.Unauthorized, h.Snapshot.State);
        Assert.InRange(h.Channel.ConnectTimes.Count, 5, 6);
        Assert.All(Gaps(h.Channel.ConnectTimes), gap => Assert.InRange(gap, 59, 63));
        Assert.All(Gaps(h.Client.Heartbeats.Select(x => x.At).ToList()), gap => Assert.InRange(gap, 59, 63));
        Assert.True(h.Store.LoadCount >= 5, "the identity is reloaded before each retry");
    }

    [Fact]
    public async Task Rejected_poll_credential_also_pauses_polling()
    {
        await using var h = new ConnectivityHarness(random: 0.5);
        h.Channel.OnConnect = (_, _) => PushFailed();
        h.Client.OnPoll = (_, _, _) => Fail<AgentPollResponse>(AgentBackendFailureKind.Unauthorized);
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(60), Second);

        Assert.Equal(AgentChannelState.Unauthorized, h.Snapshot.State);
        Assert.Single(h.Client.Polls);
        var connectsAtPause = h.Channel.ConnectTimes.Count;
        await h.AdvanceAsync(TimeSpan.FromSeconds(15), Second); // the pause started at about 20 s and lasts 63 s
        Assert.Equal(connectsAtPause, h.Channel.ConnectTimes.Count);
        Assert.Single(h.Client.Polls);
    }

    [Fact]
    public async Task Protocol_incompatibility_stops_all_attempts_and_retries_slowly()
    {
        await using var h = new ConnectivityHarness(random: 0);
        var error = ProtocolError(AgentErrorActions.UpgradeAgent);
        h.Channel.OnConnect = (_, _) => PushFailed(AgentBackendFailureKind.ProtocolIncompatible, error);
        h.Client.OnHeartbeat = (n, _) => n == 1
            ? AgentBackendResult<AgentHeartbeatResponse>.Success(new AgentHeartbeatResponse { HeartbeatIntervalSeconds = 30 })
            : Fail<AgentHeartbeatResponse>(AgentBackendFailureKind.ProtocolIncompatible, error: error);
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(59), Second);

        Assert.Equal(AgentChannelState.ProtocolIncompatible, h.Snapshot.State);
        Assert.Single(h.Channel.ConnectTimes);
        Assert.Single(h.Client.Heartbeats);
        Assert.Empty(h.Client.Polls);
        Assert.Contains(AgentErrorActions.UpgradeAgent, h.Logs.AllText, StringComparison.Ordinal);

        await h.AdvanceAsync(TimeSpan.FromSeconds(5), Second);

        Assert.Equal(2, h.Channel.ConnectTimes.Count);
        Assert.Equal(2, h.Client.Heartbeats.Count);
    }

    [Fact]
    public async Task Protocol_notice_from_the_server_stops_the_channel_and_pauses_attempts()
    {
        await using var h = new ConnectivityHarness(random: 0);
        await h.StartAndSettleAsync();
        Assert.Equal(AgentChannelState.Connected, h.Snapshot.State);

        h.Channel.Listener!.OnProtocolNotice(new AgentProtocolNotice
        {
            Kind = AgentPushChannelContract.NoticeServerUpgradeRequired,
            Action = AgentErrorActions.WaitForServerUpgrade,
        });
        await h.AdvanceAsync(TimeSpan.FromSeconds(50), Second);

        Assert.Equal(AgentChannelState.ProtocolIncompatible, h.Snapshot.State);
        Assert.Single(h.Channel.ConnectTimes);
        Assert.True(h.Channel.StopCount >= 1);
        Assert.Contains(AgentErrorActions.WaitForServerUpgrade, h.Logs.AllText, StringComparison.Ordinal);
        Assert.Single(h.Client.Heartbeats);

        await h.AdvanceAsync(TimeSpan.FromSeconds(20), Second);

        Assert.Equal(2, h.Channel.ConnectTimes.Count);
        Assert.Equal(AgentChannelState.Connected, h.Snapshot.State);
    }

    // ---- heartbeat -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Heartbeat_runs_on_its_own_period_whatever_the_channel_does()
    {
        await using var h = new ConnectivityHarness(random: 0.5);
        h.Channel.OnConnect = (_, _) => PushFailed();
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromMinutes(5), 2 * Second);

        var beats = h.Client.Heartbeats;
        Assert.InRange(beats.Count, 9, 11);
        Assert.All(Gaps(beats.Select(b => b.At).ToList()), gap => Assert.InRange(gap, 30, 33));
        Assert.NotNull(h.Snapshot.LastSuccessfulHeartbeatUtc);
    }

    [Fact]
    public async Task Heartbeat_failures_never_make_it_faster()
    {
        await using var h = new ConnectivityHarness(random: 0);
        h.Client.OnHeartbeat = (_, _) => Fail<AgentHeartbeatResponse>(AgentBackendFailureKind.Transient);
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromMinutes(5), Second);

        var gaps = Gaps(h.Client.Heartbeats.Select(b => b.At).ToList());
        Assert.InRange(h.Client.Heartbeats.Count, 10, 11);
        Assert.All(gaps, gap => Assert.InRange(gap, 29.9, 31.1));
        Assert.True(h.Snapshot.ConsecutiveHeartbeatFailures >= 10);
        Assert.Null(h.Snapshot.LastSuccessfulHeartbeatUtc);
    }

    [Fact]
    public async Task Rate_limited_heartbeat_honours_retry_after()
    {
        await using var h = new ConnectivityHarness(random: 0);
        h.Client.OnHeartbeat = (n, _) => n == 1
            ? Fail<AgentHeartbeatResponse>(AgentBackendFailureKind.RateLimited, TimeSpan.FromSeconds(100))
            : AgentBackendResult<AgentHeartbeatResponse>.Success(new AgentHeartbeatResponse { HeartbeatIntervalSeconds = 30 });
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(95), Second);
        Assert.Single(h.Client.Heartbeats);

        await h.AdvanceAsync(TimeSpan.FromSeconds(10), Second);
        Assert.Equal(2, h.Client.Heartbeats.Count);
        Assert.InRange((h.Client.Heartbeats[1].At - h.Client.Heartbeats[0].At).TotalSeconds, 99.4, 101.1);
    }

    [Theory]
    [InlineData(10, 10)]   // server recommends faster: follow it
    [InlineData(2, 5)]     // but never below the 5 s minimum
    [InlineData(120, 30)]  // server slower than configured: configured wins
    [InlineData(0, 30)]    // no recommendation
    public async Task Effective_heartbeat_interval_is_the_smaller_of_configured_and_server_advertised(int serverSeconds, int expectedSeconds)
    {
        await using var h = new ConnectivityHarness(random: 0);
        h.Client.OnHeartbeat = (_, _) => AgentBackendResult<AgentHeartbeatResponse>.Success(
            new AgentHeartbeatResponse { HeartbeatIntervalSeconds = serverSeconds, OfflineTimeoutSeconds = serverSeconds * 3 });
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(125), Second);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), h.Snapshot.EffectiveHeartbeatInterval);
        var beats = h.Client.Heartbeats;
        Assert.All(Gaps(beats.Select(b => b.At).ToList()), gap => Assert.InRange(gap, expectedSeconds - 0.1, expectedSeconds + 1.1));
        Assert.InRange(beats.Count, 125 / (expectedSeconds + 1) + 1, (125 / expectedSeconds) + 2);
    }

    [Fact]
    public async Task Server_lowering_the_heartbeat_interval_is_logged_once_and_failures_do_not_speed_it_up_further()
    {
        await using var h = new ConnectivityHarness(random: 0);
        h.Client.OnHeartbeat = (n, _) => n <= 3
            ? AgentBackendResult<AgentHeartbeatResponse>.Success(new AgentHeartbeatResponse { HeartbeatIntervalSeconds = 10 })
            : Fail<AgentHeartbeatResponse>(AgentBackendFailureKind.Transient);
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(120), Second);

        Assert.Single(h.Logs.Entries, e => e.Message.Contains("shorter heartbeat interval", StringComparison.Ordinal));
        Assert.All(Gaps(h.Client.Heartbeats.Select(b => b.At).ToList()), gap => Assert.True(gap >= 9.9, $"gap {gap}s"));
    }

    [Fact]
    public async Task Heartbeat_request_reports_version_platform_runtime_and_capability_revision()
    {
        await using var h = new ConnectivityHarness(random: 0);
        await h.StartAndSettleAsync();
        h.Capabilities.Revision = "rev-7";

        await h.AdvanceAsync(TimeSpan.FromSeconds(31), Second);

        var first = h.Client.Heartbeats[0].Request;
        Assert.Equal("1.2.3", first.AgentVersion);
        Assert.Equal(AgentPlatformNames.Linux, first.Platform);
        Assert.Null(first.CapabilityRevision);
        Assert.Equal("Test OS", first.Runtime?.OsDescription);
        Assert.Equal("10.0.0", first.Runtime?.RuntimeVersion);
        Assert.Equal("rev-7", h.Client.Heartbeats[1].Request.CapabilityRevision);
    }

    // ---- shutdown --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_stops_every_loop_and_the_channel_without_further_requests(bool pushWorks)
    {
        await using var h = new ConnectivityHarness(random: 0.5);
        h.Channel.OnConnect = (_, _) => pushWorks ? AgentPushConnectResult.Connected : PushFailed();
        await h.StartAndSettleAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(45), Second);

        var started = System.Diagnostics.Stopwatch.StartNew();
        await h.StopAsync();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(AgentChannelState.Stopped, h.Snapshot.State);
        Assert.False(h.Snapshot.PollingActive);
        Assert.True(h.Channel.StopCount >= 1);
        var heartbeats = h.Client.Heartbeats.Count;
        var polls = h.Client.Polls.Count;
        var connects = h.Channel.ConnectTimes.Count;

        await h.AdvanceAsync(TimeSpan.FromMinutes(10), 10 * Second);

        Assert.Equal(heartbeats, h.Client.Heartbeats.Count);
        Assert.Equal(polls, h.Client.Polls.Count);
        Assert.Equal(connects, h.Channel.ConnectTimes.Count);
    }

    [Fact]
    public async Task Unexpected_transport_exceptions_become_transient_failures_instead_of_ending_the_service()
    {
        await using var h = new ConnectivityHarness(random: 0.5);
        h.Channel.OnConnect = (n, _) => n == 1 ? throw new InvalidOperationException("boom") : AgentPushConnectResult.Connected;
        await h.StartAndSettleAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(250));

        Assert.False(h.Run.IsCompleted);
        Assert.Equal(AgentChannelState.Connected, h.Snapshot.State);
        Assert.Equal(2, h.Channel.ConnectTimes.Count);
    }
}
