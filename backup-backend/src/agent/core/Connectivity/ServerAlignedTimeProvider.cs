namespace BackupPlatform.Agent.Core.Connectivity;

/// <summary>
/// The agent's wall clock, aligned to the backend's. Leases and other deadlines are absolute backend timestamps, so a machine whose clock
/// or time zone is wrong would drop every task as already expired. Timers and monotonic timestamps stay local: only <see cref="GetUtcNow"/> shifts.
/// Local file timestamps must be compared with <see cref="Local"/>, not with this clock.
/// </summary>
public sealed class ServerAlignedTimeProvider(TimeProvider local) : TimeProvider
{
    /// <summary>A slower heartbeat cannot be centred on the server's timestamp precisely enough to trust.</summary>
    public static readonly TimeSpan MaximumRoundTrip = TimeSpan.FromSeconds(5);

    private long _offsetTicks;

    /// <summary>The unadjusted clock, for comparisons against timestamps produced by this machine (file times).</summary>
    public TimeProvider Local => local;

    /// <summary>Backend time minus local time; zero until the first heartbeat is observed.</summary>
    public TimeSpan Offset => TimeSpan.FromTicks(Volatile.Read(ref _offsetTicks));

    public override DateTimeOffset GetUtcNow() => local.GetUtcNow().AddTicks(Volatile.Read(ref _offsetTicks));

    public override TimeZoneInfo LocalTimeZone => local.LocalTimeZone;

    public override long TimestampFrequency => local.TimestampFrequency;

    public override long GetTimestamp() => local.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        local.CreateTimer(callback, state, dueTime, period);

    /// <summary>Re-estimates the offset from a backend timestamp taken somewhere inside a request that took <paramref name="roundTrip"/>.</summary>
    /// <returns>False when the sample was too slow to use and the offset was left unchanged.</returns>
    public bool Observe(DateTimeOffset serverTime, TimeSpan roundTrip)
    {
        if (roundTrip < TimeSpan.Zero || roundTrip > MaximumRoundTrip)
        {
            return false;
        }
        var localAtServer = local.GetUtcNow() - roundTrip / 2;
        Volatile.Write(ref _offsetTicks, (serverTime - localAtServer).Ticks);
        return true;
    }
}
