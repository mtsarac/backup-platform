using System.Security.Cryptography;
using BackupPlatform.Encryption;

namespace BackupPlatform.UnitTests.Encryption;

public sealed class ShamirSecretSharingTests
{
    private static IEnumerable<int[]> Subsets(int total, int size)
    {
        var indexes = Enumerable.Range(0, total).ToArray();
        IEnumerable<int[]> Pick(int start, int remaining) =>
            remaining == 0
                ? [[]]
                : indexes.Skip(start).SelectMany((value, offset) => Pick(start + offset + 1, remaining - 1).Select(rest => (int[])[value, .. rest]));
        return Pick(0, size);
    }

    [Theory]
    [InlineData(3, 5)]
    [InlineData(2, 2)]
    [InlineData(2, 7)]
    [InlineData(4, 6)]
    [InlineData(5, 5)]
    public void EveryKSubsetRecoversAndEveryShorterSubsetIsRefused(int threshold, int total)
    {
        var secret = RandomNumberGenerator.GetBytes(257);
        var shares = ShamirSecretSharing.Split(secret, threshold, total);

        foreach (var subset in Subsets(total, threshold))
        {
            Assert.Equal(secret, ShamirSecretSharing.Combine([.. subset.Select(i => shares[i])]));
        }

        foreach (var subset in Subsets(total, threshold - 1))
        {
            Assert.Throws<BpeShareException>(() => ShamirSecretSharing.Combine([.. subset.Select(i => shares[i])]));
        }
    }

    [Fact]
    public void ShareOrderAndSurplusSharesDoNotMatter()
    {
        var secret = RandomNumberGenerator.GetBytes(64);
        var shares = ShamirSecretSharing.Split(secret, 3, 5);

        Assert.Equal(secret, ShamirSecretSharing.Combine([shares[4], shares[0], shares[2]]));
        Assert.Equal(secret, ShamirSecretSharing.Combine([.. shares.Reverse()]));
    }

    [Fact]
    public void SharesRevealNothingByThemselves()
    {
        var secret = new byte[32];
        var shares = ShamirSecretSharing.Split(secret, 3, 5);

        // An all-zero secret must not produce all-zero (or equal) share data.
        Assert.All(shares, share => Assert.Contains(share.AsSpan(42, 32).ToArray(), value => value != 0));
        Assert.Equal(5, shares.Select(share => Convert.ToHexString(share.AsSpan(42, 32))).Distinct().Count());
    }

    [Fact]
    public void SplittingTwiceUsesIndependentRandomness()
    {
        var secret = RandomNumberGenerator.GetBytes(32);

        Assert.NotEqual(ShamirSecretSharing.Split(secret, 3, 5)[0], ShamirSecretSharing.Split(secret, 3, 5)[0]);
    }

    [Fact]
    public void AnyChangedByteOfAShareIsDetected()
    {
        var shares = ShamirSecretSharing.Split(RandomNumberGenerator.GetBytes(40), 3, 5);
        for (var offset = 0; offset < shares[0].Length; offset++)
        {
            var tampered = (byte[])shares[0].Clone();
            tampered[offset] ^= 0x01;

            Assert.Throws<BpeShareException>(() => ShamirSecretSharing.Combine([tampered, shares[1], shares[2]]));
        }
    }

    [Fact]
    public void AForgedShareWithAValidChecksumIsStillCaughtBySecretCheck()
    {
        var shares = ShamirSecretSharing.Split(RandomNumberGenerator.GetBytes(40), 3, 5);
        var forged = (byte[])shares[0].Clone();
        forged[^9] ^= 0x01; // last data byte
        SHA256.HashData(forged.AsSpan(0, forged.Length - 8))[..8].CopyTo(forged.AsSpan(forged.Length - 8));

        var failure = Assert.Throws<BpeShareException>(() => ShamirSecretSharing.Combine([forged, shares[1], shares[2]]));
        Assert.Contains("corrupted or forged", failure.Message);
    }

    [Fact]
    public void SharesFromDifferentCeremoniesCannotBeMixed()
    {
        var a = ShamirSecretSharing.Split(RandomNumberGenerator.GetBytes(40), 3, 5);
        var b = ShamirSecretSharing.Split(RandomNumberGenerator.GetBytes(40), 3, 5);

        var failure = Assert.Throws<BpeShareException>(() => ShamirSecretSharing.Combine([a[0], a[1], b[2]]));
        Assert.Contains("different sets", failure.Message);
    }

    [Fact]
    public void ADuplicatedShareDoesNotCountTowardsTheThreshold()
    {
        var shares = ShamirSecretSharing.Split(RandomNumberGenerator.GetBytes(40), 3, 5);

        var failure = Assert.Throws<BpeShareException>(() => ShamirSecretSharing.Combine([shares[0], shares[0], shares[1]]));
        Assert.Contains("more than once", failure.Message);
    }

    [Fact]
    public void TextEncodingRoundTripsAndRejectsGarbage()
    {
        var share = ShamirSecretSharing.Split(RandomNumberGenerator.GetBytes(40), 3, 5)[1];

        Assert.Equal(share, ShamirSecretSharing.FromText("  " + ShamirSecretSharing.ToText(share) + "\n"));
        Assert.Throws<BpeShareException>(() => ShamirSecretSharing.FromText("hello"));
        Assert.Throws<BpeShareException>(() => ShamirSecretSharing.FromText(ShamirSecretSharing.TextPrefix + "!!!"));
        Assert.Throws<BpeShareException>(() => ShamirSecretSharing.Combine([[1, 2, 3]]));
        Assert.Throws<BpeShareException>(() => ShamirSecretSharing.Combine([]));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(6, 5)]
    [InlineData(3, 256)]
    public void InvalidParametersAreRejected(int threshold, int total) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ShamirSecretSharing.Split(new byte[8], threshold, total));

    [Fact]
    public void EmptyAndOversizedSecretsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShamirSecretSharing.Split([], 3, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShamirSecretSharing.Split(new byte[ShamirSecretSharing.MaxSecretLength + 1], 3, 5));
    }

    [Fact]
    public void WorksForTheLargestSupportedShareCount()
    {
        var secret = RandomNumberGenerator.GetBytes(48);
        var shares = ShamirSecretSharing.Split(secret, 128, 255);

        Assert.Equal(secret, ShamirSecretSharing.Combine([.. shares.Skip(100).Take(128)]));
        Assert.Throws<BpeShareException>(() => ShamirSecretSharing.Combine([.. shares.Take(127)]));
    }
}
