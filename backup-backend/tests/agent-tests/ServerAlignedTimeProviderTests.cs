using BackupPlatform.Agent.Core.Connectivity;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests;

public sealed class ServerAlignedTimeProviderTests
{
    private static readonly DateTimeOffset ServerNow = new(2026, 10, 18, 18, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(3)]
    [InlineData(-3)]
    public void A_machine_clock_hours_off_still_sees_a_server_lease_as_valid(int hoursAhead)
    {
        var local = new FakeTimeProvider(ServerNow.AddHours(hoursAhead));
        var clock = new ServerAlignedTimeProvider(local);
        var leaseExpiresAt = ServerNow.AddMinutes(2);
        Assert.Equal(hoursAhead > 0, leaseExpiresAt <= clock.GetUtcNow());

        Assert.True(clock.Observe(ServerNow, TimeSpan.Zero));

        Assert.True(leaseExpiresAt > clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromHours(-hoursAhead), clock.Offset);
    }

    [Fact]
    public void The_server_timestamp_is_centred_in_the_round_trip()
    {
        var clock = new ServerAlignedTimeProvider(new FakeTimeProvider(ServerNow));

        clock.Observe(ServerNow, TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(1), clock.Offset);
    }

    [Fact]
    public void A_slow_round_trip_is_ignored_and_keeps_the_previous_offset()
    {
        var clock = new ServerAlignedTimeProvider(new FakeTimeProvider(ServerNow));
        clock.Observe(ServerNow.AddMinutes(1), TimeSpan.Zero);
        var accepted = clock.Offset;

        var used = clock.Observe(ServerNow.AddHours(5), ServerAlignedTimeProvider.MaximumRoundTrip + TimeSpan.FromMilliseconds(1));

        Assert.False(used);
        Assert.Equal(accepted, clock.Offset);
    }

    [Fact]
    public void Only_the_wall_clock_shifts_while_timers_and_timestamps_stay_local()
    {
        var local = new FakeTimeProvider(ServerNow.AddHours(3));
        var clock = new ServerAlignedTimeProvider(local);
        clock.Observe(ServerNow, TimeSpan.Zero);
        var fired = 0;
        using var timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        var before = clock.GetTimestamp();

        local.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(1, fired);
        Assert.Equal(TimeSpan.FromSeconds(10), clock.GetElapsedTime(before));
        Assert.Equal(ServerNow.AddSeconds(10), clock.GetUtcNow());
        Assert.Equal(ServerNow.AddHours(3).AddSeconds(10), clock.Local.GetUtcNow());
    }
}
