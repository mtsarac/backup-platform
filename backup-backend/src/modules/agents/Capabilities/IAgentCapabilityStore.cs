using BackupPlatform.Application.Agents;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Agents.Capabilities;

/// <summary>A capability as persisted: identifier, claimed support and small descriptive metadata.</summary>
public sealed record AgentCapabilityEntry(string Identifier, CapabilitySupport Support, IReadOnlyDictionary<string, string> Metadata);

/// <summary>The last capability snapshot an agent synced, ordered by identifier.</summary>
public sealed record AgentCapabilitySnapshot(string Revision, DateTimeOffset ReportedAt, IReadOnlyList<AgentCapabilityEntry> Capabilities);

public enum ReplaceSnapshotOutcome
{
    /// <summary>The agent's snapshot was replaced.</summary>
    Replaced = 1,
    /// <summary>A snapshot with the same revision was already stored; nothing was written.</summary>
    Unchanged = 2,
    /// <summary>No agent exists for the tenant and agent of the identity.</summary>
    AgentNotFound = 3
}

/// <summary>Persistence of agent capability snapshots. Tenant and agent always come from an authenticated identity or an authorized tenant route.</summary>
public interface IAgentCapabilityStore
{
    /// <summary>
    /// Atomically replaces the agent's snapshot unless one with <paramref name="revision"/> is already stored. Concurrent
    /// calls for one agent are serialized, so the stored rows always form exactly one consistent snapshot.
    /// </summary>
    Task<ReplaceSnapshotOutcome> ReplaceSnapshotAsync(
        AgentIdentity identity, string revision, IReadOnlyCollection<Capability> capabilities, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The stored snapshot revision of the agent, or null when it has never synced. One indexed read.</summary>
    Task<string?> GetRevisionAsync(AgentIdentity identity, CancellationToken cancellationToken);

    /// <summary>Snapshots of all agents of the tenant that have one, in one query.</summary>
    Task<IReadOnlyDictionary<AgentId, AgentCapabilitySnapshot>> ListAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task<AgentCapabilitySnapshot?> GetAsync(TenantId tenantId, AgentId agentId, CancellationToken cancellationToken);
}
