using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;

namespace BackupPlatform.UnitTests.ManagedService.Quota;

public sealed class QuotaStateResolverTests
{
    private const long Limit = 1_000_000_000;

    private static EffectiveStorageLimit Limited(long bytes) => EffectiveStorageLimit.Resolve(bytes, StorageLimitOverride.None);

    private static QuotaState Resolve(long used, long reserved = 0, long limit = Limit, int warning = 80, int critical = 90, QuotaEnforcementMode enforcement = QuotaEnforcementMode.BlockNewBackups) =>
        QuotaStateResolver.Resolve(Limited(limit), enforcement, warning, critical, used, reserved);

    [Theory]
    [InlineData(0, QuotaState.Normal)]
    [InlineData(1, QuotaState.Normal)]
    [InlineData(799_999_999, QuotaState.Normal)]
    [InlineData(800_000_000, QuotaState.Warning)]
    [InlineData(800_000_001, QuotaState.Warning)]
    [InlineData(899_999_999, QuotaState.Warning)]
    [InlineData(900_000_000, QuotaState.Critical)]
    [InlineData(900_000_001, QuotaState.Critical)]
    [InlineData(999_999_999, QuotaState.Critical)]
    [InlineData(1_000_000_000, QuotaState.OverQuota)]
    [InlineData(1_000_000_001, QuotaState.OverQuota)]
    [InlineData(long.MaxValue, QuotaState.OverQuota)]
    public void DefaultThresholdsChangeStateExactlyAtEightyNinetyAndOneHundredPercent(long used, QuotaState expected) =>
        Assert.Equal(expected, Resolve(used));

    [Theory]
    [InlineData(499_999_999, QuotaState.Normal)]
    [InlineData(500_000_000, QuotaState.Warning)]
    [InlineData(749_999_999, QuotaState.Warning)]
    [InlineData(750_000_000, QuotaState.Critical)]
    [InlineData(999_999_999, QuotaState.Critical)]
    [InlineData(1_000_000_000, QuotaState.OverQuota)]
    public void ThresholdsComeFromThePolicy(long used, QuotaState expected) =>
        Assert.Equal(expected, Resolve(used, warning: 50, critical: 75));

    [Theory]
    [InlineData(0, 799_999_999, QuotaState.Normal)]
    [InlineData(0, 800_000_000, QuotaState.Warning)]
    [InlineData(500_000_000, 300_000_000, QuotaState.Warning)]
    [InlineData(500_000_000, 400_000_000, QuotaState.Critical)]
    [InlineData(500_000_000, 499_999_999, QuotaState.Critical)]
    [InlineData(500_000_000, 500_000_000, QuotaState.OverQuota)]
    [InlineData(0, 1_000_000_000, QuotaState.OverQuota)]
    public void ReservedBytesCountTowardsTheState(long used, long reserved, QuotaState expected) =>
        Assert.Equal(expected, Resolve(used, reserved));

    [Theory]
    [InlineData(2, QuotaState.Normal)]
    [InlineData(7, QuotaState.Normal)]
    [InlineData(8, QuotaState.Warning)]
    [InlineData(9, QuotaState.Critical)]
    [InlineData(10, QuotaState.OverQuota)]
    public void PercentagesAreExactOnASmallLimit(long used, QuotaState expected) =>
        Assert.Equal(expected, Resolve(used, limit: 10));

    [Theory]
    [InlineData(2, QuotaState.Normal)]
    [InlineData(3, QuotaState.OverQuota)]
    public void ATinyLimitIsNotRoundedAway(long used, QuotaState expected) =>
        Assert.Equal(expected, Resolve(used, limit: 3, warning: 99, critical: 100));

    [Fact]
    public void ANullLimitIsUnlimited() =>
        Assert.Equal(QuotaState.Unlimited, QuotaStateResolver.Resolve(null, QuotaEnforcementMode.BlockNewBackups, 80, 90, long.MaxValue, long.MaxValue));

