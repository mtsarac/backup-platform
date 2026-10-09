using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Secrets;
using BackupPlatform.Infrastructure.Storage;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.SecretProviders.DataProtection;
using BackupPlatform.Storage;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

/// <summary>
/// A provider that exists only in tests: the built-in descriptors carry no secret fields, so this one (one required config field, one required
/// secret field) lets the suites prove that a credential value never reaches a view, an audit event or a row.
/// </summary>
internal static class VaultProvider
{
    public const string Type = "vault";
    public const string Host = "host";
    public const string Password = "password";

    public static StorageProviderDescriptor Descriptor { get; } = new(
        Type, "Vault (test)", StorageProviderAvailability.Available, StorageTopology.BackendRelayed, BackupStorageExecutionLocation.Backend, ["storage.read"],
        [StorageOwnership.TenantManaged], tenantCreatable: true, platformCreatable: false, maxConnectionsPerTenant: null,
        [new StorageFieldDefinition(Host, "Host", StorageFieldKind.Text, true, 64)], [new StorageSecretFieldDefinition(Password, "Password", true)], StorageTestMode.BackendRoundTrip);

    public static IStorageProviderCatalog Catalog() => new StorageProviderCatalog([.. BuiltInStorageProviders.All, Descriptor]);
}

/// <summary>Succeeds only when the stored secret resolves, and never repeats it: what a real tester does with credentials.</summary>
internal sealed class VaultTester : IStorageConnectionTester
{
    public string ProviderType => VaultProvider.Type;

    public async Task<StorageTestResult> TestAsync(StorageConnection connection, ISecretProvider secrets, CancellationToken cancellationToken)
    {
        if (connection.SecretReference is null)
        {
            return new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.InvalidConfiguration, "No credentials.");
        }
        var value = await secrets.ResolveAsync(new SecretReference(connection.SecretReference), cancellationToken);
        return value.Reveal().Length > 0
            ? new StorageTestResult(StorageTestStatus.Succeeded, StorageTestCodes.Ok, "Resolved the credentials.")
            : new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.SecretUnavailable, "Empty.");
    }
}

/// <summary>
/// Real stores, real PostgreSQL, a real Data Protection secret store. Every method returns independent objects (own pool) so racing callers behave
/// like separate backend nodes.
/// </summary>
internal sealed class ConnectionWorld(PostgreSqlFixture database)
{
    public static readonly DateTimeOffset Now = QuotaWorld.T0;

    public QuotaWorld Quota { get; } = new(database);
    public PostgreSqlFixture Database { get; } = database;
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();

    public PostgreSqlStorageConnectionStore Store() => new(Quota.Options(), TimeProvider.System, NullLogger<PostgreSqlStorageConnectionStore>.Instance);

    public PostgreSqlSecretCiphertextRepository Ciphertexts() => new(Quota.Options(), TimeProvider.System);

    public DataProtectionSecretStore Secrets(IDataProtectionProvider? protection = null) => new(protection ?? _protection, Ciphertexts());

    public StorageConnectionService Service(ISecretStore? secrets = null) => new(
        Store(), VaultProvider.Catalog(), secrets ?? Secrets(), [new AgentSideConnectionTester(StorageProviderTypes.Local), new VaultTester()], TimeProvider.System,
        NullLogger<StorageConnectionService>.Instance);

    public async Task<StorageActor> PlatformActorAsync() => new(await Quota.ActorAsync(), AuditActorKind.PlatformAdmin);

    public Task<TenantId> TenantAsync() => Quota.Harness.AddTenantAsync();

    /// <summary>The tenant's two system connections (materialized by the first list).</summary>
    public async Task<(StorageConnection Local, StorageConnection Central)> DefaultsAsync(TenantId tenantId)
    {
        var list = await Store().ListAsync(tenantId, default) ?? throw new InvalidOperationException("The tenant does not exist.");
        return (list.Single(record => record.Connection.ProviderType == StorageProviderTypes.Local && record.Connection.IsDefault).Connection,
            list.Single(record => record.Connection.ProviderType == StorageProviderTypes.Central && record.Connection.IsDefault).Connection);
    }

