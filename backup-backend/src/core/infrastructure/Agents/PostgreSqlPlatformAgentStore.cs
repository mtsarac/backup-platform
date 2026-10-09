using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Agents.Platform;
using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Agents;

/// <summary>
/// Cross-tenant agent reads under explicit platform operations. Tenant isolation is deliberately bypassed here and only
/// here for agents: the caller (a PlatformAdmin-only endpoint) is the authorization boundary.
/// </summary>
public sealed class PostgreSqlPlatformAgentStore(DbContextOptions<PlatformDbContext> options) : IPlatformAgentStore
{
    public async Task<PagedResult<PlatformAgentRow>> ListAsync(
        PlatformAgentFilter filter, int page, int pageSize, DateTimeOffset now, TimeSpan offlineTimeout, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var cutoff = (now - offlineTimeout).ToUniversalTime();
        var query = from agent in context.Agents.AsNoTracking()
                    join tenant in context.Tenants.AsNoTracking() on agent.TenantId equals tenant.Id
                    join record in context.AgentConnectivity.AsNoTracking() on agent.Id equals record.AgentId into joined
                    from record in joined.DefaultIfEmpty()
                    select new { Agent = agent, TenantName = tenant.Name, Record = record };
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
            AgentConnectivityFilter.Online => query.Where(row => row.Record != null && row.Record.LastSeenAt > cutoff),
            AgentConnectivityFilter.Offline => query.Where(row => row.Record != null && row.Record.LastSeenAt <= cutoff),
            AgentConnectivityFilter.Unknown => query.Where(row => row.Record == null),
            _ => query
        };
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLowerInvariant();
            query = query.Where(row => (row.Agent.HostName != null && row.Agent.HostName.ToLower().Contains(term)) || row.TenantName.ToLower().Contains(term));
        }
        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.TenantName).ThenBy(row => row.Agent.HostName).ThenBy(row => row.Agent.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize).ToListAsync(cancellationToken);
        var ids = rows.Select(row => row.Agent.Id).ToArray();
        var enrolled = ids.Length == 0
            ? []
            : await context.AgentCredentials.AsNoTracking().Where(credential => credential.Generation == 1 && ids.Contains(credential.AgentId))
                .Select(credential => new { credential.AgentId, credential.IssuedAt }).ToListAsync(cancellationToken);
        var capabilities = ids.Length == 0
            ? []
            : await context.AgentCapabilities.AsNoTracking().Where(capability => ids.Contains(capability.AgentId))
                .GroupBy(capability => capability.AgentId)
                .Select(group => new { AgentId = group.Key, Count = group.Count(), Revision = group.Max(capability => capability.Revision), ReportedAt = group.Max(capability => capability.ReportedAt) })
                .ToListAsync(cancellationToken);
        var enrolledAt = enrolled.ToDictionary(row => row.AgentId, row => (DateTimeOffset?)row.IssuedAt);
        var capabilityByAgent = capabilities.ToDictionary(row => row.AgentId);
        var items = rows.Select(row =>
        {
            capabilityByAgent.TryGetValue(row.Agent.Id, out var capability);
            return new PlatformAgentRow(
                row.TenantName,
                new AgentConnectivityListItem(row.Agent, row.Record, enrolledAt.GetValueOrDefault(row.Agent.Id)),
                capability?.Count ?? 0, capability?.Revision, capability?.ReportedAt);
        }).ToList();
        return new PagedResult<PlatformAgentRow>(items, total, page, pageSize);
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
