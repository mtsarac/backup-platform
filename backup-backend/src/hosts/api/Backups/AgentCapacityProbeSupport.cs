using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Contracts.Storage;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Storage;

namespace BackupPlatform.Api.Backups;

public sealed class AgentConnectivityLiveness(AgentConnectivityQueryService connectivity) : ITenantAgentLiveness
{
    public async Task<Guid?> FindActiveAgentAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var agents = await connectivity.ListAsync(tenantId, AgentConnectivityFilter.Online, cancellationToken).ConfigureAwait(false);
        var online = agents.FirstOrDefault(a => a.Status == AgentOperationalStatus.Online || a.Connectivity == AgentConnectivityState.Online);
        return online?.Agent.Id.Value;
    }
}

public sealed class DefaultAgentCapacityProbeDispatcher(
    Func<Guid, CapacityProbeTaskRequest, CancellationToken, Task<CapacityProbeTaskResult>>? dispatcher = null) : IAgentCapacityProbeDispatcher
{
    public Task<CapacityProbeTaskResult> DispatchAsync(Guid agentId, CapacityProbeTaskRequest request, CancellationToken cancellationToken)
    {
        if (dispatcher is not null)
        {
            return dispatcher(agentId, request, cancellationToken);
        }

        return Task.FromResult(CapacityProbeTaskResult.Failure(
            request.TaskId,
            request.ConnectionId,
            CapacityProbeFailureCodes.CapacityUnavailable,
            "No agent dispatcher transport available."));
    }
}