    public static StorageConnection NewConnection(
        TenantId tenantId, string name, string providerType, StorageOwnership ownership = StorageOwnership.TenantManaged, string? quotaScope = null) =>
        new(Guid.NewGuid(), tenantId, name, providerType, ownership, quotaScope, null, isDefault: false, Now);

    /// <summary>Adds a connection through the store (no per-provider limit) and returns it as stored.</summary>
    public async Task<StorageConnection> AddAsync(
        TenantId tenantId, string name, string providerType, StorageOwnership ownership = StorageOwnership.TenantManaged, string? quotaScope = null)
    {
        var result = await Store().CreateAsync(
            NewConnection(tenantId, name, providerType, ownership, quotaScope), null, await PlatformActorAsync(), new Dictionary<string, object?> { ["name"] = name }, Now, default);
        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        return result.Record!.Connection;
    }

    public async Task<StorageConnectionResult> SetEnabledAsync(Guid id, TenantId? tenantScope, bool enabled) =>
        await Store().MutateAsync(id, tenantScope, (connection, now) => connection.SetEnabled(enabled, now)
            ? new StorageMutationResult(true, [new StorageAuditEntry(enabled ? StorageConnectionAuditActions.Enabled : StorageConnectionAuditActions.Disabled, new Dictionary<string, object?>())])
            : StorageMutationResult.NoChange, await PlatformActorAsync(), Now, default);

    public async Task<StorageConnection> RowAsync(Guid id)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.StorageConnections.AsNoTracking().SingleAsync(row => row.Id == id);
    }

    public async Task<List<StorageConnection>> RowsAsync(TenantId tenantId)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.StorageConnections.AsNoTracking().Where(row => row.TenantId == tenantId).OrderBy(row => row.NormalizedName).ToListAsync();
    }

    public async Task<List<BackupReplica>> ReplicasAsync(Guid artifactId)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.BackupReplicas.AsNoTracking().Where(row => row.ArtifactId == artifactId).OrderBy(row => row.StorageProvider).ToListAsync();
    }

    public async Task<List<AuditEvent>> AuditOfTenantAsync(TenantId tenantId, string? actionPrefix = "storage_connection.")
    {
        await using var context = Database.CreatePlatformContext();
        var events = await context.AuditEvents.AsNoTracking().Where(row => row.TenantId == tenantId).OrderBy(row => row.At).ThenBy(row => row.Id).ToListAsync();
        return events.Where(row => actionPrefix is null || row.Action.StartsWith(actionPrefix, StringComparison.Ordinal)).ToList();
    }

    public Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters) => ScalarAsync(Database.ConnectionString, sql, parameters);

    public static async Task<long> ScalarAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public static async Task<string> TextAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    public static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The independent SQL oracle of the replicas still occupying storage, filtered by the connection the replica lives on (joined by the replica's own
    /// connection id, not by provider). Logical counts an artifact once, physical every replica.
    /// </summary>
    public static async Task<(long Logical, long Physical)> OracleAsync(string connectionString, TenantId tenantId, string? providerType = null, string? ownership = null, string? quotaScope = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            WITH r AS (
              SELECT rep."ArtifactId", rep."SizeBytes" AS replica_bytes, art."SizeBytes" AS artifact_bytes
              FROM backup_replicas rep
              JOIN backup_artifacts art ON art."Id" = rep."ArtifactId" AND art."TenantId" = rep."TenantId"
              JOIN storage_connections c ON c."Id" = rep."StorageConnectionId" AND c."TenantId" = rep."TenantId"
              WHERE rep."TenantId" = @t AND rep."Status" <> 'Deleted'
                AND (@p::text IS NULL OR c."ProviderType" = @p) AND (@o::text IS NULL OR c."Ownership" = @o) AND (@q::text IS NULL OR c."QuotaScope" = @q))
            SELECT COALESCE((SELECT SUM(a."artifact_bytes") FROM (SELECT DISTINCT "ArtifactId", artifact_bytes FROM r) a), 0), COALESCE((SELECT SUM(replica_bytes) FROM r), 0)
            """, connection);
        command.Parameters.AddWithValue("t", tenantId.Value);
        command.Parameters.AddWithValue("p", (object?)providerType ?? DBNull.Value);
        command.Parameters.AddWithValue("o", (object?)ownership ?? DBNull.Value);
        command.Parameters.AddWithValue("q", (object?)quotaScope ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
