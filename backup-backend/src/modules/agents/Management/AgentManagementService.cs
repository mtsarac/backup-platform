using BackupPlatform.Agents.Credentials;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Agents.Management;

/// <summary>Facts about a revocation, shaped for later auditing. <see cref="Changed"/> is false for a repeated request.</summary>
public sealed record AgentRevoked(AgentId AgentId, TenantId TenantId, UserId RevokedBy, bool Changed, DateTimeOffset At);

/// <summary>Tenant-scoped agent administration. The caller must already have authorized a tenant administrator for the tenant.</summary>
public sealed class AgentManagementService(IAgentRegistryStore store, TimeProvider timeProvider)
{
    public Task<IReadOnlyList<Agent>> ListAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        return store.ListAgentsAsync(tenantId, cancellationToken);
    }

    /// <summary>Null when the agent does not exist in the tenant, including when it belongs to another tenant.</summary>
    public async Task<AgentRevoked?> RevokeAsync(TenantId tenantId, AgentId agentId, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(actor);
        var now = timeProvider.GetUtcNow();
        var commit = await store.RevokeAgentAsync(tenantId, agentId, now, cancellationToken).ConfigureAwait(false);
        return commit is null ? null : new AgentRevoked(agentId, tenantId, actor, commit.Changed, now);
    }
}
