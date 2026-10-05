using BackupPlatform.Domain.Backups;

namespace BackupPlatform.UnitTests;

public sealed class BackupRetryPolicyTests
{
    private static BackupRetryPolicy Policy(
        int maxAttempts = 5, double initial = 10, double max = 600, BackupBackoffStrategy backoff = BackupBackoffStrategy.Exponential, double jitter = 0) => new()
    {
        MaxAttempts = maxAttempts,
        InitialDelay = TimeSpan.FromSeconds(initial),
        MaxDelay = TimeSpan.FromSeconds(max),
        Backoff = backoff,
        Jitter = jitter
    };

    [Fact]
    public void DefaultPolicyIsValidAndAllowsThreeTotalAttempts()
    {
        var policy = BackupRetryPolicy.Default;
        Assert.Same(policy, policy.Validate());
        Assert.Equal(3, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.InitialDelay);
        Assert.Equal(TimeSpan.FromMinutes(10), policy.MaxDelay);
        Assert.Equal(BackupBackoffStrategy.Exponential, policy.Backoff);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(10, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(11, false)]
    public void MaxAttemptsMustBeBetweenOneAndTen(int maxAttempts, bool valid)
    {
        var policy = Policy(maxAttempts);
        if (valid) policy.Validate();
        else Assert.Throws<ArgumentException>(() => policy.Validate());
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 86_400, true)]
    [InlineData(86_400, 86_400, true)]
    [InlineData(0.999, 60, false)]
    [InlineData(0, 60, false)]
    [InlineData(-5, 60, false)]
    [InlineData(86_401, 86_401, false)]
    [InlineData(10, 9.999, false)]
    [InlineData(10, 86_401, false)]
    public void DelaysMustBeBoundedAndMaxDelayMustNotBeBelowInitialDelay(double initialSeconds, double maxSeconds, bool valid)
    {
        var policy = Policy(initial: initialSeconds, max: maxSeconds);
        if (valid) policy.Validate();
        else Assert.Throws<ArgumentException>(() => policy.Validate());
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(1.0, true)]
    [InlineData(0.5, true)]
    [InlineData(-0.01, false)]
    [InlineData(1.01, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    public void JitterMustBeAFiniteFractionBetweenZeroAndOne(double jitter, bool valid)
    {
        var policy = Policy(jitter: jitter);
        if (valid) policy.Validate();
        else Assert.Throws<ArgumentException>(() => policy.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void UnknownBackoffStrategyIsRejected(int backoff) =>
        Assert.Throws<ArgumentException>(() => Policy(backoff: (BackupBackoffStrategy)backoff).Validate());

    [Theory]
    [InlineData(3, 0, true)]
    [InlineData(3, 1, true)]
    [InlineData(3, 2, true)]
    [InlineData(3, 3, false)]
    [InlineData(3, 4, false)]
    [InlineData(1, 0, true)]
    [InlineData(1, 1, false)]
    [InlineData(10, 9, true)]
    [InlineData(10, 10, false)]
    public void MaxAttemptsCountsTotalAttemptsIncludingTheFirst(int maxAttempts, int attemptsUsed, bool retryAllowed) =>
        Assert.Equal(retryAllowed, Policy(maxAttempts).HasAttemptsRemaining(attemptsUsed));

    [Fact]
    public void SingleAttemptPolicyNeverAllowsARetryAfterTheFirstAttempt()
    {
        var policy = Policy(1);
        Assert.False(policy.HasAttemptsRemaining(1));
        Assert.False(policy.HasAttemptsRemaining(2));
    }

    [Fact]
    public void ExponentialBackoffDoublesPerFailedAttemptAndIsCappedAtMaxDelay()
    {
        var policy = Policy(initial: 10, max: 100);
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayAfter(1, 0));
        Assert.Equal(TimeSpan.FromSeconds(20), policy.DelayAfter(2, 0));
        Assert.Equal(TimeSpan.FromSeconds(40), policy.DelayAfter(3, 0));
        Assert.Equal(TimeSpan.FromSeconds(80), policy.DelayAfter(4, 0));
        Assert.Equal(TimeSpan.FromSeconds(100), policy.DelayAfter(5, 0));
        Assert.Equal(TimeSpan.FromSeconds(100), policy.DelayAfter(6, 0));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(1_000)]
    [InlineData(int.MaxValue)]
    public void HugeAttemptNumbersSaturateAtMaxDelayWithoutOverflow(int failedAttemptNumber)
    {
        var policy = Policy(initial: 30, max: 600);
        Assert.Equal(TimeSpan.FromSeconds(600), policy.DelayAfter(failedAttemptNumber, 0));
    }

    [Fact]
    public void FixedBackoffIsConstantRegardlessOfAttemptNumber()
    {
        var policy = Policy(initial: 15, max: 600, backoff: BackupBackoffStrategy.Fixed);
        foreach (var attempt in new[] { 1, 2, 3, 9, 1_000 })
            Assert.Equal(TimeSpan.FromSeconds(15), policy.DelayAfter(attempt, 0));
    }

    [Fact]
    public void JitterOnlyShortensTheDelayAndNeverBeyondItsFraction()
    {
        var policy = Policy(initial: 100, max: 1_000, jitter: 0.25);
        // attempt 2: base 200s; sample 0 keeps it, sample 1 removes the whole jitter fraction.
        Assert.Equal(TimeSpan.FromSeconds(200), policy.DelayAfter(2, 0));
        Assert.Equal(TimeSpan.FromSeconds(150), policy.DelayAfter(2, 1));
        Assert.Equal(TimeSpan.FromSeconds(175), policy.DelayAfter(2, 0.5));
        var previous = policy.DelayAfter(2, 0);
        for (var sample = 0.05; sample <= 1.0; sample += 0.05)
        {
            var delay = policy.DelayAfter(2, sample);
            Assert.True(delay <= previous, "a larger sample never lengthens the delay");
            Assert.InRange(delay, TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(200));
            previous = delay;
        }
    }

    [Theory]
    [InlineData(BackupBackoffStrategy.Exponential)]
    [InlineData(BackupBackoffStrategy.Fixed)]
    public void JitterNeverPushesTheDelayAboveMaxDelay(BackupBackoffStrategy backoff)
    {
        var policy = Policy(initial: 300, max: 400, backoff: backoff, jitter: 1);
        foreach (var attempt in new[] { 1, 2, 3, 50 })
            foreach (var sample in new[] { 0.0, 0.3, 0.999, 1.0 })
            {
                var delay = policy.DelayAfter(attempt, sample);
                Assert.True(delay <= policy.MaxDelay);
                Assert.True(delay >= TimeSpan.Zero);
            }
    }

    [Fact]
    public void DelayIsDeterministicForAGivenSample()
    {
        var policy = Policy(jitter: 0.4);
        Assert.Equal(policy.DelayAfter(3, 0.37), policy.DelayAfter(3, 0.37));
        Assert.NotEqual(policy.DelayAfter(3, 0.1), policy.DelayAfter(3, 0.9));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void DelayRejectsFailedAttemptNumbersBelowOne(int failedAttemptNumber) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy().DelayAfter(failedAttemptNumber, 0));

    [Theory]
    [InlineData(-0.001)]
    [InlineData(1.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void DelayRejectsRandomSamplesOutsideTheUnitInterval(double unitRandom) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy().DelayAfter(1, unitRandom));
}
