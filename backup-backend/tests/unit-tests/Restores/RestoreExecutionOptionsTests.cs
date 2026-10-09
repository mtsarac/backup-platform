using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;

namespace BackupPlatform.UnitTests;

public sealed class RestoreExecutionOptionsTests
{
    [Fact]
    public void DefaultsUseTheBackupLeaseAndConservativeExpiryAndSweepIntervals()
    {
        var options = new RestoreExecutionOptions();
        Assert.Equal(TimeSpan.FromSeconds(120), BackupOrchestrator.LeaseDuration);
        Assert.Equal(BackupOrchestrator.LeaseDuration, options.LeaseDuration);
        Assert.Equal(TimeSpan.FromHours(1), options.UnclaimedExpiry);
        Assert.Equal(TimeSpan.FromSeconds(15), options.SweepInterval);
        Assert.Empty(options.Validate());
        options.ThrowIfInvalid();
    }

    public static TheoryData<TimeSpan> InvalidLeaseDurations => new()
    {
        TimeSpan.Zero, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1), TimeSpan.FromHours(1) + TimeSpan.FromTicks(1)
    };

    public static TheoryData<TimeSpan> ValidLeaseDurations => new()
    {
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120), TimeSpan.FromHours(1)
    };

    [Theory]
    [MemberData(nameof(InvalidLeaseDurations))]
    public void ALeaseOutsideThirtySecondsToOneHourHasOneProblem(TimeSpan duration)
    {
        var options = new RestoreExecutionOptions { LeaseDuration = duration };
        Assert.Equal("LeaseDuration must be between 30 seconds and one hour.", Assert.Single(options.Validate()));
        Assert.Contains("LeaseDuration", Assert.Throws<InvalidOperationException>(options.ThrowIfInvalid).Message);
    }

    [Theory]
    [MemberData(nameof(ValidLeaseDurations))]
    public void TheInclusiveLeaseBoundsAreValid(TimeSpan duration)
    {
        var options = new RestoreExecutionOptions { LeaseDuration = duration };
        Assert.Empty(options.Validate());
        options.ThrowIfInvalid();
    }

    public static TheoryData<TimeSpan> InvalidUnclaimedExpiries => new()
    {
        TimeSpan.Zero, TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1), TimeSpan.FromDays(7) + TimeSpan.FromTicks(1)
    };

    public static TheoryData<TimeSpan> ValidUnclaimedExpiries => new()
    {
        TimeSpan.FromMinutes(1), TimeSpan.FromHours(1), TimeSpan.FromDays(7)
    };

    [Theory]
    [MemberData(nameof(InvalidUnclaimedExpiries))]
    public void AnUnclaimedExpiryOutsideOneMinuteToSevenDaysHasOneProblem(TimeSpan duration)
    {
        var options = new RestoreExecutionOptions { UnclaimedExpiry = duration };
        Assert.Equal("UnclaimedExpiry must be between one minute and seven days.", Assert.Single(options.Validate()));
        Assert.Contains("UnclaimedExpiry", Assert.Throws<InvalidOperationException>(options.ThrowIfInvalid).Message);
    }

    [Theory]
    [MemberData(nameof(ValidUnclaimedExpiries))]
    public void TheInclusiveUnclaimedExpiryBoundsAreValid(TimeSpan duration)
    {
        var options = new RestoreExecutionOptions { UnclaimedExpiry = duration };
        Assert.Empty(options.Validate());
        options.ThrowIfInvalid();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(999)]
    public void ASweepIntervalBelowOneSecondHasOneProblem(int milliseconds)
    {
        var options = new RestoreExecutionOptions { SweepInterval = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.Equal("SweepInterval must be at least one second.", Assert.Single(options.Validate()));
        Assert.Contains("SweepInterval", Assert.Throws<InvalidOperationException>(options.ThrowIfInvalid).Message);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(15000)]
    [InlineData(86400000)]
    public void ASweepIntervalOfAtLeastOneSecondIsValid(int milliseconds)
    {
        var options = new RestoreExecutionOptions { SweepInterval = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.Empty(options.Validate());
        options.ThrowIfInvalid();
    }

    [Fact]
    public void ValidationReportsAllProblemsAndThrowIfInvalidIncludesEveryOne()
    {
        var options = new RestoreExecutionOptions { LeaseDuration = TimeSpan.Zero, UnclaimedExpiry = TimeSpan.Zero, SweepInterval = TimeSpan.Zero };
        var problems = options.Validate();
        Assert.Equal(3, problems.Count);
        var exception = Assert.Throws<InvalidOperationException>(options.ThrowIfInvalid);
        Assert.StartsWith("Invalid restore execution options:", exception.Message);
        Assert.All(problems, problem => Assert.Contains(problem, exception.Message));
    }
}
