using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Api.Backups;

/// <summary>Turns "an agent has new work" into the existing best-effort poll hint. Whether it is delivered never matters: agents poll.</summary>
public sealed class AgentPushWorkSignal(IAgentPushDispatcher push) : IBackupWorkSignal
{
    public async Task WorkAvailableAsync(AgentId agentId, CancellationToken cancellationToken) =>
        await push.TrySendPollHintAsync(agentId, "WorkAvailable", cancellationToken);
}
