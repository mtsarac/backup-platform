using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Domain.Identity;
using Microsoft.AspNetCore.SignalR;

namespace BackupPlatform.Api.Push;

/// <summary>SignalR implementation of <see cref="IAgentPushDispatcher"/>; lives in the host so the module stays transport-free.</summary>
public sealed class SignalRAgentPushDispatcher(IHubContext<AgentHub, IAgentHubClient> hub, AgentConnectionRegistry registry) : IAgentPushDispatcher
{
    public IReadOnlyCollection<AgentId> ConnectedAgents => registry.ConnectedAgents;

    public async Task<bool> TrySendPollHintAsync(AgentId agentId, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        var connectionId = registry.FindConnectionId(agentId);
        if (connectionId is null)
        {
            return false;
        }
        await hub.Clients.Client(connectionId).PollHint(new AgentPollHint { Reason = reason });
        return true;
    }

    public Task DisconnectAsync(AgentId agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        registry.Abort(agentId);
        return Task.CompletedTask;
    }
}
