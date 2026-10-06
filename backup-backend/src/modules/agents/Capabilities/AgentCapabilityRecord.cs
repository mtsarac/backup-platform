using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Agents.Capabilities;

/// <summary>
/// One capability of an agent's last synced snapshot. The snapshot of an agent is the set of its rows; every row of
/// one snapshot carries the same <see cref="Revision"/> and <see cref="ReportedAt"/>. What the agent reported is
/// stored as claimed: the backend validates its shape but cannot verify instance-level support.
/// </summary>
/// <remarks>Written only by <see cref="IAgentCapabilityStore"/>, which replaces the whole snapshot in one transaction.</remarks>
public sealed class AgentCapabilityRecord : ITenantOwned
{
    private AgentCapabilityRecord()
    {
        AgentId = null!;
        TenantId = null!;
        Identifier = null!;
        Revision = null!;
    }

    public AgentCapabilityRecord(
        AgentId agentId, TenantId tenantId, string identifier, CapabilitySupport support, string? metadataJson, string revision, DateTimeOffset reportedAt)
    {
        AgentId = agentId;
        TenantId = tenantId;
        Identifier = identifier;
        Support = support;
        MetadataJson = metadataJson;
        Revision = revision;
        ReportedAt = reportedAt;
    }

    public AgentId AgentId { get; private set; }
    public TenantId TenantId { get; private set; }
    public string Identifier { get; private set; }
    public CapabilitySupport Support { get; private set; }
    /// <summary>JSON object of the capability's metadata, or null when it has none.</summary>
    public string? MetadataJson { get; private set; }
    public string Revision { get; private set; }
    /// <summary>Server clock at the time the snapshot was stored.</summary>
    public DateTimeOffset ReportedAt { get; private set; }
}
