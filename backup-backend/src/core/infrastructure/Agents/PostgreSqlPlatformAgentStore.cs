using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Agents.Platform;
using BackupPlatform.Application.Lists;
using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Lists;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Agents;

/// <summary>
/// Cross-tenant agent reads under explicit platform operations. Tenant isolation is deliberately bypassed here and only
/// here for agents: the caller (a PlatformAdmin-only endpoint) is the authorization boundary.
/// </summary>
public sealed class PostgreSqlPlatformAgentStore(DbContextOptions<PlatformDbContext> options) : IPlatformAgentStore
{
    private sealed class AgentRow
    {
        public required Agent Agent { get; init; }
        public required string TenantName { get; init; }
        public AgentConnectivityRecord? Record { get; init; }
        public DateTimeOffset? EnrolledAt { get; init; }
        /// <summary>0 online, 1 offline, 2 unknown (never reported).</summary>
        public required int ConnectivityRank { get; init; }
    }

    // An agent has no creation date of its own; its first credential's issue time (enrolment) stands in, so a pending agent sorts last.
    private static readonly SortMap<AgentRow> Sorts = SortMap<AgentRow>.Create()
        .Text("hostName", row => row.Agent.HostName)
        .Text("tenantName", row => row.TenantName)
        .Value("status", row => row.Agent.Status)
        .Value("connectivity", row => row.ConnectivityRank)
        .Date("lastSeenAt", row => row.Record == null ? (DateTimeOffset?)null : row.Record.LastSeenAt)
        .Date("enrolledAt", row => row.EnrolledAt);

    public async Task<PagedResult<PlatformAgentRow>> ListAsync(
        PlatformAgentFilter filter, ListQuery listQuery, DateTimeOffset now, TimeSpan offlineTimeout, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var cutoff = (now - offlineTimeout).ToUniversalTime();
        var query = from agent in context.Agents.AsNoTracking()
                    join tenant in context.Tenants.AsNoTracking() on agent.TenantId equals tenant.Id
                    join record in context.AgentConnectivity.AsNoTracking() on agent.Id equals record.AgentId into joined
                    from record in joined.DefaultIfEmpty()
                    select new AgentRow
                    {
                        Agent = agent, TenantName = tenant.Name, Record = record,
                        EnrolledAt = context.AgentCredentials.Where(credential => credential.AgentId == agent.Id && credential.Generation == 1)
                            .Select(credential => (DateTimeOffset?)credential.IssuedAt).FirstOrDefault(),
                        ConnectivityRank = record == null ? 2 : record.LastSeenAt > cutoff ? 0 : 1
                    };
        if (filter.TenantId is { } tenantId)
        {
            query = query.Where(row => row.Agent.TenantId == tenantId);
        }
        if (filter.Status is { } status)
        {
            query = query.Where(row => row.Agent.Status == status);
        }
        query = filter.Connectivity switch
        {
            AgentConnectivityFilter.Online => query.Where(row => row.ConnectivityRank == 0),
            AgentConnectivityFilter.Offline => query.Where(row => row.ConnectivityRank == 1),
            AgentConnectivityFilter.Unknown => query.Where(row => row.ConnectivityRank == 2),
            _ => query
        };
        var page = await query
            .ApplySearch(listQuery, row => row.Agent.HostName, row => row.TenantName)
            .ApplySort(Sorts, listQuery, "enrolledAt", row => row.Agent.Id)
            .ToPagedResultAsync(listQuery, cancellationToken);
        var rows = page.Items;
        var ids = rows.Select(row => row.Agent.Id).ToArray();
        var capabilities = ids.Length == 0
            ? []
            : await context.AgentCapabilities.AsNoTracking().Where(capability => ids.Contains(capability.AgentId))
                .GroupBy(capability => capability.AgentId)
                .Select(group => new { AgentId = group.Key, Count = group.Count(), Revision = group.Max(capability => capability.Revision), ReportedAt = group.Max(capability => capability.ReportedAt) })
                .ToListAsync(cancellationToken);
        var capabilityByAgent = capabilities.ToDictionary(row => row.AgentId);
        var items = rows.Select(row =>
        {
            capabilityByAgent.TryGetValue(row.Agent.Id, out var capability);
            return new PlatformAgentRow(
                row.TenantName,
                new AgentConnectivityListItem(row.Agent, row.Record, row.EnrolledAt),
                capability?.Count ?? 0, capability?.Revision, capability?.ReportedAt);
        }).ToList();
        return new PagedResult<PlatformAgentRow>(items, page.TotalCount, page.Page, page.PageSize);
    }

    public async Task<AgentConnectivitySummary> SummarizeAsync(
        TenantId? tenantId, DateTimeOffset now, TimeSpan offlineTimeout, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var cutoff = (now - offlineTimeout).ToUniversalTime();
        var agents = context.Agents.AsNoTracking().AsQueryable();
        if (tenantId is not null)
        {
            agents = agents.Where(agent => agent.TenantId == tenantId);
        }
        var groups = await (from agent in agents
                            join record in context.AgentConnectivity.AsNoTracking() on agent.Id equals record.AgentId into joined
                            from record in joined.DefaultIfEmpty()
                            select new { agent.Status, Bucket = record == null ? 0 : record.LastSeenAt > cutoff ? 1 : 2 })
            .GroupBy(row => new { row.Status, row.Bucket })
            .Select(group => new { group.Key.Status, group.Key.Bucket, Count = group.Count() })
            .ToListAsync(cancellationToken);
        int Count(Func<AgentStatus, int, bool> predicate) => groups.Where(group => predicate(group.Status, group.Bucket)).Sum(group => group.Count);
        return new AgentConnectivitySummary(
            Online: Count((status, bucket) => status == AgentStatus.Active && bucket == 1),
            Offline: Count((status, bucket) => status == AgentStatus.Active && bucket == 2),
            Unknown: Count((status, bucket) => status is AgentStatus.Active or AgentStatus.PendingEnrollment && bucket == 0),
            Disabled: Count((status, _) => status == AgentStatus.Disabled),
            Revoked: Count((status, _) => status == AgentStatus.Revoked));
    }
}
