using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Agents.Connectivity;

public sealed record AgentStatusView(
    Agent Agent,
    AgentConnectivityState Connectivity,
    AgentOperationalStatus Status,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? LastConnectedAt,
    string? ReportedAgentVersion,
    string? ReportedProtocolVersion,
    string? CapabilityRevision,
    string? OsDescription,
    string? RuntimeVersion,
    DateTimeOffset? EnrolledAt,
    AgentCapabilitySnapshot? Capabilities);

/// <summary>Tenant-scoped, read-only view of agent liveness. The caller must already have authorized a tenant viewer.</summary>
public sealed class AgentConnectivityQueryService(
    IAgentConnectivityStore store, IAgentCapabilityStore capabilityStore, TimeProvider timeProvider, AgentConnectivityOptions options,
    AgentProtocolGate gate)
{
    public async Task<IReadOnlyList<AgentStatusView>> ListAsync(
        TenantId tenantId, AgentConnectivityFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        var now = timeProvider.GetUtcNow();
        var items = await store.ListAsync(tenantId, now, options.OfflineTimeout, filter, cancellationToken).ConfigureAwait(false);
        var snapshots = await capabilityStore.ListAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return items.Select(item => ToView(item, now, snapshots.GetValueOrDefault(item.Agent.Id))).ToList();
    }

    public async Task<AgentStatusView?> GetAsync(TenantId tenantId, AgentId agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(agentId);
        var item = await store.GetAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }
        var snapshot = await capabilityStore.GetAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        return ToView(item, timeProvider.GetUtcNow(), snapshot);
    }

    public Task<AgentConnectivitySummary> SummarizeAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        return store.SummarizeAsync(tenantId, timeProvider.GetUtcNow(), options.OfflineTimeout, cancellationToken);
    }

    /// <summary>Derives the display state of one stored agent row; used by the tenant and the platform views alike.</summary>
    public AgentStatusView ToView(AgentConnectivityListItem item, DateTimeOffset now, AgentCapabilitySnapshot? capabilities)
    {
        var record = item.Connectivity;
        var connectivity = AgentConnectivityEvaluator.Evaluate(record?.LastSeenAt, now, options.OfflineTimeout);
        bool? supported = record is { ReportedProtocolMajor: { } major, ReportedProtocolMinor: { } minor } ? gate.IsSupported(major, minor) : null;
        return new AgentStatusView(
            item.Agent, connectivity, AgentConnectivityEvaluator.EvaluateOperational(item.Agent.Status, connectivity, supported),
            record?.LastSeenAt, record?.LastHeartbeatAt, record?.LastConnectedAt,
            record?.ReportedAgentVersion ?? item.Agent.Software.Version.ToString(),
            record?.ReportedProtocolMajor is { } m && record.ReportedProtocolMinor is { } n
                ? $"{m}.{n}"
                : $"{item.Agent.Software.ProtocolVersion.Major}.{item.Agent.Software.ProtocolVersion.Minor}",
            record?.CapabilityRevision, record?.ReportedOsDescription, record?.ReportedRuntimeVersion, item.EnrolledAt, capabilities);
    }
}
