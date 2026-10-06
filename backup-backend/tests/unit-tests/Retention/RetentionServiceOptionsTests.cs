using BackupPlatform.Application.Backups;

namespace BackupPlatform.UnitTests.Retention;

/// <summary>The numbers every retention worker obeys: a delete that keeps failing backs off to a bound and never spins, and a bad setting is refused at start.</summary>
public sealed class RetentionServiceOptionsTests
{
    [Fact]
    public void TheRetryDelayDoublesPerFailureAndStopsAtTheCap()
    {
        var options = new BackupRetentionOptions { DeleteRetryInitialDelay = TimeSpan.FromMinutes(1), DeleteRetryMaxDelay = TimeSpan.FromMinutes(20) };

        Assert.Equal(
            [1.0, 2.0, 4.0, 8.0, 16.0, 20.0, 20.0],
            Enumerable.Range(1, 7).Select(attempt => options.DeleteRetryDelay(attempt).TotalMinutes));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(1)]
    public void TheFirstFailureAndNonsenseAttemptNumbersWaitTheInitialDelay(int attempts)
    {
        var options = new BackupRetentionOptions { DeleteRetryInitialDelay = TimeSpan.FromSeconds(30) };

        Assert.Equal(TimeSpan.FromSeconds(30), options.DeleteRetryDelay(attempts));
    }

    [Fact]
    public void TheRetryDelayIsNeverZeroAndNeverExceedsTheCapEvenForAbsurdAttemptCounts()
    {
        var options = new BackupRetentionOptions { DeleteRetryInitialDelay = TimeSpan.FromSeconds(1), DeleteRetryMaxDelay = TimeSpan.FromHours(1) };

        foreach (var attempts in new[] { 1, 2, 11, 12, 31, 32, 100, 10_000, int.MaxValue })
        {
            var delay = options.DeleteRetryDelay(attempts);
            Assert.InRange(delay, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
        }

        Assert.Equal(TimeSpan.FromHours(1), options.DeleteRetryDelay(int.MaxValue));
    }

    [Fact]
    public void TheDefaultsAreValid()
    {
        var options = new BackupRetentionOptions();

        Assert.Empty(options.Validate());
        options.ThrowIfInvalid();
    }

    public static TheoryData<string, BackupRetentionOptions> InvalidOptions => new()
    {
        { "no batch", new BackupRetentionOptions { BatchSize = 0 } },
        { "huge batch", new BackupRetentionOptions { BatchSize = 1001 } },
        { "no batches per cycle", new BackupRetentionOptions { MaxBatchesPerCycle = 0 } },
        { "sub-second scan interval", new BackupRetentionOptions { ScanInterval = TimeSpan.FromMilliseconds(10) } },
        { "lease too short to be a lease", new BackupRetentionOptions { DeleteLeaseDuration = TimeSpan.FromSeconds(1) } },
        { "zero initial retry delay (a hot loop)", new BackupRetentionOptions { DeleteRetryInitialDelay = TimeSpan.Zero } },
        { "retry cap below the initial delay", new BackupRetentionOptions { DeleteRetryInitialDelay = TimeSpan.FromMinutes(5), DeleteRetryMaxDelay = TimeSpan.FromMinutes(1) } },
        { "default keep below the minimum", new BackupRetentionOptions { DefaultKeepFor = TimeSpan.FromMinutes(5), MinimumKeepFor = TimeSpan.FromHours(1) } },
        { "default grace below the minimum", new BackupRetentionOptions { DefaultDeleteGracePeriod = TimeSpan.FromMinutes(5), MinimumDeleteGracePeriod = TimeSpan.FromHours(1) } },
        { "default keep beyond the representable maximum", new BackupRetentionOptions { DefaultKeepFor = TimeSpan.FromDays(4000) } },
        { "no orphan age", new BackupRetentionOptions { OrphanMinimumAge = TimeSpan.Zero } },
        { "no partial upload age", new BackupRetentionOptions { PartialUploadMinimumAge = TimeSpan.Zero } },
        { "no sweep deletions", new BackupRetentionOptions { MaxSweepDeletionsPerRun = 0 } },
        { "no occurrence history", new BackupRetentionOptions { ScheduleOccurrenceHistoryRetention = TimeSpan.Zero } },
        { "zero prune batch", new BackupRetentionOptions { ScheduleOccurrencePruneBatchSize = 0 } },
        { "huge prune batch", new BackupRetentionOptions { ScheduleOccurrencePruneBatchSize = 10_001 } }
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void ASettingThatWouldMisbehaveIsRefused(string reason, BackupRetentionOptions options)
    {
        Assert.NotEmpty(options.Validate());
        var refused = Assert.Throws<InvalidOperationException>(options.ThrowIfInvalid);
        Assert.Contains("Invalid backup retention options", refused.Message);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void RetentionMayBeDisabledByDefaultWithoutAMinimumKeep()
    {
        var options = new BackupRetentionOptions { DefaultRetentionEnabled = false, DefaultKeepFor = TimeSpan.FromMinutes(1) };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void AConfiguredNodeIdIsUsedTrimmedAndStable()
    {
        var options = new BackupRetentionOptions { NodeId = "  backend-1  " };

        Assert.Equal("backend-1", options.ResolveNodeId());
        Assert.Equal("backend-1", options.ResolveNodeId());
    }

    [Fact]
    public void WithoutAConfiguredNodeIdEveryProcessGetsItsOwnBoundedId()
    {
        var options = new BackupRetentionOptions();

        var first = options.ResolveNodeId();
        var second = options.ResolveNodeId();

        Assert.NotEqual(first, second);
        Assert.InRange(first.Length, 1, 128);
    }
}