    [Fact]
    public void AnUnlimitedLimitIsUnlimitedWhateverTheUsage()
    {
        var unlimited = EffectiveStorageLimit.Resolve(Limit, StorageLimitOverride.Unlimited);
        Assert.Equal(QuotaState.Unlimited, QuotaStateResolver.Resolve(unlimited, QuotaEnforcementMode.BlockNewBackups, 80, 90, long.MaxValue, 0));
        Assert.Equal(QuotaState.Unlimited, QuotaStateResolver.Resolve(EffectiveStorageLimit.Resolve(null, StorageLimitOverride.None), QuotaEnforcementMode.WarnOnly, 80, 90, 5, 5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(850_000_000)]
    [InlineData(1_000_000_000)]
    [InlineData(long.MaxValue)]
    public void EnforcementNoneReportsUnlimitedEvenWithALimit(long used) =>
        Assert.Equal(QuotaState.Unlimited, Resolve(used, enforcement: QuotaEnforcementMode.None));

    [Theory]
    [InlineData(QuotaEnforcementMode.BlockNewBackups)]
    [InlineData(QuotaEnforcementMode.WarnOnly)]
    public void WarnOnlyStillReportsTheRealState(QuotaEnforcementMode enforcement) =>
        Assert.Equal(QuotaState.OverQuota, Resolve(2 * Limit, enforcement: enforcement));

    [Fact]
    public void ALimitMarkedLimitedButWithoutBytesIsUnlimited() =>
        Assert.Equal(QuotaState.Unlimited, QuotaStateResolver.Resolve(new EffectiveStorageLimit(false, null, EffectiveStorageLimitSource.Plan), QuotaEnforcementMode.BlockNewBackups, 80, 90, 1, 0));

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void AZeroOrNegativeLimitIsOverQuotaWithoutDividingByZero(long limit) =>
        Assert.Equal(QuotaState.OverQuota, QuotaStateResolver.Resolve(new EffectiveStorageLimit(false, limit, EffectiveStorageLimitSource.Override), QuotaEnforcementMode.BlockNewBackups, 80, 90, 0, 0));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(-100, -100)]
    public void NegativeUsageIsTreatedAsZero(long used, long reserved) =>
        Assert.Equal(QuotaState.Normal, Resolve(used, reserved));

    [Fact]
    public void NegativeReservationNeverCancelsUsage() =>
        Assert.Equal(QuotaState.OverQuota, Resolve(Limit, -Limit));

    [Theory]
    [InlineData(long.MaxValue, 0, QuotaState.OverQuota)]
    [InlineData(long.MaxValue, long.MaxValue, QuotaState.OverQuota)]
    [InlineData(long.MaxValue - 1, 0, QuotaState.Critical)]
    [InlineData(long.MaxValue / 2, 0, QuotaState.Normal)]
    [InlineData(long.MaxValue / 10 * 8 + 10, 0, QuotaState.Warning)]
    [InlineData(long.MaxValue / 10 * 9 + 10, 0, QuotaState.Critical)]
    public void HugeValuesDoNotOverflow(long used, long reserved, QuotaState expected) =>
        Assert.Equal(expected, Resolve(used, reserved, limit: long.MaxValue));

    [Theory]
    [InlineData(1, 2, 3)]
    [InlineData(0, 0, 0)]
    [InlineData(-5, 3, 3)]
    [InlineData(5, -3, 5)]
    [InlineData(long.MaxValue, 1, long.MaxValue)]
    [InlineData(long.MaxValue, long.MaxValue, long.MaxValue)]
    [InlineData(long.MaxValue - 1, 1, long.MaxValue)]
    [InlineData(long.MaxValue - 1, 2, long.MaxValue)]
    public void CommittedIsUsedPlusReservedClampedAndNeverNegative(long used, long reserved, long expected) =>
        Assert.Equal(expected, QuotaStateResolver.Committed(used, reserved));
}
