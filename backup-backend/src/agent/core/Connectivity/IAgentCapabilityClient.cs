using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Core.Connectivity;

/// <summary>
/// Sends the agent's capability snapshot to the backend. Same contract as <see cref="IAgentBackendClient"/>: the identity
/// is passed per call, every non-success outcome (including network errors) is an <see cref="AgentBackendFailure"/>, and
/// only cancellation throws.
/// </summary>
public interface IAgentCapabilityClient
{
    Task<AgentBackendResult<AgentCapabilitySyncResponse>> SyncAsync(
        AgentLocalIdentity identity, AgentCapabilitySyncRequest request, CancellationToken cancellationToken);
}
