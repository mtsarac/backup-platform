using BackupPlatform.ManagedService;
using BackupPlatform.ManagedService.Subscriptions;

namespace BackupPlatform.UnitTests.ManagedService;

public sealed class EffectiveStorageLimitTests
{
    private const long Gb = 1_000_000_000;

    [Fact]
    public void NoOverrideUsesThePlanLimit()
    {
        var limit = EffectiveStorageLimit.Resolve(5 * Gb, StorageLimitOverride.None);
        Assert.Equal((false, (long?)(5 * Gb), EffectiveStorageLimitSource.Plan), (limit.IsUnlimited, limit.Bytes, limit.Source));
    }

    [Fact]
    public void NoOverrideOnAnUnlimitedPlanIsUnlimited()
    {
        var limit = EffectiveStorageLimit.Resolve(null, StorageLimitOverride.None);
        Assert.Equal((true, (long?)null, EffectiveStorageLimitSource.Plan), (limit.IsUnlimited, limit.Bytes, limit.Source));
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
