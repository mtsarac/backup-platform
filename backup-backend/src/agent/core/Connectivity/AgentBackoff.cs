namespace BackupPlatform.Agent.Core.Connectivity;

/// <summary>
/// The one delay calculator of the connectivity layer. Pure apart from the injected random source: failure delays use
/// exponential growth from a base delay, capped, with full jitter; periodic intervals only ever get stretched by a small
/// jitter, never shortened, so a configured interval is always a lower bound.
/// </summary>
public sealed class AgentBackoff
{
    /// <summary>Fraction of an interval that periodic jitter may add on top.</summary>
    public const double IntervalJitterFraction = 0.1;

    private readonly Func<double> _random;

    /// <param name="random">Returns values in [0, 1); injectable so tests are deterministic.</param>
    public AgentBackoff(TimeSpan baseDelay, TimeSpan maxDelay, Func<double>? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, baseDelay);
        BaseDelay = baseDelay;
        MaxDelay = maxDelay;
        _random = random ?? Random.Shared.NextDouble;
    }

    public TimeSpan BaseDelay { get; }

    public TimeSpan MaxDelay { get; }

    /// <summary>The exclusive upper bound of <see cref="GetDelay"/> for a zero-based retry count: <c>min(max, base * 2^retry)</c>.</summary>
    public TimeSpan GetCeiling(int retryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retryCount);
        // 2^retry overflows a double only after 1000+ doublings; the cap is reached long before.
        var scaled = BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(retryCount, 62));
        return scaled >= MaxDelay.TotalMilliseconds ? MaxDelay : TimeSpan.FromMilliseconds(scaled);
    }

    /// <summary>Full-jitter delay: uniformly distributed in <c>[0, GetCeiling(retryCount))</c>.</summary>
    public TimeSpan GetDelay(int retryCount) => GetCeiling(retryCount) * Math.Clamp(_random(), 0d, 1d);

    /// <summary>Returns <paramref name="interval"/> plus up to <see cref="IntervalJitterFraction"/> of it.</summary>
    public TimeSpan AddJitter(TimeSpan interval) => interval + (interval * (IntervalJitterFraction * Math.Clamp(_random(), 0d, 1d)));
}
