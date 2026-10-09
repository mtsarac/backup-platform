using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Agents.Platform;

public sealed record PlatformAgentFilter(TenantId? TenantId, AgentConnectivityFilter Connectivity, AgentStatus? Status, string? Search);

/// <summary>One agent of the cross-tenant list. The capability columns summarize the stored snapshot, not the snapshot itself.</summary>
public sealed record PlatformAgentRow(
    string TenantName, AgentConnectivityListItem Item, int CapabilityCount, string? CapabilityRevision, DateTimeOffset? CapabilitiesReportedAt);

public sealed record PlatformAgentView(
    string TenantName, AgentStatusView Agent, int CapabilityCount, string? CapabilityRevision, DateTimeOffset? CapabilitiesReportedAt);

/// <summary>Cross-tenant agent reads. Implementations use the explicit platform operations context and name no ambient tenant.</summary>
public interface IPlatformAgentStore
{
    Task<PagedResult<PlatformAgentRow>> ListAsync(
        PlatformAgentFilter filter, int page, int pageSize, DateTimeOffset now, TimeSpan offlineTimeout, CancellationToken cancellationToken);

    /// <summary>Counts of the agents of one tenant, or of every tenant when <paramref name="tenantId"/> is null.</summary>
    Task<AgentConnectivitySummary> SummarizeAsync(TenantId? tenantId, DateTimeOffset now, TimeSpan offlineTimeout, CancellationToken cancellationToken);
}

/// <summary>Read-only platform view of agents. The caller must already have authorized a PlatformAdmin.</summary>
public sealed class PlatformAgentQueryService(
    IPlatformAgentStore store, AgentConnectivityQueryService connectivity, TimeProvider timeProvider, AgentConnectivityOptions options)
{
    public async Task<PagedResult<PlatformAgentView>> ListAsync(
        PlatformAgentFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var now = timeProvider.GetUtcNow();
        var rows = await store.ListAsync(filter, page, pageSize, now, options.OfflineTimeout, cancellationToken).ConfigureAwait(false);
        return new PagedResult<PlatformAgentView>(
            rows.Items.Select(row => new PlatformAgentView(
                row.TenantName, connectivity.ToView(row.Item, now, null), row.CapabilityCount, row.CapabilityRevision, row.CapabilitiesReportedAt)).ToList(),
            rows.TotalCount, rows.Page, rows.PageSize);
    }

    public Task<AgentConnectivitySummary> SummarizeAsync(TenantId? tenantId, CancellationToken cancellationToken) =>
        store.SummarizeAsync(tenantId, timeProvider.GetUtcNow(), options.OfflineTimeout, cancellationToken);
}
