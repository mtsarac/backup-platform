using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BackupPlatform.Infrastructure.Storage;

/// <summary>
/// Connection persistence under explicit platform operations; the endpoints that call it are the authorization boundary and always pass the tenant
/// they authorized, so a tenant-scoped lookup of another tenant's id finds nothing.
/// </summary>
/// <remarks>
/// Every write takes the tenant lock (<c>FOR NO KEY UPDATE</c>, the same lock quota decisions take) before it reads, so connection changes, run
/// admissions and usage recomputation for one tenant are serialized on any node. Audit events are written in the change's own transaction. A change
/// that alters what quota counts (the quota scope label) recomputes the tenant's usage projection in that transaction, so the projection never lags
/// behind the label it was computed from.
/// </remarks>
public sealed class PostgreSqlStorageConnectionStore(
    DbContextOptions<PlatformDbContext> options, TimeProvider time, ILogger<PostgreSqlStorageConnectionStore> logger) : IStorageConnectionStore
{
    public async Task<IReadOnlyList<StorageConnectionRecord>?> ListAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
        {
            return null;
        }
        await StorageConnectionProvisioner.EnsureDefaultsAsync(context, tenantId, time.GetUtcNow(), cancellationToken);
        var rows = await Records(context).Where(row => row.Connection.TenantId == tenantId).OrderBy(row => row.Connection.NormalizedName).ThenBy(row => row.Connection.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<PagedResult<StorageConnectionRecord>> SearchAsync(StorageConnectionFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var rows = Records(context);
        if (filter.Ownership is { } ownership)
        {
            rows = rows.Where(row => row.Connection.Ownership == ownership);
        }
        if (!string.IsNullOrWhiteSpace(filter.ProviderType))
        {
            var provider = filter.ProviderType.Trim().ToLowerInvariant();
            rows = rows.Where(row => row.Connection.ProviderType == provider);
        }
        if (filter.TenantId is { } tenantId)
        {
            rows = rows.Where(row => row.Connection.TenantId == tenantId);
        }
        if (filter.Enabled is { } enabled)
        {
            rows = rows.Where(row => row.Connection.Enabled && !row.Connection.AdministrativelyDisabled == enabled);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLowerInvariant();
            rows = rows.Where(row => row.Connection.NormalizedName.Contains(term) || row.TenantName.ToLower().Contains(term));
        }
        var total = await rows.LongCountAsync(cancellationToken);
        var items = await rows.OrderBy(row => row.TenantName).ThenBy(row => row.Connection.NormalizedName).ThenBy(row => row.Connection.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<StorageConnectionRecord>(items.Select(ToRecord).ToList(), total, page, pageSize);
    }

    public async Task<StorageConnectionRecord?> FindAsync(Guid id, TenantId? tenantScope, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await ReadAsync(context, id, tenantScope, cancellationToken);
    }

    public async Task<StorageConnectionResult> CreateAsync(
        StorageConnection connection, int? maxPerTenant, StorageActor actor, IReadOnlyDictionary<string, object?> auditDetails, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await TenantUsageEngine.LockTenantAsync(context, connection.TenantId, cancellationToken))
        {
            return new(StorageConnectionOutcome.TenantNotFound);
        }
        if (maxPerTenant is { } max
            && await context.StorageConnections.CountAsync(row => row.TenantId == connection.TenantId && row.ProviderType == connection.ProviderType, cancellationToken) >= max)
        {
            return new(StorageConnectionOutcome.LimitReached);
        }
        if (await context.StorageConnections.AnyAsync(row => row.TenantId == connection.TenantId && row.NormalizedName == connection.NormalizedName, cancellationToken))
        {
            return new(StorageConnectionOutcome.NameTaken);
        }
        context.StorageConnections.Add(connection);
        context.AuditEvents.Add(Audit(now, actor, StorageConnectionAuditActions.Created, connection.TenantId, connection.Id, auditDetails));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return new(StorageConnectionOutcome.NameTaken);
        }
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Storage connection {ConnectionId} ({ProviderType}, {Ownership}) created for tenant {TenantId}.", connection.Id, connection.ProviderType, connection.Ownership, connection.TenantId);
        return new(StorageConnectionOutcome.Changed, await ReadAsync(context, connection.Id, connection.TenantId, cancellationToken));
    }

    public async Task<StorageConnectionResult> MutateAsync(
        Guid id, TenantId? tenantScope, StorageMutation mutation, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await OwnerAsync(context, id, tenantScope, cancellationToken);
        if (owner is null)
        {
            return new(StorageConnectionOutcome.NotFound);
        }
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await TenantUsageEngine.LockTenantAsync(context, owner, cancellationToken))
        {
            return new(StorageConnectionOutcome.NotFound);
        }
        var connection = await context.StorageConnections.SingleOrDefaultAsync(row => row.TenantId == owner && row.Id == id, cancellationToken);
        if (connection is null)
        {
            return new(StorageConnectionOutcome.NotFound);
        }
        var previousConfiguration = connection.Configuration;
        var previousSecret = connection.SecretReference;
        var result = mutation(connection, now);
        if (result.RejectionCode is not null)
        {
            return new(StorageConnectionOutcome.Rejected, Code: result.RejectionCode);
        }
        if (connection.ProviderType == StorageProviderTypes.Sftp)
        {
            // This is checked under the admission lock, not against an earlier view: a new run cannot race a location change.
            string[] locationFields = ["host", "port", "username", "authenticationType", "remoteBaseDirectory"];
            var movesStorage = locationFields.Any(field => previousConfiguration.GetValueOrDefault(field) != connection.Configuration.GetValueOrDefault(field));
            var clearsCredentials = previousSecret is not null && connection.SecretReference is null;
            if ((movesStorage || clearsCredentials) && await HasReferencesAsync(context, owner, id, cancellationToken))
                return new(StorageConnectionOutcome.Rejected, Code: "connection_in_use");
        }
        if (connection.ProviderType == StorageProviderTypes.GoogleDrive)
        {
            var movesStorage = previousConfiguration.GetValueOrDefault("folderId") != connection.Configuration.GetValueOrDefault("folderId");
            var clearsCredentials = previousSecret is not null && connection.SecretReference is null;
            if ((movesStorage || clearsCredentials) && await HasReferencesAsync(context, owner, id, cancellationToken))
                return new(StorageConnectionOutcome.Rejected, Code: "connection_in_use");
        }
        if (connection.ProviderType is StorageProviderTypes.S3 or StorageProviderTypes.Smb)
        {
            string[] fields = connection.ProviderType == StorageProviderTypes.S3 ? ["endpoint", "bucket", "region"] : ["host", "share"];
            var movesStorage = fields.Any(field => previousConfiguration.GetValueOrDefault(field) != connection.Configuration.GetValueOrDefault(field));
            var clearsCredentials = previousSecret is not null && connection.SecretReference is null;
            if ((movesStorage || clearsCredentials) && await HasReferencesAsync(context, owner, id, cancellationToken))
                return new(StorageConnectionOutcome.Rejected, Code: "connection_in_use");
        }
        if (!result.Changed)
        {
            return new(StorageConnectionOutcome.Unchanged, await ReadAsync(context, id, owner, cancellationToken));
        }
        foreach (var entry in result.Audit ?? [])
        {
            context.AuditEvents.Add(Audit(now, actor, entry.Action, owner, id, entry.Details));
        }
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            if (result.AffectsQuota)
            {
                await RecalculateUsageAsync(context, owner, now, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(StorageConnectionOutcome.ConcurrentUpdate);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return new(StorageConnectionOutcome.NameTaken);
        }
        return new(StorageConnectionOutcome.Changed, await ReadAsync(context, id, owner, cancellationToken));
    }

    public async Task<StorageConnectionResult> DeleteAsync(Guid id, TenantId? tenantScope, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await OwnerAsync(context, id, tenantScope, cancellationToken);
        if (owner is null)
        {
            return new(StorageConnectionOutcome.NotFound);
        }
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await TenantUsageEngine.LockTenantAsync(context, owner, cancellationToken))
        {
            return new(StorageConnectionOutcome.NotFound);
        }
        var connection = await context.StorageConnections.SingleOrDefaultAsync(row => row.TenantId == owner && row.Id == id, cancellationToken);
        if (connection is null)
        {
            return new(StorageConnectionOutcome.NotFound);
        }
        if (actor.Kind != AuditActorKind.PlatformAdmin && connection.AdministrativelyDisabled)
        {
            return new(StorageConnectionOutcome.Rejected, Code: "administratively_disabled");
        }
        var referenced = await HasReferencesAsync(context, owner, id, cancellationToken);
        if (referenced)
        {
            return new(StorageConnectionOutcome.InUse);
        }
        var record = new StorageConnectionRecord(connection, null, 0, 0);
        context.StorageConnections.Remove(connection);
        context.AuditEvents.Add(Audit(now, actor, StorageConnectionAuditActions.Deleted, owner, id, new Dictionary<string, object?>
        {
            ["name"] = connection.Name,
            ["providerType"] = connection.ProviderType,
            ["ownership"] = connection.Ownership.ToString(),
            ["quotaScope"] = connection.QuotaScope
        }));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(StorageConnectionOutcome.ConcurrentUpdate);
        }
        catch (DbUpdateException)
        {
            // The composite foreign key of a replica inserted after the check above: the connection is in use after all.
            return new(StorageConnectionOutcome.InUse);
        }
        return new(StorageConnectionOutcome.Changed, record);
    }

    private static async Task<bool> HasReferencesAsync(PlatformDbContext context, TenantId owner, Guid id, CancellationToken cancellationToken)
    {
        if (await context.BackupReplicas.AnyAsync(replica => replica.TenantId == owner && replica.StorageConnectionId == id, cancellationToken))
            return true;
        var text = id.ToString("D");
        return await context.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM backup_jobs job, jsonb_array_elements(job."Configuration" -> 'Destinations') destination
                           WHERE job."TenantId" = {owner.Value} AND destination ->> 'StorageConnectionId' = {text})
                OR EXISTS (SELECT 1 FROM backup_runs run, jsonb_array_elements(run."Configuration" -> 'Destinations') destination
                           WHERE run."TenantId" = {owner.Value} AND destination ->> 'StorageConnectionId' = {text}) AS "Value"
            """).SingleAsync(cancellationToken);
    }

    /// <summary>Under the tenant lock: usage recomputed from the sources with the tenant's current policy, written in the caller's transaction.</summary>
    private static async Task RecalculateUsageAsync(PlatformDbContext context, TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var policy = await TenantUsageEngine.ResolvePolicyAsync(context, tenantId, cancellationToken);
        var used = await TenantUsageEngine.ComputeUsedAsync(context, tenantId, policy, cancellationToken);
        var reserved = await TenantUsageEngine.ComputeReservedAsync(context, tenantId, cancellationToken);
        await TenantUsageEngine.WriteAsync(context, tenantId, policy, used, reserved, now, cancellationToken);
    }

    private static async Task<TenantId?> OwnerAsync(PlatformDbContext context, Guid id, TenantId? tenantScope, CancellationToken cancellationToken) =>
        await context.StorageConnections.AsNoTracking().Where(row => row.Id == id && (tenantScope == null || row.TenantId == tenantScope))
            .Select(row => row.TenantId).SingleOrDefaultAsync(cancellationToken);

    private static async Task<StorageConnectionRecord?> ReadAsync(PlatformDbContext context, Guid id, TenantId? tenantScope, CancellationToken cancellationToken)
    {
        var row = await Records(context).Where(item => item.Connection.Id == id && (tenantScope == null || item.Connection.TenantId == tenantScope)).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    private sealed class Row
    {
        public required StorageConnection Connection { get; init; }
        public required string TenantName { get; init; }
        public int ReplicaCount { get; init; }
        public long? StoredBytes { get; init; }
    }

    private static StorageConnectionRecord ToRecord(Row row) => new(row.Connection, row.TenantName, row.ReplicaCount, row.StoredBytes ?? 0);

    /// <summary>Connections with the tenant's name and the physical footprint: replicas still occupying storage on each (the shared definition, <see cref="BackupReplicaStorage"/>).</summary>
    private static IQueryable<Row> Records(PlatformDbContext context)
    {
        var occupying = BackupReplicaStorage.OccupyingStatuses.ToArray();
        return context.StorageConnections.AsNoTracking().Select(connection => new Row
        {
            Connection = connection,
            TenantName = context.Tenants.Where(tenant => tenant.Id == connection.TenantId).Select(tenant => tenant.Name).FirstOrDefault()!,
            ReplicaCount = context.BackupReplicas.Count(replica => replica.TenantId == connection.TenantId && replica.StorageConnectionId == connection.Id && occupying.Contains(replica.Status)),
            StoredBytes = context.BackupReplicas.Where(replica => replica.TenantId == connection.TenantId && replica.StorageConnectionId == connection.Id && occupying.Contains(replica.Status))
                .Sum(replica => (long?)replica.SizeBytes)
        });
    }

    private static AuditEvent Audit(DateTimeOffset now, StorageActor actor, string action, TenantId tenantId, Guid connectionId, IReadOnlyDictionary<string, object?> details) =>
        AuditEvent.Record(now, actor.UserId, actor.Kind, action, tenantId, StorageConnectionAuditActions.TargetType, connectionId.ToString("D"), details);

    private static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
