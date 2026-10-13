using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.UnitTests.Storage;

public sealed class StorageObjectKeyTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("tenant-a/mssql/sales/sales_full.bak")]
    [InlineData("A.b-c_d=e+f@g")]
    [InlineData(".hidden")]
    [InlineData("a.b.c/d.e")]
    [InlineData("2026-10-05T12=00=00Z.bak")]
    public void ValidKeysAreAccepted(string value)
    {
        var key = new StorageObjectKey(value);

        Assert.Equal(value, key.Value);
        Assert.Equal(value, key.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("../a")]
    [InlineData("a/..")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("/")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("\\a")]
    [InlineData("C:/x")]
    [InlineData("C:x")]
    [InlineData("a/C:")]
    [InlineData("a:b")]
    [InlineData("file.bak:stream")]
    [InlineData("file.bak::$DATA")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("a/NUL")]
    [InlineData("a/prn.bak")]
    [InlineData("COM1.bak")]
    [InlineData("lpt9")]
    [InlineData("a.")]
    [InlineData("a/b.")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a%2e%2e")]
    [InlineData("ü")]
    [InlineData("日本語")]
    [InlineData("a/é")]
    public void InvalidKeysAreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => new StorageObjectKey(value));
        Assert.False(StorageObjectKey.TryCreate(value, out var key));
        Assert.Null(key);
    }

    [Fact]
    public void NullIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new StorageObjectKey(null!));
        Assert.False(StorageObjectKey.TryCreate(null, out var key));
        Assert.Null(key);
    }

    [Fact]
    public void SegmentLengthBoundaryIs128()
    {
        Assert.Equal(128, new StorageObjectKey(new string('a', 128)).Value.Length);
        Assert.Throws<ArgumentException>(() => new StorageObjectKey(new string('a', 129)));
        Assert.Throws<ArgumentException>(() => new StorageObjectKey("ok/" + new string('a', 129)));
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void KeyLengthPreservesOldScopesAndBoundsRepairScopes(int length, bool accepted)
    {
        var segments = length / (StorageObjectKey.MaxSegmentLength + 1);
        var remainder = length - segments * (StorageObjectKey.MaxSegmentLength + 1);
        var value = string.Join('/', Enumerable.Repeat(new string('a', StorageObjectKey.MaxSegmentLength), segments).Append(new string('b', remainder)));

        Assert.Equal(accepted, StorageObjectKey.TryCreate(value, out var key));
        if (accepted)
            Assert.Equal(value, key!.Value);
        else
            Assert.Throws<ArgumentException>(() => new StorageObjectKey(value));
    }

    [Fact]
    public void SegmentsAndFileNameFollowTheSeparator()
    {
        var key = new StorageObjectKey("tenant/mssql/sales_full.bak");

        Assert.Equal(["tenant", "mssql", "sales_full.bak"], key.Segments);
        Assert.Equal("sales_full.bak", key.FileName);
        Assert.Equal("single", new StorageObjectKey("single").FileName);
        Assert.Equal(["single"], new StorageObjectKey("single").Segments);
    }

    [Fact]
    public void CombineAppendsAValidatedSegment()
    {
        var prefix = new StorageObjectKey("tenant/job-1");

        Assert.Equal("tenant/job-1/sales.bak", prefix.Combine("sales.bak").Value);
        Assert.Throws<ArgumentException>(() => prefix.Combine(".."));
        Assert.Throws<ArgumentException>(() => prefix.Combine(""));
        Assert.Throws<ArgumentException>(() => prefix.Combine("a\\b"));
        Assert.Throws<ArgumentException>(() => prefix.Combine("CON"));
    }

    [Fact]
    public void KeysWithTheSameValueAreEqual()
    {
        Assert.Equal(new StorageObjectKey("a/b"), new StorageObjectKey("a/b"));
        Assert.NotEqual(new StorageObjectKey("a/b"), new StorageObjectKey("a/B"));
    }

    [Fact]
    public void LocationIsProviderPlusKeyAndNeverAPath()
    {
        var location = new StorageLocation(StorageProviderId.Central, new StorageObjectKey("t/x.bak"));

        Assert.Equal("central:t/x.bak", location.ToString());
    }
}
