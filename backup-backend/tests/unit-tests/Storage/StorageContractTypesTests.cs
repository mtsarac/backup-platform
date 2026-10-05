using System.Security.Cryptography;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.UnitTests.Storage;

public sealed class StorageContractTypesTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Theory]
    [InlineData("local")]
    [InlineData("central")]
    [InlineData("s3-eu-1")]
    [InlineData("a")]
    public void ProviderIdsAreAccepted(string value) => Assert.Equal(value, new StorageProviderId(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData("Local")]
    [InlineData("local_disk")]
    [InlineData("local disk")]
    [InlineData("local/disk")]
    [InlineData("yerel-ö")]
    public void InvalidProviderIdsAreRejected(string value) => Assert.Throws<ArgumentException>(() => new StorageProviderId(value));

    [Fact]
    public void ProviderIdLengthBoundaryIs32()
    {
        Assert.Equal(32, new StorageProviderId(new string('a', 32)).Value.Length);
        Assert.Throws<ArgumentException>(() => new StorageProviderId(new string('a', 33)));
        Assert.Throws<ArgumentNullException>(() => new StorageProviderId(null!));
    }

    [Fact]
    public void WellKnownProviderIdsAreStable()
    {
        Assert.Equal(new StorageProviderId("local"), StorageProviderId.Local);
        Assert.Equal(new StorageProviderId("central"), StorageProviderId.Central);
        Assert.NotEqual(StorageProviderId.Local, StorageProviderId.Central);
    }

    [Fact]
    public void ChecksumHexIsNormalisedToLowercase()
    {
        var upper = new ContentChecksum(ChecksumAlgorithm.Sha256, AbcSha256.ToUpperInvariant());
        var lower = new ContentChecksum(ChecksumAlgorithm.Sha256, AbcSha256);

        Assert.Equal(AbcSha256, upper.Hex);
        Assert.Equal(lower, upper);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
        Assert.Equal("sha256:" + AbcSha256, lower.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(65)]
    public void ChecksumWithWrongLengthIsRejected(int length) =>
        Assert.Throws<ArgumentException>(() => new ContentChecksum(ChecksumAlgorithm.Sha256, new string('a', length)));

    [Fact]
    public void ChecksumWithNonHexCharactersIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new ContentChecksum(ChecksumAlgorithm.Sha256, new string('g', 64)));
        Assert.Throws<ArgumentException>(() => new ContentChecksum(ChecksumAlgorithm.Sha256, new string('a', 63) + " "));
        Assert.Throws<ArgumentNullException>(() => new ContentChecksum(ChecksumAlgorithm.Sha256, null!));
    }

    [Fact]
    public void UnknownChecksumAlgorithmIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentChecksum((ChecksumAlgorithm)0, AbcSha256));

    [Fact]
    public void ChecksumFromDigestBytesMatchesHex()
    {
        var digest = SHA256.HashData("abc"u8);

        Assert.Equal(AbcSha256, ContentChecksum.Sha256(digest).Hex);
        Assert.Throws<ArgumentException>(() => ContentChecksum.Sha256(new byte[31]));
        Assert.Throws<ArgumentException>(() => ContentChecksum.Sha256(new byte[33]));
    }

    [Fact]
    public void DifferentDigestsAreNotEqual() =>
        Assert.NotEqual(new ContentChecksum(ChecksumAlgorithm.Sha256, AbcSha256), new ContentChecksum(ChecksumAlgorithm.Sha256, new string('0', 64)));

    [Fact]
    public void NoneSupportsNothing()
    {
        Assert.Empty(StorageCapabilities.None);
        Assert.False(StorageCapabilities.None.Supports(StorageCapabilityIds.Delete));
    }

    [Fact]
    public void CapabilitiesSupportExactlyWhatIsListed()
    {
        var capabilities = new StorageCapabilities([StorageCapabilityIds.Delete, StorageCapabilityIds.AtomicPublish, StorageCapabilityIds.Delete]);

        Assert.Equal(2, capabilities.Count);
        Assert.True(capabilities.Supports(StorageCapabilityIds.Delete));
        Assert.True(capabilities.Supports(new StorageCapabilityId("storage.atomic-publish")));
        Assert.False(capabilities.Supports(StorageCapabilityIds.ServerSideChecksum));
        Assert.Throws<ArgumentNullException>(() => capabilities.Supports(null!));
        Assert.Throws<ArgumentNullException>(() => new StorageCapabilities(null!));
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("storage.")]
    [InlineData(".delete")]
    [InlineData("storage..delete")]
    [InlineData("Storage.Delete")]
    [InlineData("storage.delete_all")]
    public void InvalidCapabilityIdsAreRejected(string value) => Assert.Throws<ArgumentException>(() => new StorageCapabilityId(value));
}
