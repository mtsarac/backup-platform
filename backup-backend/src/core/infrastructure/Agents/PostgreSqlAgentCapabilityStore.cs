using System.Text.Encodings.Web;
using System.Text.Json;
using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Application.Agents;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BackupPlatform.Infrastructure.Agents;

/// <summary>
/// Capability snapshots under explicit platform operations; every statement names its tenant.
/// </summary>
/// <remarks>
/// A snapshot is replaced inside one transaction that first locks the agent's row (<c>FOR NO KEY UPDATE</c>, which
/// does not block the heartbeat's foreign-key checks). Concurrent syncs of one agent therefore run one after another:
/// the later one sees the earlier one's revision and either writes nothing or replaces the whole snapshot, so the stored
/// rows are always exactly one consistent snapshot. The row is located by tenant and agent together, so an identity can
/// never touch another tenant's rows.
/// </remarks>
public sealed class PostgreSqlAgentCapabilityStore(DbContextOptions<PlatformDbContext> options) : IAgentCapabilityStore
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string LockAgentSql = """
        SELECT "Id" AS "Value" FROM agents WHERE "Id" = @agent AND "TenantId" = @tenant FOR NO KEY UPDATE
        """;

    public async Task<ReplaceSnapshotOutcome> ReplaceSnapshotAsync(
        AgentIdentity identity, string revision, IReadOnlyCollection<Capability> capabilities, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(revision);
        ArgumentNullException.ThrowIfNull(capabilities);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var locked = await context.Database.SqlQueryRaw<Guid>(LockAgentSql,
            new NpgsqlParameter("agent", NpgsqlDbType.Uuid) { Value = identity.AgentId.Value },
            new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = identity.TenantId.Value })
            .ToListAsync(cancellationToken);
        if (locked.Count == 0)
        {
            return ReplaceSnapshotOutcome.AgentNotFound;
        }
        var stored = await context.AgentCapabilities.AsNoTracking()
            .Where(record => record.TenantId == identity.TenantId && record.AgentId == identity.AgentId)
            .Select(record => record.Revision)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.Equals(stored, revision, StringComparison.Ordinal))
        {
            return ReplaceSnapshotOutcome.Unchanged;
        }
        await context.AgentCapabilities
            .Where(record => record.TenantId == identity.TenantId && record.AgentId == identity.AgentId)
            .ExecuteDeleteAsync(cancellationToken);
        var reportedAt = now.ToUniversalTime();
        context.AgentCapabilities.AddRange(capabilities.Select(capability => new AgentCapabilityRecord(
            identity.AgentId, identity.TenantId, capability.Identifier.Value, capability.Support,
            capability.Metadata.Count == 0 ? null : JsonSerializer.Serialize(capability.Metadata, MetadataJsonOptions), revision, reportedAt)));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReplaceSnapshotOutcome.Replaced;
    }

    public async Task<string?> GetRevisionAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.AgentCapabilities.AsNoTracking()
            .Where(record => record.TenantId == identity.TenantId && record.AgentId == identity.AgentId)
            .Select(record => record.Revision)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<AgentId, AgentCapabilitySnapshot>> ListAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var rows = await context.AgentCapabilities.AsNoTracking()
            .Where(record => record.TenantId == tenantId)
            .ToListAsync(cancellationToken);
        return rows.GroupBy(row => row.AgentId).ToDictionary(group => group.Key, group => ToSnapshot(group));
    }

    public async Task<AgentCapabilitySnapshot?> GetAsync(TenantId tenantId, AgentId agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(agentId);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var rows = await context.AgentCapabilities.AsNoTracking()
            .Where(record => record.TenantId == tenantId && record.AgentId == agentId)
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : ToSnapshot(rows);
    }

    private static AgentCapabilitySnapshot ToSnapshot(IEnumerable<AgentCapabilityRecord> rows)
    {
        var ordered = rows.OrderBy(row => row.Identifier, StringComparer.Ordinal).ToList();
        var entries = ordered.Select(row => new AgentCapabilityEntry(
            row.Identifier, row.Support,
            row.MetadataJson is null
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(row.MetadataJson, MetadataJsonOptions) ?? new Dictionary<string, string>())).ToList();
        return new AgentCapabilitySnapshot(ordered[0].Revision, ordered[0].ReportedAt, entries);
    }
}
