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
        var agents = await ResolveAsync(db, connection, now, connectivity, cancellationToken);
        if (agents.Count == 0) return false;
        var pending = await db.StorageCapacityTasks.AsNoTracking()
            .Where(task => task.TenantId == connection.TenantId && task.ConnectionId == connection.Id && task.CompletedAt == null).ToListAsync(cancellationToken);
        return pending.All(task => agents.Contains(task.AgentId) && task.ExpiresAt > now && (task.Attempts < 3 || task.LeaseExpiresAt > now));
    }

    /// <summary>
    /// The online, probe-capable agents whose view of the connection counts. A local connection is the disk of every agent whose jobs write to it, so each of those that is
    /// online is probed (the lowest free space is reported); one nobody writes to yet belongs to the only agent there is. Any other provider is probed by one agent.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ResolveAsync(PlatformDbContext db, StorageConnection connection, DateTimeOffset now,
        AgentConnectivityOptions connectivity, CancellationToken cancellationToken)
    {
        if (!connection.EffectiveEnabled) return [];
        var local = connection.ProviderType == StorageProviderTypes.Local;
        var writers = Array.Empty<Guid>();
        if (local)
        {
            var jobs = await db.BackupJobs.AsNoTracking().Where(job => job.TenantId == connection.TenantId)
                .Select(job => new { job.AgentId, job.Configuration }).ToListAsync(cancellationToken);
            writers = [.. jobs.Where(job => job.Configuration.Destinations.Any(destination => destination.StorageProvider == StorageProviderTypes.Local
                    && (destination.StorageConnectionId == connection.Id || destination.StorageConnectionId is null && connection.IsDefault)))
                .Select(job => job.AgentId.Value).Distinct()];
        }
        var cutoff = now - connectivity.OfflineTimeout;
        var minimum = AgentProtocol.Current.SupportedVersions.Minimum;
        var maximum = AgentProtocol.Current.SupportedVersions.Maximum;
        var anyWriter = writers.Length == 0;
        var limit = local && anyWriter ? 2 : local ? writers.Length : 1;
        var agents = await db.Database.SqlQuery<Guid>($"""
            SELECT a."Id" AS "Value" FROM agents a
            JOIN agent_connectivity live ON live."AgentId" = a."Id" AND live."TenantId" = a."TenantId"
            JOIN agent_capabilities cap ON cap."AgentId" = a."Id" AND cap."TenantId" = a."TenantId"
            WHERE a."TenantId" = {connection.TenantId.Value} AND a."Status" = 2 AND live."LastSeenAt" >= {cutoff}
              AND cap."Identifier" = {CapacityProbeProtocol.Capability} AND cap."Support" = 1
              AND cap."Revision" = live."CapabilityRevision"
              AND ROW(live."ReportedProtocolMajor", live."ReportedProtocolMinor") >= ROW({minimum.Major}, {minimum.Minor})
              AND ROW(live."ReportedProtocolMajor", live."ReportedProtocolMinor") <= ROW({maximum.Major}, {maximum.Minor})
              AND ({anyWriter} OR a."Id" = ANY({writers}))
            ORDER BY a."Id" LIMIT {limit}
            """).ToListAsync(cancellationToken);
        // Nobody writes yet and several agents could: the connection has no single disk to measure.
        return local && anyWriter && agents.Count != 1 ? [] : agents;
    }
}
