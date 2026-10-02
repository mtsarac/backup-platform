using BackupPlatform.Domain.Capabilities;

namespace BackupPlatform.UnitTests;

public sealed class CapabilityTests
{
    [Theory]
    [InlineData("agent.snapshot")]
    [InlineData("backup-provider.snapshot-v2.incremental")]
    [InlineData("1.2-3")]
    public void IdentifierPreservesCanonicalNames(string value)
    {
        var identifier = new CapabilityIdentifier(value);

        Assert.Equal(value, identifier.Value);
        Assert.Equal(value, identifier.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("agent")]
    [InlineData(".agent.snapshot")]
    [InlineData("agent.snapshot.")]
    [InlineData("agent..snapshot")]
    [InlineData("Agent.snapshot")]
    [InlineData("agent.SNAPSHOT")]
    [InlineData("agent.snapshot_2")]
    [InlineData("agent.snápShot")]
    [InlineData(" agent.snapshot")]
    [InlineData("agent.snapshot ")]
    [InlineData("agent.snap\nshot")]
    [InlineData("agent/snapshot")]
    public void IdentifierRejectsNoncanonicalNames(string value)
    {
        Assert.Throws<ArgumentException>(() => new CapabilityIdentifier(value));
    }

    [Fact]
    public void IdentifierRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new CapabilityIdentifier(null!));
    }

    [Fact]
    public void IdentifierUsesValueIdentityForEqualityAndHashLookup()
    {
        var identifier = new CapabilityIdentifier("agent.snapshot");
        var equivalent = new CapabilityIdentifier("agent.snapshot");
        var different = new CapabilityIdentifier("agent.restore");
        var identifiers = new HashSet<CapabilityIdentifier> { identifier };

        Assert.Equal(identifier, equivalent);
        Assert.True(identifier == equivalent);
        Assert.Contains(equivalent, identifiers);
        Assert.NotEqual(identifier, different);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void CapabilityRejectsUndefinedSupport(int support)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Capability(new CapabilityIdentifier("agent.snapshot"), (CapabilitySupport)support));
    }

    [Fact]
    public void CapabilityRejectsNullIdentifier()
    {
        Assert.Throws<ArgumentNullException>(() => new Capability(null!, CapabilitySupport.Unknown));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "")]
    [InlineData(false, " \t")]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, " \t")]
    public void CapabilityRejectsNullOrBlankDictionaryKeys(bool metadata, string? key)
    {
        KeyValuePair<string, string>[] entries = [new(key!, "value")];

        Assert.Throws<ArgumentException>(() => CreateCapability(entries, metadata));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapabilityRejectsNullDictionaryValues(bool metadata)
    {
        KeyValuePair<string, string>[] entries = [new("key", null!)];

        Assert.Throws<ArgumentException>(() => CreateCapability(entries, metadata));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapabilityRejectsDuplicateOrdinalKeys(bool metadata)
    {
        KeyValuePair<string, string>[] entries = [new("key", "first"), new("key", "second")];

        Assert.Throws<ArgumentException>(() => CreateCapability(entries, metadata));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapabilityKeepsOrdinallyDistinctKeysAndEmptyValues(bool metadata)
    {
        KeyValuePair<string, string>[] entries = [new("key", ""), new("Key", "upper")];
        var capability = CreateCapability(entries, metadata);
        var dictionary = metadata ? capability.Metadata : capability.Constraints;

        Assert.Equal("", dictionary["key"]);
        Assert.Equal("upper", dictionary["Key"]);
        Assert.False(dictionary.ContainsKey("KEY"));
    }

    [Fact]
    public void CapabilitySnapshotsBothDictionariesAndPreventsMutation()
    {
        var constraints = new Dictionary<string, string> { ["max-size"] = "100" };
        var metadata = new Dictionary<string, string> { ["provider"] = "custom" };
        var capability = new Capability(
            new CapabilityIdentifier("agent.snapshot"),
            CapabilitySupport.Supported,
            constraints: constraints,
            metadata: metadata);

        constraints["max-size"] = "200";
        metadata.Clear();
        Assert.Equal("100", capability.Constraints["max-size"]);
        Assert.Equal("custom", capability.Metadata["provider"]);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)capability.Constraints).Add("new", "value"));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)capability.Metadata)["provider"] = "changed");
    }

    [Fact]
    public void CapabilityDistinguishesUnknownSupportFromAbsentCapability()
    {
        var capability = new Capability(new CapabilityIdentifier("agent.snapshot"), CapabilitySupport.Unknown);
        var set = new CapabilitySet([capability]);

        Assert.True(set.TryGet(new CapabilityIdentifier("agent.snapshot"), out var found));
        Assert.Equal(CapabilitySupport.Unknown, found!.Support);
        Assert.False(set.TryGet(new CapabilityIdentifier("agent.restore"), out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void CapabilityRetainsOptionalVersionAndUsesEmptyOptionalDictionaries()
    {
        var capability = new Capability(
            new CapabilityIdentifier("agent.snapshot"), CapabilitySupport.Supported, new Version(2, 3, 4));
        var unversioned = new Capability(new CapabilityIdentifier("agent.restore"), CapabilitySupport.Unsupported);

        Assert.Equal(new Version(2, 3, 4), capability.Version);
        Assert.Empty(capability.Constraints);
        Assert.Empty(capability.Metadata);
        Assert.Null(unversioned.Version);
        Assert.Equal(CapabilitySupport.Unsupported, unversioned.Support);
    }

    [Fact]
    public void CapabilitySetRejectsNullSourceAndEntries()
    {
        Assert.Throws<ArgumentNullException>(() => new CapabilitySet(null!));
        Assert.Throws<ArgumentException>(() => new CapabilitySet([null!]));
    }

    [Fact]
    public void CapabilitySetRejectsDistinctInstancesWithEqualIdentifiers()
    {
        var first = new Capability(new CapabilityIdentifier("agent.snapshot"), CapabilitySupport.Supported);
        var duplicate = new Capability(new CapabilityIdentifier("agent.snapshot"), CapabilitySupport.Unsupported);

        Assert.Throws<ArgumentException>(() => new CapabilitySet([first, duplicate]));
        Assert.Throws<ArgumentException>(() => new CapabilitySet([first, first]));
    }

    [Fact]
    public void CapabilitySetSnapshotsSourceAndSupportsValueBasedLookup()
    {
        var first = new Capability(new CapabilityIdentifier("agent.snapshot"), CapabilitySupport.Supported);
        var second = new Capability(new CapabilityIdentifier("agent.restore"), CapabilitySupport.Unsupported);
        var source = new List<Capability> { first, second };
        IReadOnlyCollection<Capability> set = new CapabilitySet(source);
        source.Clear();

        Assert.Equal(2, set.Count);
        Assert.Contains(first, set);
        Assert.Contains(second, set);
        Assert.True(((CapabilitySet)set).TryGet(new CapabilityIdentifier("agent.snapshot"), out var found));
        Assert.Same(first, found);
    }

    [Fact]
    public void EmptySetReturnsAbsentAndRejectsNullLookup()
    {
        Assert.Empty(CapabilitySet.Empty);
        Assert.False(CapabilitySet.Empty.TryGet(new CapabilityIdentifier("agent.snapshot"), out var capability));
        Assert.Null(capability);
        Assert.Throws<ArgumentNullException>(() => CapabilitySet.Empty.TryGet(null!, out _));
    }

    private static Capability CreateCapability(IEnumerable<KeyValuePair<string, string>> entries, bool metadata)
    {
        return new Capability(
            new CapabilityIdentifier("agent.snapshot"),
            CapabilitySupport.Supported,
            constraints: metadata ? null : entries,
            metadata: metadata ? entries : null);
    }
}
