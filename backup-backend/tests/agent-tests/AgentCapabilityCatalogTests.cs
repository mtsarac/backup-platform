using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Host;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.AgentTests;

public sealed class AgentCapabilityCatalogTests
{
    [Fact]
    public void The_default_catalog_states_exactly_what_the_product_implements()
    {
        var catalog = AgentCapabilityCatalog.CreateDefault();

        var supported = catalog.Capabilities.Where(c => c.Support == AgentCapabilitySupportNames.Supported).Select(c => c.Identifier).Order(StringComparer.Ordinal);
        var unsupported = catalog.Capabilities.Where(c => c.Support == AgentCapabilitySupportNames.Unsupported).Select(c => c.Identifier).Order(StringComparer.Ordinal);
        Assert.Equal(
            ["backup.multi-destination", "backup.quota-bounded-upload", "backup.three-two-one", "backup.upload-bandwidth-limit", "discovery.database-inventory", "file-system.backup.full", "file-system.restore.discovery", "file-system.restore.full", "file-system.restore.precheck", "mssql.backup.checksum", "mssql.backup.compression", "mssql.backup.copy-only", "mssql.backup.full", "mssql.restore.database", "mssql.restore.discovery", "mssql.restore.precheck", "mssql.restore.verification", "mssql.restore.verify-only",
             "postgres.backup.checksum", "postgres.backup.compression", "postgres.backup.copy-only", "postgres.backup.full", "postgres.restore.discovery", "postgres.restore.full", "postgres.restore.precheck", "postgres.restore.verification", "postgres.restore.verify", "replica.agent-lifecycle", "storage.agent-direct", "storage.backend-relay", "storage.capacity-probe", "storage.download.direct-v1", "storage.s3", "storage.smb"], supported);
        Assert.Equal(["mssql.backup.differential", "mssql.backup.log", "postgres.backup.differential", "postgres.backup.incremental", "postgres.backup.wal"], unsupported);
        Assert.Equal(39, catalog.Capabilities.Count);
        Assert.All(catalog.Capabilities.Where(c => c.Support == AgentCapabilitySupportNames.Supported),
            c => Assert.Equal("agent", Assert.Single(c.Metadata!, entry => entry.Key == "scope").Value));
        Assert.Equal(AgentCapabilityRevision.Compute(catalog.Capabilities), catalog.Revision);
        Assert.True(catalog.Revision.Length <= 64);
    }

    [Fact]
    public async Task The_source_advertises_the_catalog_revision_and_sends_the_catalog_only_when_asked_to_sync()
    {
        var catalog = AgentCapabilityCatalog.CreateDefault();
        var client = new RecordingCapabilityClient();
        ICapabilityRevisionSource source = new CatalogCapabilityRevisionSource(catalog, client, NullLogger<CatalogCapabilityRevisionSource>.Instance);

        Assert.Equal(catalog.Revision, source.GetRevision());
        Assert.Empty(client.Requests);

        Assert.True(await source.SyncAsync(TestIdentity.Create(), default));

        var request = Assert.Single(client.Requests);
        Assert.Equal(catalog.Revision, request.Revision);
        Assert.Equal(catalog.Capabilities.Count, request.Capabilities.Count);
        Assert.Equal(AgentCapabilityRevision.Compute(request.Capabilities), request.Revision);
    }

    [Fact]
    public async Task A_rejected_sync_reports_failure_without_throwing()
    {
        var client = new RecordingCapabilityClient { Failure = new AgentBackendFailure(AgentBackendFailureKind.Transient, "network down") };
        ICapabilityRevisionSource source = new CatalogCapabilityRevisionSource(
            AgentCapabilityCatalog.CreateDefault(), client, NullLogger<CatalogCapabilityRevisionSource>.Instance);

        Assert.False(await source.SyncAsync(TestIdentity.Create(), default));
    }

    [Fact]
    public async Task Sources_that_do_not_implement_sync_report_that_nothing_was_synced()
    {
        ICapabilityRevisionSource source = new NullCapabilityRevisionSource();

        Assert.Null(source.GetRevision());
        Assert.False(await source.SyncAsync(TestIdentity.Create(), default));
    }

    private sealed class RecordingCapabilityClient : IAgentCapabilityClient
    {
        public List<AgentCapabilitySyncRequest> Requests { get; } = [];

        public AgentBackendFailure? Failure { get; init; }

        public Task<AgentBackendResult<AgentCapabilitySyncResponse>> SyncAsync(
            BackupPlatform.Agent.Core.Identity.AgentLocalIdentity identity, AgentCapabilitySyncRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Failure is { } failure
                ? AgentBackendResult<AgentCapabilitySyncResponse>.Fail(failure)
                : AgentBackendResult<AgentCapabilitySyncResponse>.Success(new AgentCapabilitySyncResponse { Revision = request.Revision, Changed = true }));
        }
    }
}
