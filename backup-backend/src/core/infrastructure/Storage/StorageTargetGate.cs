using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Storage;

/// <summary>
/// The system default connections: every tenant has a Local one and a Managed (Central) one, materialized idempotently. The upgrade creates them
/// for existing tenants; this creates them for anything the upgrade did not see (a tenant created after it, a test that inserts tenants directly),
/// so "a replica or a job always has a destination" never depends on who created the tenant.
/// </summary>
internal static class StorageConnectionProvisioner
{
    public static async Task EnsureDefaultsAsync(PlatformDbContext context, TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var item in StorageConnectionDefaults.All)
        {
            await EnsureDefaultAsync(context, tenantId, item, now, cancellationToken);
        }
    }

    /// <summary>
    /// Creates the default for its provider type when the tenant has NO connection of that type yet (a tenant that deleted its default and made its
    /// own keeps what it made). A name collision with an unrelated connection gets a suffix instead of failing. Safe to race: whoever loses does nothing.
    /// </summary>
    public static Task<int> EnsureDefaultAsync(
        PlatformDbContext context, TenantId tenantId, DefaultStorageConnection item, DateTimeOffset now, CancellationToken cancellationToken, bool enabled = true)
    {
        var at = now.ToUniversalTime();
        var ownership = item.Ownership.ToString();
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO storage_connections ("Id", "TenantId", "Name", "NormalizedName", "ProviderType", "Ownership", "QuotaScope", "Enabled", "IsDefault",
                                             "ConfigurationJson", "SecretFieldNamesJson", "CreatedAt", "UpdatedAt")
            SELECT {Guid.NewGuid()}, t."Id", n."Name", lower(n."Name"), {item.ProviderType}, {ownership}, {item.QuotaScope}, {enabled}, true,
                   jsonb_build_object(), jsonb_build_array(), {at}, {at}
            FROM tenants t
            CROSS JOIN LATERAL (SELECT CASE WHEN EXISTS (SELECT 1 FROM storage_connections x WHERE x."TenantId" = t."Id" AND x."NormalizedName" = lower({item.Name}))
                                            THEN {item.Name} || ' (system)' ELSE {item.Name} END AS "Name") n
            WHERE t."Id" = {tenantId.Value}
              AND NOT EXISTS (SELECT 1 FROM storage_connections c WHERE c."TenantId" = t."Id" AND c."ProviderType" = {item.ProviderType})
            ON CONFLICT DO NOTHING
            """, cancellationToken);
    }

    /// <summary>
    /// The connection a run or replica with no explicit connection resolves to for a provider type: the tenant's default, else its only connection of
    /// that type. Null when the type has neither (and is not one that gets a system default), or when several non-default ones make the choice ambiguous.
    /// </summary>
    public static async Task<StorageConnection?> ResolveDefaultAsync(
        PlatformDbContext context, TenantId tenantId, string providerType, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var found = await FindAsync();
        if (found is not null || StorageConnectionDefaults.For(providerType) is not { } item)
        {
            return found;
        }
        await EnsureDefaultAsync(context, tenantId, item, now, cancellationToken);
        return await FindAsync();

        async Task<StorageConnection?> FindAsync()
        {
            var candidates = await context.StorageConnections.AsNoTracking().Where(connection => connection.TenantId == tenantId && connection.ProviderType == providerType)
                .OrderByDescending(connection => connection.IsDefault).ThenBy(connection => connection.CreatedAt).ThenBy(connection => connection.Id).Take(2).ToListAsync(cancellationToken);
            return candidates.Count switch
            {
                0 => null,
                _ when candidates[0].IsDefault => candidates[0],
                1 => candidates[0],
                _ => null
            };
        }
    }
}

internal readonly record struct StorageTargetDecision(BackupConfiguration? Configuration, string? DenialCode)
{
    public bool Allowed => DenialCode is null;
}

/// <summary>
/// Where the storage destination of a backup is decided. A NEW run resolves its connection here, and is refused when that connection is disabled
/// (<see cref="BackupAdmissionDenialCodes.StorageConnectionDisabled"/>) or does not exist for the tenant and provider
/// (<see cref="BackupAdmissionDenialCodes.StorageConnectionUnavailable"/>); the resolved id is snapshotted into the run's configuration so retries and
/// replica publication follow the destination the run was admitted against. Only new runs ask: a run already admitted keeps running and publishes
/// its replicas even if the connection was disabled meanwhile, and nothing stored on a disabled connection is touched.
/// </summary>
internal static class StorageTargetGate
{
    public static async Task<StorageTargetDecision> ForNewRunAsync(
        PlatformDbContext context, TenantId tenantId, BackupConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        StorageConnection? connection;
        if (configuration.StorageConnectionId is { } id)
        {
            connection = await context.StorageConnections.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == id, cancellationToken);
            if (connection is null || connection.ProviderType != configuration.StorageProvider)
            {
                return new(null, BackupAdmissionDenialCodes.StorageConnectionUnavailable);
            }
        }
        else
        {
            connection = await StorageConnectionProvisioner.ResolveDefaultAsync(context, tenantId, configuration.StorageProvider, now, cancellationToken);
            if (connection is null)
            {
                return new(null, BackupAdmissionDenialCodes.StorageConnectionUnavailable);
            }
        }
        return connection.EffectiveEnabled
            ? new(configuration with { StorageConnectionId = connection.Id }, null)
            : new(null, BackupAdmissionDenialCodes.StorageConnectionDisabled);
    }

    /// <summary>
    /// The connection a finished run's replicas belong to: the one its run snapshotted, or, for a run created before connections existed, the tenant's
    /// default for the provider. Never refuses (an enabled flag is irrelevant to publishing what already ran).
    /// </summary>
    public static async Task<Guid> ForPublishAsync(
        PlatformDbContext context, TenantId tenantId, BackupConfiguration configuration, string replicaProvider, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (configuration.StorageConnectionId is { } id
            && await context.StorageConnections.AsNoTracking().AnyAsync(row => row.TenantId == tenantId && row.Id == id && row.ProviderType == replicaProvider, cancellationToken))
        {
            return id;
        }
        var resolved = await StorageConnectionProvisioner.ResolveDefaultAsync(context, tenantId, replicaProvider, now, cancellationToken);
        if (resolved is null && StorageProviderTypes.IsValid(replicaProvider))
        {
            // A provider type the platform has no connection for (nothing the product writes today, the same case the upgrade handles): the replica still needs a
            // destination, so the tenant gets a DISABLED tenant-managed one of that type. Nothing new can be written to it; what is stored on it is still counted.
            await StorageConnectionProvisioner.EnsureDefaultAsync(
                context, tenantId, new DefaultStorageConnection(replicaProvider, replicaProvider, StorageOwnership.TenantManaged, StorageQuotaScopes.Tenant), now, cancellationToken, enabled: false);
            resolved = await StorageConnectionProvisioner.ResolveDefaultAsync(context, tenantId, replicaProvider, now, cancellationToken);
        }
        return resolved?.Id ?? throw new InvalidOperationException("No storage connection can hold this replica.");
    }

    /// <summary>A job may name a connection; it must be the tenant's and serve the job's provider. Everything else (enabled or not) is decided when a run starts.</summary>
    public static async Task EnsureJobTargetAsync(PlatformDbContext context, TenantId tenantId, BackupConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration.StorageConnectionId is not { } id)
        {
            return;
        }
        var matches = await context.StorageConnections.AsNoTracking()
            .AnyAsync(row => row.TenantId == tenantId && row.Id == id && row.ProviderType == configuration.StorageProvider, cancellationToken);
        if (!matches)
        {
            throw new InvalidBackupJobException("configuration.storageConnectionId", "invalid_value");
        }
    }
}
