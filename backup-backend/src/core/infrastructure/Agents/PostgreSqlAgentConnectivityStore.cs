using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BackupPlatform.Infrastructure.Agents;

/// <summary>
/// Liveness persistence under explicit platform operations; every statement names its tenant.
/// </summary>
/// <remarks>
/// Writes are single-statement upserts so concurrent heartbeats, polls and connects need no read-modify-write and
/// cannot lose updates. Timestamps only move forward (GREATEST) and reported metadata only changes when the writing
/// heartbeat is not older than the stored one, which makes replays and reordering harmless. The conflict clause also
/// requires the tenant to match, so a mismatched pair can never overwrite another tenant's row.
/// </remarks>
public sealed class PostgreSqlAgentConnectivityStore(DbContextOptions<PlatformDbContext> options) : IAgentConnectivityStore
{
    private const string HeartbeatSql = """
        INSERT INTO agent_connectivity AS c ("AgentId", "TenantId", "LastSeenAt", "LastHeartbeatAt", "ReportedAgentVersion",
            "ReportedProtocolMajor", "ReportedProtocolMinor", "ReportedPlatform", "ReportedOsDescription",
            "ReportedRuntimeVersion", "CapabilityRevision")
        VALUES (@agent, @tenant, @at, @at, @version, @major, @minor, @platform, @os, @runtime, @capability)
        ON CONFLICT ("AgentId") DO UPDATE SET
            "LastSeenAt" = GREATEST(c."LastSeenAt", EXCLUDED."LastSeenAt"),
            "LastHeartbeatAt" = GREATEST(c."LastHeartbeatAt", EXCLUDED."LastHeartbeatAt"),
            "ReportedAgentVersion" = CASE WHEN c."LastHeartbeatAt" IS NULL OR EXCLUDED."LastHeartbeatAt" >= c."LastHeartbeatAt" THEN EXCLUDED."ReportedAgentVersion" ELSE c."ReportedAgentVersion" END,
            "ReportedProtocolMajor" = CASE WHEN c."LastHeartbeatAt" IS NULL OR EXCLUDED."LastHeartbeatAt" >= c."LastHeartbeatAt" THEN EXCLUDED."ReportedProtocolMajor" ELSE c."ReportedProtocolMajor" END,
            "ReportedProtocolMinor" = CASE WHEN c."LastHeartbeatAt" IS NULL OR EXCLUDED."LastHeartbeatAt" >= c."LastHeartbeatAt" THEN EXCLUDED."ReportedProtocolMinor" ELSE c."ReportedProtocolMinor" END,
            "ReportedPlatform" = CASE WHEN c."LastHeartbeatAt" IS NULL OR EXCLUDED."LastHeartbeatAt" >= c."LastHeartbeatAt" THEN EXCLUDED."ReportedPlatform" ELSE c."ReportedPlatform" END,
            "ReportedOsDescription" = CASE WHEN c."LastHeartbeatAt" IS NULL OR EXCLUDED."LastHeartbeatAt" >= c."LastHeartbeatAt" THEN EXCLUDED."ReportedOsDescription" ELSE c."ReportedOsDescription" END,
            "ReportedRuntimeVersion" = CASE WHEN c."LastHeartbeatAt" IS NULL OR EXCLUDED."LastHeartbeatAt" >= c."LastHeartbeatAt" THEN EXCLUDED."ReportedRuntimeVersion" ELSE c."ReportedRuntimeVersion" END,
            "CapabilityRevision" = CASE WHEN c."LastHeartbeatAt" IS NULL OR EXCLUDED."LastHeartbeatAt" >= c."LastHeartbeatAt" THEN EXCLUDED."CapabilityRevision" ELSE c."CapabilityRevision" END
        WHERE c."TenantId" = EXCLUDED."TenantId"
        """;

    private const string TouchSql = """
        INSERT INTO agent_connectivity AS c ("AgentId", "TenantId", "LastSeenAt", "LastConnectedAt")
        VALUES (@agent, @tenant, @at, CASE WHEN @connected THEN @at END)
        ON CONFLICT ("AgentId") DO UPDATE SET
            "LastSeenAt" = GREATEST(c."LastSeenAt", EXCLUDED."LastSeenAt"),
            "LastConnectedAt" = CASE WHEN @connected THEN GREATEST(c."LastConnectedAt", EXCLUDED."LastConnectedAt") ELSE c."LastConnectedAt" END
        WHERE c."TenantId" = EXCLUDED."TenantId"
            AND (@connected OR c."LastSeenAt" < EXCLUDED."LastSeenAt" - make_interval(secs => @coalesceSeconds))
        """;

