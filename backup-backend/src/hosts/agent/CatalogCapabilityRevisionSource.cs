using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Host;

/// <summary>
/// Advertises the revision of the <see cref="AgentCapabilityCatalog"/> on every heartbeat and sends the catalog itself
/// only when the backend asks for a sync.
/// </summary>
public sealed partial class CatalogCapabilityRevisionSource(
    AgentCapabilityCatalog catalog, IAgentCapabilityClient client, ILogger<CatalogCapabilityRevisionSource> logger) : ICapabilityRevisionSource
{
    public string? GetRevision() => catalog.Revision;

    public async Task<bool> SyncAsync(AgentLocalIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var result = await client.SyncAsync(
            identity, new AgentCapabilitySyncRequest { Revision = catalog.Revision, Capabilities = catalog.Capabilities }, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            LogSynced(identity.AgentId, result.Value.Revision, result.Value.Changed);
            return true;
        }
        // Descriptions come from the transport and never contain credentials.
        LogSyncRejected(identity.AgentId, result.Failure.Kind, result.Failure.Description);
        return false;
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Capabilities synced: AgentId={AgentId} Revision={Revision} Changed={Changed}")]
    private partial void LogSynced(Guid agentId, string revision, bool changed);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "Capability sync rejected: AgentId={AgentId} Failure={Failure} Reason={Reason}")]
    private partial void LogSyncRejected(Guid agentId, AgentBackendFailureKind failure, string reason);
}
