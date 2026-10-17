using BackupPlatform.ManagedService;
using BackupPlatform.ManagedService.Subscriptions;

namespace BackupPlatform.UnitTests.ManagedService;

public sealed class EffectiveStorageLimitTests
{
    private const long Gb = 1_000_000_000;

    [Theory]
    [InlineData(5 * Gb)]
    [InlineData(null)]
    public void NoOverrideUsesThePlanLimitAndAnUnlimitedPlanIsUnlimited(long? plan)
    {
        var limit = EffectiveStorageLimit.Resolve(plan, StorageLimitOverride.None);
        Assert.Equal((plan is null, plan, EffectiveStorageLimitSource.Plan), (limit.IsUnlimited, limit.Bytes, limit.Source));
    }

    [Theory]
    [InlineData(5 * Gb, 20 * Gb)]
    [InlineData(20 * Gb, 1 * Gb)]
    [InlineData(null, 3 * Gb)]
    public void ALimitedOverrideWinsInBothDirections(long? plan, long overrideBytes)
    {
        var limit = EffectiveStorageLimit.Resolve(plan, StorageLimitOverride.Limited(overrideBytes));
        Assert.Equal((false, (long?)overrideBytes, EffectiveStorageLimitSource.Override), (limit.IsUnlimited, limit.Bytes, limit.Source));
    }

    [Theory]
    [InlineData(5 * Gb)]
    [InlineData(null)]
    public void AnUnlimitedOverrideWinsAndIsNotConfusedWithNoOverride(long? plan)
    {
        var limit = EffectiveStorageLimit.Resolve(plan, StorageLimitOverride.Unlimited);
        Assert.Equal((true, (long?)null, EffectiveStorageLimitSource.Override), (limit.IsUnlimited, limit.Bytes, limit.Source));
        Assert.NotEqual(StorageLimitOverride.None, StorageLimitOverride.Unlimited);
    }

    [Fact]
    public void TheDefaultOverrideIsNone() => Assert.Equal(StorageLimitOverride.None, default);

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(long.MaxValue)]
    public void OverridesRejectNonPositiveAndOutOfRangeBytes(long bytes) =>
        Assert.Throws<InvalidManagedServiceRequestException>(() => StorageLimitOverride.Limited(bytes));

    [Fact]
    public void StoredPairsMustBeConsistent()
    {
        Assert.Equal(StorageLimitOverride.Limited(Gb), StorageLimitOverride.From(StorageLimitOverrideKind.Limited, Gb));
        Assert.Equal(StorageLimitOverride.Unlimited, StorageLimitOverride.From(StorageLimitOverrideKind.Unlimited, null));
        Assert.Throws<ArgumentException>(() => StorageLimitOverride.From(StorageLimitOverrideKind.None, Gb));
        Assert.Throws<ArgumentException>(() => StorageLimitOverride.From(StorageLimitOverrideKind.Limited, null));
        Assert.Throws<ArgumentException>(() => StorageLimitOverride.From(StorageLimitOverrideKind.Unlimited, Gb));
    }
}
