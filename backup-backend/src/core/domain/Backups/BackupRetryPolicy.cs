namespace BackupPlatform.Domain.Backups;

public enum BackupBackoffStrategy
{
    /// <summary>Every retry waits <see cref="BackupRetryPolicy.InitialDelay"/>.</summary>
    Fixed = 1,
    /// <summary>The wait doubles after each failed attempt and never exceeds <see cref="BackupRetryPolicy.MaxDelay"/>.</summary>
    Exponential = 2
}

/// <summary>
/// How a logical execution (one <see cref="BackupRun"/>) is retried. <see cref="MaxAttempts"/> is the TOTAL number of attempts,
/// including the first one: 1 disables retries, 3 allows the first attempt plus two retries. The policy is snapshotted on the run
/// when it is created, so editing a job never changes an execution in flight.
/// </summary>
public sealed record BackupRetryPolicy
{
    public const int MaximumAttemptsLimit = 10;
    public static readonly TimeSpan MinimumDelayLimit = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumDelayLimit = TimeSpan.FromHours(24);

    public static BackupRetryPolicy Default { get; } = new();

    public int MaxAttempts { get; init; } = 3;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(10);
    public BackupBackoffStrategy Backoff { get; init; } = BackupBackoffStrategy.Exponential;

    /// <summary>
    /// Fraction (0..1) by which a computed delay may be shortened at random to de-synchronise retries. Jitter only ever
    /// shortens, so <see cref="MaxDelay"/> stays a hard upper bound.
    /// </summary>
    public double Jitter { get; init; } = 0.2;

    public BackupRetryPolicy Validate()
    {
        if (MaxAttempts is < 1 or > MaximumAttemptsLimit) throw new ArgumentException("MaxAttempts is out of range.");
        if (InitialDelay < MinimumDelayLimit || InitialDelay > MaximumDelayLimit) throw new ArgumentException("InitialDelay is out of range.");
        if (MaxDelay < InitialDelay || MaxDelay > MaximumDelayLimit) throw new ArgumentException("MaxDelay must be between InitialDelay and the upper limit.");
        if (!Enum.IsDefined(Backoff)) throw new ArgumentException("Unknown backoff strategy.");
        if (!double.IsFinite(Jitter) || Jitter is < 0 or > 1) throw new ArgumentException("Jitter must be between 0 and 1.");
        return this;
    }

    public bool HasAttemptsRemaining(int attemptsUsed) => attemptsUsed < MaxAttempts;

    /// <summary>The wait after attempt <paramref name="failedAttemptNumber"/> failed. <paramref name="unitRandom"/> is a sample in [0,1).</summary>
    public TimeSpan DelayAfter(int failedAttemptNumber, double unitRandom)
    {
        if (failedAttemptNumber < 1) throw new ArgumentOutOfRangeException(nameof(failedAttemptNumber));
        if (!double.IsFinite(unitRandom) || unitRandom is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(unitRandom));
        var ceiling = MaxDelay.TotalSeconds;
        var seconds = Backoff == BackupBackoffStrategy.Fixed
            ? InitialDelay.TotalSeconds
            // Math.Pow overflows to infinity for absurd exponents, which Min then bounds.
            : Math.Min(ceiling, InitialDelay.TotalSeconds * Math.Pow(2, failedAttemptNumber - 1));
        seconds = Math.Min(ceiling, seconds) * (1 - Jitter * unitRandom);
        return TimeSpan.FromSeconds(seconds);
    }
}
