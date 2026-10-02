using System.Globalization;
using BackupPlatform.Contracts.Protocol;

namespace BackupPlatform.UnitTests;

public sealed class ProtocolVersionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void VersionRejectsNonPositiveMajor(int major)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ProtocolVersion(major, 0));

        Assert.Equal("major", exception.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void VersionRejectsNegativeMinor(int minor)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ProtocolVersion(1, minor));

        Assert.Equal("minor", exception.ParamName);
    }

    [Fact]
    public void EqualRevisionsHaveValueEquality()
    {
        var first = new ProtocolVersion(2, 3);
        var second = new ProtocolVersion(2, 3);

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, new ProtocolVersion(2, 4));
        Assert.NotEqual(first, new ProtocolVersion(3, 3));
    }

    [Theory]
    [InlineData(1, int.MaxValue, 2, 0, -1)]
    [InlineData(2, 0, 1, int.MaxValue, 1)]
    [InlineData(1, 0, 1, int.MaxValue, -1)]
    [InlineData(1, int.MaxValue, 1, 0, 1)]
    [InlineData(2, 5, 2, 5, 0)]
    [InlineData(int.MaxValue, 0, 1, 0, 1)]
    public void ComparisonOrdersByMajorThenMinor(int major, int minor, int otherMajor, int otherMinor, int expectedSign)
    {
        IComparable<ProtocolVersion> version = new ProtocolVersion(major, minor);

        Assert.Equal(expectedSign, Math.Sign(version.CompareTo(new ProtocolVersion(otherMajor, otherMinor))));
    }

    [Fact]
    public void VersionSortsAfterNull()
    {
        var version = new ProtocolVersion(1, 0);

        Assert.True(version.CompareTo(null) > 0);
    }

    [Fact]
    public void VersionTextUsesInvariantMajorAndMinor()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberGroupSeparator = "_";
        culture.NumberFormat.NumberDecimalSeparator = ",";
        try
        {
            CultureInfo.CurrentCulture = culture;

            Assert.Equal("1234.5678", new ProtocolVersion(1234, 5678).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, true)]
    [InlineData(2, 3, true)]
    [InlineData(2, 4, true)]
    [InlineData(2, 5, false)]
    [InlineData(1, 3, false)]
    [InlineData(3, 3, false)]
    public void RangeIncludesBoundariesAndRejectsOtherMajors(int major, int minor, bool expected)
    {
        var range = new ProtocolVersionRange(new ProtocolVersion(2, 2), new ProtocolVersion(2, 4));

        Assert.Equal(expected, range.Contains(new ProtocolVersion(major, minor)));
    }

    [Fact]
    public void SingleRevisionRangeContainsOnlyThatRevision()
    {
        var range = new ProtocolVersionRange(new ProtocolVersion(1, 0), new ProtocolVersion(1, 0));

        Assert.True(range.Contains(new ProtocolVersion(1, 0)));
        Assert.False(range.Contains(new ProtocolVersion(1, 1)));
    }

    [Fact]
    public void RangeRejectsMixedMajorVersions()
    {
        Assert.Throws<ArgumentException>(() => new ProtocolVersionRange(new ProtocolVersion(1, 0), new ProtocolVersion(2, 0)));
    }

    [Fact]
    public void RangeRejectsReversedBounds()
    {
        Assert.Throws<ArgumentException>(() => new ProtocolVersionRange(new ProtocolVersion(1, 3), new ProtocolVersion(1, 2)));
    }

    [Fact]
    public void RangeRejectsNullBoundsAndNullCandidate()
    {
        var version = new ProtocolVersion(1, 0);
        var range = new ProtocolVersionRange(version, version);

        Assert.Throws<ArgumentNullException>(() => new ProtocolVersionRange(null!, version));
        Assert.Throws<ArgumentNullException>(() => new ProtocolVersionRange(version, null!));
        Assert.Throws<ArgumentNullException>(() => range.Contains(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AdvertisementAcceptsSupportedWireRevisionWithoutApplicationVersion(int minor)
    {
        var version = new ProtocolVersion(1, minor);
        var range = new ProtocolVersionRange(new ProtocolVersion(1, 0), new ProtocolVersion(1, 2));
        var advertisement = new ProtocolNegotiationContract(version, range);

        Assert.Equal(new ProtocolNegotiationContract(new ProtocolVersion(1, minor),
            new ProtocolVersionRange(new ProtocolVersion(1, 0), new ProtocolVersion(1, 2))), advertisement);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 4)]
    [InlineData(2, 2)]
    public void AdvertisementRejectsVersionOutsideDeclaredWindow(int major, int minor)
    {
        var range = new ProtocolVersionRange(new ProtocolVersion(1, 1), new ProtocolVersion(1, 3));

        var exception = Assert.Throws<ArgumentException>(() => new ProtocolNegotiationContract(new ProtocolVersion(major, minor), range));

        Assert.Equal("version", exception.ParamName);
    }

    [Fact]
    public void AdvertisementRejectsNullVersionAndRange()
    {
        var version = new ProtocolVersion(1, 0);
        var range = new ProtocolVersionRange(version, version);

        Assert.Throws<ArgumentNullException>(() => new ProtocolNegotiationContract(null!, range));
        Assert.Throws<ArgumentNullException>(() => new ProtocolNegotiationContract(version, null!));
    }
}
