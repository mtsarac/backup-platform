using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Agents.Connectivity;

public sealed record HeartbeatWrite(
    AgentId AgentId, TenantId TenantId, DateTimeOffset At, string AgentVersion, int ProtocolMajor, int ProtocolMinor,
    AgentPlatform Platform, string? OsDescription, string? RuntimeVersion, string? CapabilityRevision);

public sealed record AgentConnectivityListItem(Agent Agent, AgentConnectivityRecord? Connectivity, DateTimeOffset? EnrolledAt = null);

public enum AgentConnectivityFilter
{
    All = 0,
    Online = 1,
    Offline = 2,
    Unknown = 3
}

/// <summary>Counts of a tenant's agents: lifecycle overrides liveness, so Online/Offline/Unknown cover Active agents only.</summary>
public sealed record AgentConnectivitySummary(int Online, int Offline, int Unknown, int Disabled, int Revoked)
{
    public int Total => Online + Offline + Unknown + Disabled + Revoked;
}

/// <summary>Persistence for agent liveness metadata. Every write is a single atomic, monotonic statement.</summary>
public interface IAgentConnectivityStore
{
    /// <summary>
    /// Upserts reported metadata and moves LastSeenAt/LastHeartbeatAt forward to <see cref="HeartbeatWrite.At"/>, never
    /// backward, so replayed or reordered heartbeats are harmless and idempotent.
    /// </summary>
    Task RecordHeartbeatAsync(HeartbeatWrite write, CancellationToken cancellationToken);

    /// <summary>
    /// Records a sign of life from a poll or a push connection. Skips the write (returns false) when the stored
    /// last-seen is already within <paramref name="coalescing"/> of <paramref name="at"/>; a connect always records
    /// LastConnectedAt.
    /// </summary>
    Task<bool> TouchAsync(
        AgentId agentId, TenantId tenantId, DateTimeOffset at, bool connected, TimeSpan coalescing, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentConnectivityListItem>> ListAsync(
        TenantId tenantId, DateTimeOffset now, TimeSpan offlineTimeout, AgentConnectivityFilter filter, CancellationToken cancellationToken);

    Task<AgentConnectivityListItem?> GetAsync(TenantId tenantId, AgentId agentId, CancellationToken cancellationToken);

    Task<AgentConnectivitySummary> SummarizeAsync(
        TenantId tenantId, DateTimeOffset now, TimeSpan offlineTimeout, CancellationToken cancellationToken);

    /// <summary>Lifecycle of the given agents; unknown agents are absent from the result.</summary>
    Task<IReadOnlyDictionary<AgentId, AgentStatus>> GetLifecycleAsync(
        IReadOnlyCollection<AgentId> agentIds, CancellationToken cancellationToken);
}
