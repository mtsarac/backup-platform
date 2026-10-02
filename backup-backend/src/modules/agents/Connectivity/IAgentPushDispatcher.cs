using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Agents.Connectivity;

/// <summary>
/// Backend-to-agent push, independent of the transport. Delivery is best effort and never the only path: the
/// connection map behind an implementation is ephemeral process memory, so no correctness or liveness state may
/// depend on it (liveness is persisted by heartbeats; anything pushed must also be discoverable by polling).
/// </summary>
public interface IAgentPushDispatcher
{
    /// <summary>Tells a connected agent that polling would now find work. False when the agent has no live connection here.</summary>
    Task<bool> TrySendPollHintAsync(AgentId agentId, string reason, CancellationToken cancellationToken);

    /// <summary>Drops the agent's live connection, if any (for example after revocation). Idempotent.</summary>
    Task DisconnectAsync(AgentId agentId, CancellationToken cancellationToken);

    /// <summary>Agents with a live connection on this process.</summary>
    IReadOnlyCollection<AgentId> ConnectedAgents { get; }
}

/// <summary>Used where no push transport is hosted; nothing is ever connected.</summary>
public sealed class NullAgentPushDispatcher : IAgentPushDispatcher
{
    public Task<bool> TrySendPollHintAsync(AgentId agentId, string reason, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task DisconnectAsync(AgentId agentId, CancellationToken cancellationToken) => Task.CompletedTask;

    public IReadOnlyCollection<AgentId> ConnectedAgents => [];
}
