using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Storage;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Storage;

internal static class StorageCapacityAgentRouting
{
    public static async Task<bool> CanUseSnapshotAsync(PlatformDbContext db, StorageConnection connection, DateTimeOffset now,
        AgentConnectivityOptions connectivity, CancellationToken cancellationToken)
    {
        if (!connection.EffectiveEnabled) return false;
        if (connection.ProviderType is not (StorageProviderTypes.Local or StorageProviderTypes.Sftp)) return true;
        var agent = await ResolveAsync(db, connection, now, connectivity, cancellationToken);
        if (agent is null) return false;
        var pending = await db.StorageCapacityTasks.AsNoTracking()
            .SingleOrDefaultAsync(task => task.TenantId == connection.TenantId && task.ConnectionId == connection.Id && task.CompletedAt == null, cancellationToken);
        return pending is null || pending.AgentId == agent && pending.ExpiresAt > now && (pending.Attempts < 3 || pending.LeaseExpiresAt > now);
    }

    public static async Task<Guid?> ResolveAsync(PlatformDbContext db, StorageConnection connection, DateTimeOffset now,
        AgentConnectivityOptions connectivity, CancellationToken cancellationToken)
    {
        if (!connection.EffectiveEnabled) return null;
        var local = connection.ProviderType == StorageProviderTypes.Local;
        Guid writer = Guid.Empty;
        if (local)
        {
            var jobs = await db.BackupJobs.AsNoTracking().Where(job => job.TenantId == connection.TenantId)
                .Select(job => new { job.AgentId, job.Configuration }).ToListAsync(cancellationToken);
            foreach (var job in jobs)
            {
                if (!job.Configuration.Destinations.Any(destination => destination.StorageProvider == StorageProviderTypes.Local
                    && (destination.StorageConnectionId == connection.Id || destination.StorageConnectionId is null && connection.IsDefault))) continue;
                if (writer != Guid.Empty && writer != job.AgentId.Value) return null;
                writer = job.AgentId.Value;
            }
        }
        var cutoff = now - connectivity.OfflineTimeout;
        var minimum = AgentProtocol.Current.SupportedVersions.Minimum;
        var maximum = AgentProtocol.Current.SupportedVersions.Maximum;
        var limit = local && writer == Guid.Empty ? 2 : 1;
        var agents = await db.Database.SqlQuery<Guid>($"""
            SELECT a."Id" AS "Value" FROM agents a
            JOIN agent_connectivity live ON live."AgentId" = a."Id" AND live."TenantId" = a."TenantId"
            JOIN agent_capabilities cap ON cap."AgentId" = a."Id" AND cap."TenantId" = a."TenantId"
            WHERE a."TenantId" = {connection.TenantId.Value} AND a."Status" = 2 AND live."LastSeenAt" >= {cutoff}
              AND cap."Identifier" = {CapacityProbeProtocol.Capability} AND cap."Support" = 1
              AND cap."Revision" = live."CapabilityRevision"
              AND ROW(live."ReportedProtocolMajor", live."ReportedProtocolMinor") >= ROW({minimum.Major}, {minimum.Minor})
              AND ROW(live."ReportedProtocolMajor", live."ReportedProtocolMinor") <= ROW({maximum.Major}, {maximum.Minor})
              AND ({writer} = {Guid.Empty} OR a."Id" = {writer})
            ORDER BY a."Id" LIMIT {limit}
            """).ToListAsync(cancellationToken);
        return agents.Count == 1 ? agents[0] : null;
    }
}
