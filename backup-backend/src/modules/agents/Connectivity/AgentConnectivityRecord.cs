using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Agents.Connectivity;

/// <summary>
/// Operational liveness metadata for one agent, kept apart from the <see cref="Agent"/> lifecycle model. At most one
/// row per agent; no row means the agent has never been seen. Online/Offline is never stored: it is derived from
/// <see cref="LastSeenAt"/> and the server clock, so a silent agent costs no writes.
/// </summary>
/// <remarks>
/// Written only by atomic monotonic upserts in the store (a late or replayed request can never move timestamps back),
/// never through change tracking. Reported values are what the agent last claimed, not facts the backend verified.
/// Connection identifiers are ephemeral and are deliberately not persisted.
/// </remarks>
public sealed class AgentConnectivityRecord : ITenantOwned
{
    private AgentConnectivityRecord()
    {
        AgentId = null!;
        TenantId = null!;
    }

    public AgentId AgentId { get; private set; }
    public TenantId TenantId { get; private set; }
    /// <summary>Last authenticated sign of life (heartbeat, poll or push connect), by the server clock.</summary>
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset? LastHeartbeatAt { get; private set; }
    public DateTimeOffset? LastConnectedAt { get; private set; }
    public string? ReportedAgentVersion { get; private set; }
    public int? ReportedProtocolMajor { get; private set; }
    public int? ReportedProtocolMinor { get; private set; }
    public AgentPlatform? ReportedPlatform { get; private set; }
    public string? ReportedOsDescription { get; private set; }
    public string? ReportedRuntimeVersion { get; private set; }
    public string? CapabilityRevision { get; private set; }
}
