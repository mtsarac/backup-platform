using BackupPlatform.Agent.Core.Backup;

namespace BackupPlatform.UnitTests.Backup;

public sealed class StagingUsageRegistryTests
{
    [Fact]
    public void ANameIsHeldUntilItsLeaseIsDisposed()
    {
        var registry = new StagingUsageRegistry();

        Assert.False(registry.IsHeld("a.bak"));
        var lease = registry.Hold("a.bak");
        Assert.True(registry.IsHeld("a.bak"));
        Assert.False(registry.IsHeld("b.bak"));

        lease.Dispose();
        Assert.False(registry.IsHeld("a.bak"));
    }

    [Fact]
    public void HoldsAreCountedSoTwoHoldsNeedTwoReleases()
    {
        var registry = new StagingUsageRegistry();
        var first = registry.Hold("a.bak");
        var second = registry.Hold("a.bak");

        first.Dispose();
        Assert.True(registry.IsHeld("a.bak"));

        second.Dispose();
        Assert.False(registry.IsHeld("a.bak"));
    }

    [Fact]
    public void DisposingALeaseTwiceReleasesItOnlyOnce()
    {
        var registry = new StagingUsageRegistry();
        var first = registry.Hold("a.bak");
        using var second = registry.Hold("a.bak");

        first.Dispose();
        first.Dispose();

        Assert.True(registry.IsHeld("a.bak"));
    }

    [Fact]
    public void NamesAreComparedIgnoringCaseBecauseSomeFileSystemsDo()
    {
        var registry = new StagingUsageRegistry();
        using var lease = registry.Hold("Sales_FULL.bak");

        Assert.True(registry.IsHeld("sales_full.bak"));
    }

    [Fact]
    public async Task ConcurrentHoldsAndReleasesBalanceOut()
    {
        var registry = new StagingUsageRegistry();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                registry.Hold("a.bak").Dispose();
            }
        })));

        Assert.False(registry.IsHeld("a.bak"));
    }

    [Fact]
    public void AnEmptyNameCannotBeHeld() => Assert.ThrowsAny<ArgumentException>(() => new StagingUsageRegistry().Hold(""));
}
