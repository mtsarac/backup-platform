using BackupPlatform.Domain.Discovery;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests.Discovery;

public sealed class DiscoveryAgentStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);

    private static DiscoveryAgentState NewState() => DiscoveryAgentState.CreateInitial(new TenantId(Guid.NewGuid()), AgentId.New(), "initial", T0);

    [Fact]
    public void A_new_agent_is_due_for_its_initial_scan_and_only_one_claim_succeeds()
    {
        var state = NewState();

        Assert.Equal("initial", state.DueReason(T0, "periodic"));
        Assert.True(state.TryClaim(T0, Guid.NewGuid(), "periodic", 0, out var reason));
        Assert.Equal("initial", reason);
        Assert.Equal(DiscoveryScanState.Running, state.StateAt(T0));
        Assert.False(state.TryClaim(T0.AddSeconds(5), Guid.NewGuid(), "periodic", 0, out _));
        Assert.Null(state.DueReason(T0.AddSeconds(5), "periodic"));
    }

    [Fact]
    public void A_claimed_scan_that_never_reports_can_be_claimed_again_after_the_running_timeout()
    {
        var state = NewState();
        state.TryClaim(T0, Guid.NewGuid(), "periodic", 0, out _);
        state.RequestScan("manual", null, T0.AddMinutes(1));

        Assert.Null(state.DueReason(T0.AddMinutes(9), "periodic"));
        Assert.Equal("manual", state.DueReason(T0.AddMinutes(11), "periodic"));
    }

    [Fact]
    public void Only_the_current_scan_id_can_complete_and_a_failed_scan_is_not_a_success()
    {
        var state = NewState();
        var scanId = Guid.NewGuid();
        state.TryClaim(T0, scanId, "periodic", 0, out _);

        Assert.False(state.Complete(Guid.NewGuid(), T0.AddMinutes(1), "Completed", null, "[]"));
        Assert.True(state.Complete(scanId, T0.AddMinutes(1), "Failed", "scan_timeout", "[]"));
        Assert.False(state.Complete(scanId, T0.AddMinutes(2), "Completed", null, "[]"));
        Assert.Null(state.LastSuccessfulScanAt);
        Assert.Equal("scan_timeout", state.LastFailureCode);
        Assert.Equal(DiscoveryScanState.Idle, state.StateAt(T0.AddMinutes(2)));
    }

    [Fact]
    public void The_periodic_schedule_follows_the_interval_with_jitter_and_becomes_due_by_itself()
    {
        var state = NewState();
        state.TryClaim(T0, Guid.NewGuid(), "periodic", 0, out _);
        state.Complete(state.ScanId!.Value, T0.AddMinutes(1), "Completed", null, "[]");

        Assert.Equal(T0.AddMinutes(DiscoveryAgentState.DefaultIntervalMinutes), state.NextScanDueAt);
        Assert.Null(state.DueReason(T0.AddMinutes(DiscoveryAgentState.DefaultIntervalMinutes - 1), "periodic"));
        Assert.Equal("periodic", state.DueReason(T0.AddMinutes(DiscoveryAgentState.DefaultIntervalMinutes), "periodic"));

        var jittered = NewState();
        jittered.TryClaim(T0, Guid.NewGuid(), "periodic", 1, out _);
        Assert.Equal(T0.AddMinutes(DiscoveryAgentState.DefaultIntervalMinutes * 1.10), jittered.NextScanDueAt);
    }

    [Fact]
    public void Manual_requests_are_throttled_and_a_disabled_agent_is_never_due()
    {
        var state = NewState();
        state.TryClaim(T0, Guid.NewGuid(), "periodic", 0, out _);
        state.Complete(state.ScanId!.Value, T0.AddSeconds(10), "Completed", null, "[]");

        Assert.False(state.CanRequestManual(T0.AddSeconds(20)));
        Assert.Equal(TimeSpan.FromSeconds(10), state.ManualRetryAfter(T0.AddSeconds(20)));
        Assert.True(state.CanRequestManual(T0.AddSeconds(31)));

        state.RequestScan("manual", null, T0.AddMinutes(1));
        state.ChangeSettings(false, 60, true, T0.AddMinutes(2));
        Assert.Null(state.DueReason(T0.AddDays(30), "periodic"));
        Assert.Equal(DiscoveryScanState.Idle, state.StateAt(T0.AddMinutes(3)));

        state.ChangeSettings(true, 60, true, T0.AddMinutes(4));
        Assert.Equal("manual", state.DueReason(T0.AddMinutes(4), "periodic"));
    }

    [Theory]
    [InlineData(DiscoveryAgentState.MinimumIntervalMinutes - 1)]
    [InlineData(DiscoveryAgentState.MaximumIntervalMinutes + 1)]
    public void An_interval_outside_the_supported_range_is_refused(int minutes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NewState().ChangeSettings(true, minutes, true, T0));
}
