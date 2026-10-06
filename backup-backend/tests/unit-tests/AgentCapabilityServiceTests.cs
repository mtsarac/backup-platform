using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests;

public sealed class AgentCapabilityServiceTests
{
    private static readonly AgentIdentity Caller = new(AgentId.New(), TenantId.New(), Guid.NewGuid(), 1);

    private static AgentCapabilityReport Report(string identifier = "mssql.backup.full", string support = "Supported", IReadOnlyDictionary<string, string>? metadata = null) =>
        new() { Identifier = identifier, Support = support, Metadata = metadata };

    private static AgentCapabilitySyncRequest Request(params AgentCapabilityReport[] reports) =>
        new() { Revision = AgentCapabilityRevision.Compute(reports), Capabilities = reports };

    private static (AgentCapabilityService Service, FakeStore Store) Create(string? stored = null)
    {
        var store = new FakeStore { Revision = stored };
        return (new AgentCapabilityService(store, TimeProvider.System), store);
    }

    [Fact]
    public async Task AValidSnapshotIsStoredForTheAuthenticatedIdentityOnly()
    {
        var (service, store) = Create();
        var request = Request(Report(), Report("mssql.backup.log", "Unsupported"));

        var result = await service.SyncAsync(Caller, request, CancellationToken.None);

        Assert.Equal(CapabilitySyncStatus.Synced, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(request.Revision, result.Revision);
        var write = Assert.Single(store.Writes);
        Assert.Equal(Caller, write.Identity);
        Assert.Equal(["mssql.backup.full", "mssql.backup.log"], write.Capabilities.Select(capability => capability.Identifier.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ASnapshotWithTheStoredRevisionIsAcceptedWithoutChange()
    {
        var request = Request(Report());
        var (service, store) = Create(stored: request.Revision);
        store.UnchangedWhenRevisionMatches = true;

        var result = await service.SyncAsync(Caller, request, CancellationToken.None);

        Assert.Equal(CapabilitySyncStatus.Synced, result.Status);
        Assert.False(result.Changed);
    }

    [Fact]
    public async Task ARevisionThatDoesNotMatchTheCapabilitiesIsRejectedWithoutWriting()
    {
        var (service, store) = Create();
        var request = Request(Report()) with { Revision = "c1-" + new string('0', 48) };

        var result = await service.SyncAsync(Caller, request, CancellationToken.None);

        Assert.Equal(CapabilitySyncStatus.InvalidRequest, result.Status);
        Assert.Equal("Revision", result.InvalidField);
        Assert.Empty(store.Writes);
    }

    public static TheoryData<string, AgentCapabilitySyncRequest> InvalidRequests()
    {
        static AgentCapabilitySyncRequest With(params AgentCapabilityReport[] reports) => Request(reports);
        var many = Enumerable.Range(0, 129).Select(index => Report($"mssql.cap-{index}")).ToArray();
        var nine = Enumerable.Range(0, 9).ToDictionary(index => $"k{index}", _ => "v");
        return new TheoryData<string, AgentCapabilitySyncRequest>
        {
            { "Capabilities", With() },
            { "Capabilities", With(many) },
            { "Capabilities.Identifier", With(Report("NotValid")) },
            { "Capabilities.Identifier", With(Report("single")) },
            { "Capabilities.Identifier", With(Report("mssql..backup")) },
            { "Capabilities.Identifier", With(Report("")) },
            { "Capabilities.Identifier", With(Report("mssql." + new string('a', 124))) },
            { "Capabilities.Identifier", With(Report(), Report()) },
            { "Capabilities.Support", With(Report(support: "1")) },
            { "Capabilities.Support", With(Report(support: "supported")) },
            { "Capabilities.Support", With(Report(support: "")) },
            { "Capabilities.Support", With(Report(support: "Supported,Unsupported")) },
            { "Capabilities.Metadata", With(Report(metadata: nine)) },
            { "Capabilities.Metadata", With(Report(metadata: new Dictionary<string, string> { [new string('k', 65)] = "v" })) },
            { "Capabilities.Metadata", With(Report(metadata: new Dictionary<string, string> { ["k"] = new string('v', 257) })) },
            { "Capabilities.Metadata", With(Report(metadata: new Dictionary<string, string> { [" "] = "v" })) },
            { "Capabilities.Metadata", With(Report(metadata: new Dictionary<string, string> { ["k"] = " " })) },
            { "Capabilities.Metadata", With(Report(metadata: new Dictionary<string, string> { ["k"] = "line\nbreak" })) },
            { "Capabilities.Metadata", With(Report(metadata: new Dictionary<string, string> { ["k\t"] = "v" })) }
        };
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidSnapshotsAreRejectedByFieldWithoutWritingOrEchoingInput(string field, AgentCapabilitySyncRequest request)
    {
        var (service, store) = Create();

        var result = await service.SyncAsync(Caller, request, CancellationToken.None);

        Assert.Equal(CapabilitySyncStatus.InvalidRequest, result.Status);
        Assert.Equal(field, result.InvalidField);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task ABoundaryShapedSnapshotIsAccepted()
    {
        var (service, _) = Create();
        var metadata = Enumerable.Range(0, 8).ToDictionary(index => new string((char)('a' + index), 64), _ => new string('v', 256));
        var reports = Enumerable.Range(0, 128).Select(index => Report($"mssql.cap-{index}", "Unknown", metadata)).ToArray();

        var result = await service.SyncAsync(Caller, Request(reports), CancellationToken.None);

        Assert.Equal(CapabilitySyncStatus.Synced, result.Status);
    }

    [Fact]
    public async Task AnAgentWithoutTheSnapshotsAgentRowIsNotFound()
    {
        var (service, store) = Create();
        store.AgentExists = false;

        var result = await service.SyncAsync(Caller, Request(Report()), CancellationToken.None);

        Assert.Equal(CapabilitySyncStatus.AgentNotFound, result.Status);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", null, false)]
    [InlineData("  ", "c1-aaa", false)]
    [InlineData("c1-aaa", null, true)]
    [InlineData("c1-aaa", "c1-bbb", true)]
    [InlineData("c1-aaa", "c1-aaa", false)]
    [InlineData(" c1-aaa ", "c1-aaa", false)]
    public async Task ASyncIsRequestedOnlyForAnAdvertisedRevisionThatDiffersFromTheStoredOne(string? reported, string? stored, bool expected)
    {
        var (service, store) = Create(stored);

        var requested = await service.IsSyncRequestedAsync(Caller, reported, CancellationToken.None);

        Assert.Equal(expected, requested);
        Assert.True(store.RevisionReads <= 1);
        if (string.IsNullOrWhiteSpace(reported))
        {
            Assert.Equal(0, store.RevisionReads);
        }
    }

    private sealed record Write(AgentIdentity Identity, string Revision, IReadOnlyCollection<Capability> Capabilities);

    private sealed class FakeStore : IAgentCapabilityStore
    {
        public string? Revision { get; set; }
        public bool AgentExists { get; set; } = true;
        public bool UnchangedWhenRevisionMatches { get; set; }
        public int RevisionReads { get; private set; }
        public List<Write> Writes { get; } = [];

        public Task<ReplaceSnapshotOutcome> ReplaceSnapshotAsync(
            AgentIdentity identity, string revision, IReadOnlyCollection<Capability> capabilities, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (!AgentExists)
            {
                return Task.FromResult(ReplaceSnapshotOutcome.AgentNotFound);
            }
            if (UnchangedWhenRevisionMatches && Revision == revision)
            {
                return Task.FromResult(ReplaceSnapshotOutcome.Unchanged);
            }
            Writes.Add(new Write(identity, revision, capabilities));
            Revision = revision;
            return Task.FromResult(ReplaceSnapshotOutcome.Replaced);
        }

        public Task<string?> GetRevisionAsync(AgentIdentity identity, CancellationToken cancellationToken)
        {
            RevisionReads++;
            return Task.FromResult(Revision);
        }

        public Task<IReadOnlyDictionary<AgentId, AgentCapabilitySnapshot>> ListAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AgentCapabilitySnapshot?> GetAsync(TenantId tenantId, AgentId agentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
