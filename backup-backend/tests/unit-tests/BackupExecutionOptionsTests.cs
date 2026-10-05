using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests;

public sealed class BackupExecutionOptionsTests
{
    [Fact]
    public void DefaultsAreConservativeAndValid()
    {
        var options = new BackupExecutionOptions();
        Assert.Equal(20, options.GlobalMaxConcurrentBackups);
        Assert.Equal(5, options.TenantMaxConcurrentBackups);
        Assert.Equal(1, options.AgentMaxConcurrentBackups);
        Assert.Empty(options.TenantOverrides);
        Assert.Empty(options.Validate());
        options.ThrowIfInvalid();
    }

    [Theory]
    [InlineData(nameof(BackupExecutionOptions.GlobalMaxConcurrentBackups))]
    [InlineData(nameof(BackupExecutionOptions.TenantMaxConcurrentBackups))]
    [InlineData(nameof(BackupExecutionOptions.AgentMaxConcurrentBackups))]
    [InlineData(nameof(BackupExecutionOptions.RetryScanBatchSize))]
    public void LimitsBelowOneAreRejectedAndOneIsAccepted(string member)
    {
        foreach (var bad in new[] { 0, -1, int.MinValue })
        {
            var problems = With(member, bad).Validate();
            Assert.Single(problems);
            Assert.Contains(member, problems[0]);
            Assert.Throws<InvalidOperationException>(() => With(member, bad).ThrowIfInvalid());
        }
        Assert.Empty(With(member, 1).Validate());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(999, false)]
    [InlineData(1_000, true)]
    [InlineData(5_000, true)]
    public void RetryScanIntervalMustBeAtLeastOneSecond(int milliseconds, bool valid)
    {
        var options = new BackupExecutionOptions { RetryScanInterval = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.Equal(valid, options.Validate().Count == 0);
    }

    [Fact]
    public void NegativeRetryScanIntervalIsRejected() =>
        Assert.NotEmpty(new BackupExecutionOptions { RetryScanInterval = TimeSpan.FromSeconds(-5) }.Validate());

    [Fact]
    public void TenantOverridesRequireGuidKeysAndPositiveLimits()
    {
        var tenant = Guid.NewGuid().ToString();
        Assert.Empty(new BackupExecutionOptions { TenantOverrides = { [tenant] = 1 } }.Validate());
        Assert.Single(new BackupExecutionOptions { TenantOverrides = { ["not-a-guid"] = 2 } }.Validate());
        Assert.Single(new BackupExecutionOptions { TenantOverrides = { [""] = 2 } }.Validate());
        Assert.Single(new BackupExecutionOptions { TenantOverrides = { [tenant] = 0 } }.Validate());
        Assert.Single(new BackupExecutionOptions { TenantOverrides = { [tenant] = -3 } }.Validate());
        // Both defects in one entry, and several defective entries, are all reported.
        Assert.Equal(2, new BackupExecutionOptions { TenantOverrides = { ["nope"] = 0 } }.Validate().Count);
        var several = new BackupExecutionOptions { TenantOverrides = { ["a"] = 1, [Guid.NewGuid().ToString()] = 0 } };
        Assert.Equal(2, several.Validate().Count);
        Assert.Throws<InvalidOperationException>(() => several.ThrowIfInvalid());
    }

    [Fact]
    public void TenantLimitReturnsTheOverrideForThatTenantAndTheDefaultForOthers()
    {
        var special = TenantId.New();
        var other = TenantId.New();
        var options = new BackupExecutionOptions
        {
            TenantMaxConcurrentBackups = 4,
            TenantOverrides = { [special.Value.ToString()] = 9 }
        };
        Assert.Equal(9, options.TenantLimit(special));
        Assert.Equal(4, options.TenantLimit(other));
    }

    [Fact]
    public void TenantOverrideMayLowerTheLimitBelowTheDefault()
    {
        var tenant = TenantId.New();
        var options = new BackupExecutionOptions { TenantOverrides = { [tenant.Value.ToString()] = 1 } };
        Assert.Equal(1, options.TenantLimit(tenant));
    }

    [Theory]
    [InlineData("D", false)]
    [InlineData("N", false)]
    [InlineData("B", true)]
    [InlineData("D", true)]
    public void AnOverrideKeyThatValidationAcceptsIsActuallyHonouredWhateverItsGuidFormatOrCasing(string format, bool upper)
    {
        var tenant = TenantId.New();
        var key = tenant.Value.ToString(format);
        if (upper) key = key.ToUpperInvariant();
        var options = new BackupExecutionOptions { TenantMaxConcurrentBackups = 5, TenantOverrides = { [key] = 2 } };
        // Either validation refuses the spelling at startup, or the limit applies. Silently ignoring a validated override is the bug.
        if (options.Validate().Count == 0) Assert.Equal(2, options.TenantLimit(tenant));
    }

    private static BackupExecutionOptions With(string member, int value) => member switch
    {
        nameof(BackupExecutionOptions.GlobalMaxConcurrentBackups) => new() { GlobalMaxConcurrentBackups = value },
        nameof(BackupExecutionOptions.TenantMaxConcurrentBackups) => new() { TenantMaxConcurrentBackups = value },
        nameof(BackupExecutionOptions.AgentMaxConcurrentBackups) => new() { AgentMaxConcurrentBackups = value },
        nameof(BackupExecutionOptions.RetryScanBatchSize) => new() { RetryScanBatchSize = value },
        _ => throw new ArgumentOutOfRangeException(nameof(member))
    };
}
