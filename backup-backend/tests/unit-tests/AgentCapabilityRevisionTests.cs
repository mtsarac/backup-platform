using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.UnitTests;

public sealed class AgentCapabilityRevisionTests
{
    private static AgentCapabilityReport Report(string identifier, string support = "Supported", params (string Key, string Value)[] metadata) => new()
    {
        Identifier = identifier,
        Support = support,
        Metadata = metadata.Length == 0 ? null : metadata.ToDictionary(entry => entry.Key, entry => entry.Value)
    };

    private static readonly AgentCapabilityReport[] Baseline =
    [
        Report("mssql.backup.full", "Supported", ("scope", "agent")),
        Report("mssql.backup.log", "Unsupported")
    ];

    [Fact]
    public void TheRevisionIsDeterministicLowercaseHexWithAVersionPrefixWithinTheHeartbeatLimit()
    {
        var first = AgentCapabilityRevision.Compute(Baseline);
        var second = AgentCapabilityRevision.Compute(Baseline.Select(report => report with { }).ToList());

        Assert.Equal(first, second);
        Assert.StartsWith(AgentCapabilityRevision.Prefix, first, StringComparison.Ordinal);
        Assert.Equal(AgentCapabilityRevision.Length, first.Length);
        Assert.True(first.Length <= 64);
        Assert.All(first[AgentCapabilityRevision.Prefix.Length..], character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    [Fact]
    public void TheRevisionDoesNotDependOnCapabilityOrMetadataOrder()
    {
        var forward = new[]
        {
            Report("mssql.backup.full", "Supported", ("b", "2"), ("a", "1")),
            Report("mssql.backup.log", "Unsupported")
        };
        var reversed = new[]
        {
            Report("mssql.backup.log", "Unsupported"),
            Report("mssql.backup.full", "Supported", ("a", "1"), ("b", "2"))
        };

        Assert.Equal(AgentCapabilityRevision.Compute(forward), AgentCapabilityRevision.Compute(reversed));
    }

    [Fact]
    public void NullAndEmptyMetadataAreTheSameCapability()
    {
        var none = Report("mssql.backup.full");
        var empty = none with { Metadata = new Dictionary<string, string>() };

        Assert.Equal(AgentCapabilityRevision.Compute([none]), AgentCapabilityRevision.Compute([empty]));
    }

    public static TheoryData<string, AgentCapabilityReport[]> Changes => new()
    {
        { "identifier", [Report("mssql.backup.fuller", "Supported", ("scope", "agent")), Baseline[1]] },
        { "support", [Baseline[0], Report("mssql.backup.log", "Supported")] },
        { "metadata value", [Report("mssql.backup.full", "Supported", ("scope", "instance")), Baseline[1]] },
        { "metadata key", [Report("mssql.backup.full", "Supported", ("scopes", "agent")), Baseline[1]] },
        { "metadata added", [Baseline[0], Report("mssql.backup.log", "Unsupported", ("scope", "agent"))] },
        { "capability removed", [Baseline[0]] },
        { "capability added", [Baseline[0], Baseline[1], Report("mssql.restore.verify-only")] },
        { "empty set", [] }
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public void AnyChangeOfTheCapabilitySetChangesTheRevision(string change, AgentCapabilityReport[] changed)
    {
        Assert.NotEqual(AgentCapabilityRevision.Compute(Baseline), AgentCapabilityRevision.Compute(changed));
        Assert.False(string.IsNullOrEmpty(change));
    }

    [Fact]
    public void ValuesCannotShiftBetweenFieldsToProduceTheSameRevision()
    {
        var one = new[] { Report("mssql.a", "Supported", ("k", "v;1:x")) };
        var other = new[] { Report("mssql.a", "Supported", ("k", "v"), ("1:x", "")) };

        Assert.NotEqual(AgentCapabilityRevision.Compute(one), AgentCapabilityRevision.Compute(other));
    }
}