    public async Task RecordHeartbeatAsync(HeartbeatWrite write, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await context.Database.ExecuteSqlRawAsync(HeartbeatSql,
        [
            new NpgsqlParameter("agent", NpgsqlDbType.Uuid) { Value = write.AgentId.Value },
            new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = write.TenantId.Value },
            new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = write.At.ToUniversalTime() },
            new NpgsqlParameter("version", NpgsqlDbType.Varchar) { Value = write.AgentVersion },
            new NpgsqlParameter("major", NpgsqlDbType.Integer) { Value = write.ProtocolMajor },
            new NpgsqlParameter("minor", NpgsqlDbType.Integer) { Value = write.ProtocolMinor },
            new NpgsqlParameter("platform", NpgsqlDbType.Integer) { Value = (int)write.Platform },
            Nullable("os", NpgsqlDbType.Varchar, write.OsDescription),
            Nullable("runtime", NpgsqlDbType.Varchar, write.RuntimeVersion),
            Nullable("capability", NpgsqlDbType.Varchar, write.CapabilityRevision)
        ], cancellationToken);
    }

    public async Task<bool> TouchAsync(
        AgentId agentId, TenantId tenantId, DateTimeOffset at, bool connected, TimeSpan coalescing, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var affected = await context.Database.ExecuteSqlRawAsync(TouchSql,
        [
            new NpgsqlParameter("agent", NpgsqlDbType.Uuid) { Value = agentId.Value },
            new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = tenantId.Value },
            new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at.ToUniversalTime() },
            new NpgsqlParameter("connected", NpgsqlDbType.Boolean) { Value = connected },
            new NpgsqlParameter("coalesceSeconds", NpgsqlDbType.Double) { Value = coalescing.TotalSeconds }
        ], cancellationToken);
        return affected > 0;
    }

    public async Task<IReadOnlyList<AgentConnectivityListItem>> ListAsync(
        TenantId tenantId, DateTimeOffset now, TimeSpan offlineTimeout, AgentConnectivityFilter filter, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var cutoff = (now - offlineTimeout).ToUniversalTime();
        var query = from agent in context.Agents.AsNoTracking()
                    where agent.TenantId == tenantId
                    join record in context.AgentConnectivity.AsNoTracking() on agent.Id equals record.AgentId into joined
                    from record in joined.DefaultIfEmpty()
                    select new
                    {
                        Agent = agent,
                        Record = record,
                        EnrolledAt = context.AgentCredentials
                            .Where(credential => credential.TenantId == tenantId && credential.AgentId == agent.Id && credential.Generation == 1)
                            .Select(credential => (DateTimeOffset?)credential.IssuedAt).SingleOrDefault()
                    };
        // Online is defined as now - lastSeen < timeout, i.e. lastSeen > cutoff; see AgentConnectivityEvaluator.
        query = filter switch
        {
            AgentConnectivityFilter.Online => query.Where(row => row.Record != null && row.Record.LastSeenAt > cutoff),
            AgentConnectivityFilter.Offline => query.Where(row => row.Record != null && row.Record.LastSeenAt <= cutoff),
            AgentConnectivityFilter.Unknown => query.Where(row => row.Record == null),
            _ => query
        };
        var rows = await query.OrderBy(row => row.Agent.HostName).ThenBy(row => row.Agent.Id).ToListAsync(cancellationToken);
        return rows.Select(row => new AgentConnectivityListItem(row.Agent, row.Record, row.EnrolledAt)).ToList();
    }

    public async Task<AgentConnectivityListItem?> GetAsync(TenantId tenantId, AgentId agentId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var row = await (from agent in context.Agents.AsNoTracking()
                         where agent.TenantId == tenantId && agent.Id == agentId
                         join record in context.AgentConnectivity.AsNoTracking() on agent.Id equals record.AgentId into joined
                         from record in joined.DefaultIfEmpty()
                         select new
                         {
                             Agent = agent,
                             Record = record,
                             EnrolledAt = context.AgentCredentials
                                 .Where(credential => credential.TenantId == tenantId && credential.AgentId == agentId && credential.Generation == 1)
                                 .Select(credential => (DateTimeOffset?)credential.IssuedAt).SingleOrDefault()
                         }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new AgentConnectivityListItem(row.Agent, row.Record, row.EnrolledAt);
    }

    public async Task<AgentConnectivitySummary> SummarizeAsync(
        TenantId tenantId, DateTimeOffset now, TimeSpan offlineTimeout, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var cutoff = (now - offlineTimeout).ToUniversalTime();
        var groups = await (from agent in context.Agents.AsNoTracking()
                            where agent.TenantId == tenantId
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

    public async Task<IReadOnlyDictionary<AgentId, AgentStatus>> GetLifecycleAsync(
        IReadOnlyCollection<AgentId> agentIds, CancellationToken cancellationToken)
    {
        if (agentIds.Count == 0)
        {
            return new Dictionary<AgentId, AgentStatus>();
        }
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var ids = agentIds.ToArray();
        var rows = await context.Agents.AsNoTracking()
            .Where(agent => ids.Contains(agent.Id))
            .Select(agent => new { agent.Id, agent.Status })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Id, row => row.Status);
    }

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, string? value) =>
        new(name, type) { Value = (object?)value ?? DBNull.Value };
}
