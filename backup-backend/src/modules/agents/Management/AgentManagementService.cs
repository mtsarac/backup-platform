using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Agents.Credentials;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Agents.Management;

/// <summary>Facts about a revocation, shaped for later auditing. <see cref="Changed"/> is false for a repeated request.</summary>
public sealed record AgentRevoked(AgentId AgentId, TenantId TenantId, UserId RevokedBy, bool Changed, DateTimeOffset At);

/// <summary>Tenant-scoped agent administration. The caller must already have authorized a tenant administrator for the tenant.</summary>
public sealed class AgentManagementService(IAgentRegistryStore store, TimeProvider timeProvider, IAgentPushDispatcher pushDispatcher)
{
    /// <summary>Null when the agent does not exist in the tenant, including when it belongs to another tenant.</summary>
    public async Task<AgentRevoked?> RevokeAsync(TenantId tenantId, AgentId agentId, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(actor);
        var now = timeProvider.GetUtcNow();
        var commit = await store.RevokeAgentAsync(tenantId, agentId, now, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            return null;
        }
        // Revocation is committed first, so a connection that survives this best-effort drop (another node, a race) is
        // still refused on its next authentication and by the periodic connection audit.
        await pushDispatcher.DisconnectAsync(agentId, cancellationToken).ConfigureAwait(false);
        return new AgentRevoked(agentId, tenantId, actor, commit.Changed, now);
    }
}
