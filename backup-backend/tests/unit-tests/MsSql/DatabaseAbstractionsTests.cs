using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Domain.Capabilities;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class DatabaseAbstractionsTests
{
    [Theory]
    [InlineData("mssql.backup.full")]
    [InlineData("postgresql.backup.base")]
    [InlineData("a.b")]
    [InlineData("1.2-3")]
    [InlineData("")]
    [InlineData("mssql")]
    [InlineData("mssql..full")]
    [InlineData(".mssql.full")]
    [InlineData("mssql.full.")]
    [InlineData("MSSQL.backup")]
    [InlineData("mssql.back_up")]
    [InlineData("mssql.backup full")]
    [InlineData("mssql.bäckup")]
    public void CapabilityIdentifiersAcceptExactlyWhatThePlatformIdentifiersAccept(string value)
    {
        // The provider layer cannot reference the domain project, so it repeats the grammar; this keeps the two from drifting
        // and guarantees any provider capability can be mapped to a platform capability.
        var platformAccepts = Accepts(() => new CapabilityIdentifier(value));
        var providerAccepts = Accepts(() => new DatabaseCapabilityId(value));

        Assert.Equal(platformAccepts, providerAccepts);
    }

    [Fact]
    public void SupportLevelsHaveTheSameNumericValuesAsThePlatformSupportLevels()
    {
        Assert.Equal((int)CapabilitySupport.Unknown, (int)DatabaseCapabilitySupport.Unknown);
        Assert.Equal((int)CapabilitySupport.Supported, (int)DatabaseCapabilitySupport.Supported);
        Assert.Equal((int)CapabilitySupport.Unsupported, (int)DatabaseCapabilitySupport.Unsupported);
    }

    [Fact]
    public void CapabilitySetsRejectDuplicatesAndAnswerUnknownForUnevaluatedIds()
    {
        var full = new DatabaseCapability(new DatabaseCapabilityId("x.full"), DatabaseCapabilitySupport.Supported, "ok", "fine");
        var set = new DatabaseCapabilitySet([full]);

        Assert.Throws<ArgumentException>(() => new DatabaseCapabilitySet([full, full]));
        Assert.Equal(DatabaseCapabilitySupport.Supported, set.SupportOf(new DatabaseCapabilityId("x.full")));
        Assert.Equal(DatabaseCapabilitySupport.Unknown, set.SupportOf(new DatabaseCapabilityId("x.other")));
    }

    [Fact]
    public void CapabilitiesRequireAReasonAndADefinedSupportLevel()
    {
        var id = new DatabaseCapabilityId("x.full");
        Assert.Throws<ArgumentException>(() => new DatabaseCapability(id, DatabaseCapabilitySupport.Supported, " ", "fine"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseCapability(id, (DatabaseCapabilitySupport)9, "ok", "fine"));
    }

    [Theory]
    [InlineData("mssql")]
    [InlineData("postgresql")]
    [InlineData("my-sql-2")]
    public void EngineIdsAreLowercaseTokens(string value) => Assert.Equal(value, new DatabaseEngineId(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData("MSSQL")]
    [InlineData("ms.sql")]
    [InlineData("ms sql")]
    public void EngineIdsRejectEverythingElse(string value) => Assert.Throws<ArgumentException>(() => new DatabaseEngineId(value));

    [Fact]
    public void EndpointsAddressAnInstanceByNameOrPortNotBoth()
    {
        Assert.Throws<ArgumentException>(() => new DatabaseEndpoint("host", 1433, "INSTANCE"));
        Assert.Equal("INSTANCE", new DatabaseEndpoint("host", instanceName: "INSTANCE").InstanceName);
        Assert.Equal(1433, new DatabaseEndpoint("host", 1433).Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("two words")]
    [InlineData("tab\there")]
    public void EndpointsRejectUnusableHosts(string host) => Assert.Throws<ArgumentException>(() => new DatabaseEndpoint(host));

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void EndpointsRejectInvalidPorts(int port) => Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseEndpoint("host", port));

    [Fact]
    public void OptionsDefaultToSecureFiniteValues()
    {
        var options = new DatabaseConnectionOptions();

        Assert.Equal(TimeSpan.FromSeconds(15), options.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.CommandTimeout);
        Assert.True(options.RequireEncryption);
        Assert.False(options.TrustServerCertificate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(500)]
    public void OptionsRejectZeroNegativeAndExcessiveTimeouts(int milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(milliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseConnectionOptions(connectTimeout: value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseConnectionOptions(commandTimeout: value));
    }

    [Fact]
    public void InfiniteTimeoutsAreNotExpressible()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseConnectionOptions(commandTimeout: Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseConnectionOptions(connectTimeout: TimeSpan.FromDays(1)));
    }

    [Fact]
    public void FractionalTimeoutsRoundUpToWholeSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(2), new DatabaseConnectionOptions(connectTimeout: TimeSpan.FromMilliseconds(1100)).ConnectTimeout);

    [Fact]
    public void PreCheckRequestDeduplicatesRequestedCapabilities()
    {
        var id = new DatabaseCapabilityId("mssql.backup.checksum");
        var request = new FullBackupPreCheckRequest("Sales", [id, new DatabaseCapabilityId("mssql.backup.checksum")]);
        Assert.Single(request.RequestedCapabilities);
    }

    private static bool Accepts(Action construct)
    {
        try
        {
            construct();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
