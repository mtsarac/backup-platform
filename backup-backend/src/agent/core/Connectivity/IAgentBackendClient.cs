using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Core.Connectivity;

/// <summary>
/// Request/response calls to the backend's agent API. The identity is passed per call so a credential reloaded from the
/// store takes effect on the next request. Implementations authenticate each call, send the protocol header, map every
/// non-success outcome (including network errors) to an <see cref="AgentBackendFailure"/>, and throw only for cancellation.
/// </summary>
public interface IAgentBackendClient
{
    /// <summary>The backend this client talks to, for diagnostics.</summary>
    Uri BackendBaseUri { get; }

    Task<AgentBackendResult<AgentHeartbeatResponse>> SendHeartbeatAsync(AgentLocalIdentity identity, AgentHeartbeatRequest request, CancellationToken cancellationToken);

    /// <param name="cursor">The cursor of the previous poll answer, or null for the first poll.</param>
    Task<AgentBackendResult<AgentPollResponse>> PollAsync(AgentLocalIdentity identity, string? cursor, CancellationToken cancellationToken);
}
