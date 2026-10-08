using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BackupPlatform.IntegrationTests.ManagedService;

/// <summary>
/// Upgrade from the schema that existed before the commercial layer, on a database this class creates and drops itself.
/// </summary>
public sealed class ManagedServiceMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261007114015_PlatformAudit";
    private readonly string _database = $"backup_platform_migration_{Guid.NewGuid():N}";
    private string? _administrative;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_CONNECTION_STRING");
        Assert.False(string.IsNullOrWhiteSpace(configured), "Set BACKUP_PLATFORM_TEST_CONNECTION_STRING.");
        var builder = new NpgsqlConnectionStringBuilder(configured) { Pooling = false };
        _administrative = builder.ConnectionString;
        await using (var connection = new NpgsqlConnection(_administrative))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", connection);
            await command.ExecuteNonQueryAsync();
        }
        builder.Database = _database;
        _connectionString = builder.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_administrative);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private PlatformDbContext Context() =>
        PlatformDbContext.CreateForPlatformOperations(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(_connectionString).Options);

    private static IMigrator MigratorOf(PlatformDbContext context) => context.GetService<IMigrator>();

    private async Task<List<(Guid Tenant, string Plan, string? SystemKey, int Status, int Kind, long? Bytes, Guid? AssignedBy, Guid Id)>> SubscriptionsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT s."TenantId", p."Name", p."SystemKey", s."Status", s."StorageLimitOverrideKind", s."StorageLimitOverrideBytes", s."AssignedByUserId", s."Id"
            FROM tenant_subscriptions s JOIN plans p ON p."Id" = s."PlanId" ORDER BY s."TenantId"
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(Guid, string, string?, int, int, long?, Guid?, Guid)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.GetGuid(7)));
        }
        return rows;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ExistingTenantsBecomeActiveLegacyUnlimitedSubscribersWithoutLosingAnyData()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var user = Guid.NewGuid();
        await using (var context = Context())
        {
            await MigratorOf(context).MigrateAsync(PreviousMigration);
            Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_name IN ('plans', 'tenant_subscriptions')"));
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO tenants (\"Id\", \"Name\") VALUES ({tenantA}, 'Tenant A'), ({tenantB}, 'Tenant B')");
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users (\"Id\", \"DisplayName\") VALUES ({user}, 'Member')");
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO tenant_memberships (\"TenantId\", \"UserId\", \"Role\") VALUES ({tenantA}, {user}, 2)");
        }

        await using (var context = Context())
        {
            await context.Database.MigrateAsync();
        }

        var rows = await SubscriptionsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { tenantA, tenantB }.Order().ToArray(), rows.Select(row => row.Tenant).ToArray());
        Assert.All(rows, row =>
        {
            Assert.Equal(("Legacy Unlimited", "legacy_unlimited", 1, 0, (long?)null, (Guid?)null), (row.Plan, row.SystemKey, row.Status, row.Kind, row.Bytes, row.AssignedBy));
        });
        // Data is intact and the commercial plans exist as plain rows.
        Assert.Equal(2, await ScalarAsync($"SELECT count(*) FROM tenants WHERE \"Id\" IN ('{tenantA}', '{tenantB}')"));
        Assert.Equal(1, await ScalarAsync($"SELECT count(*) FROM tenant_memberships WHERE \"TenantId\" = '{tenantA}' AND \"UserId\" = '{user}' AND \"Role\" = 2"));
        Assert.Equal(5, await ScalarAsync("SELECT count(*) FROM plans"));
        Assert.Equal(5_000_000_000, await ScalarAsync("SELECT \"StorageLimitBytes\" FROM plans WHERE \"Name\" = '5 GB'"));
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM plans WHERE \"IsSystem\" AND \"SystemKey\" = 'legacy_unlimited' AND \"StorageLimitBytes\" IS NULL"));
        Assert.Equal(2, await ScalarAsync("SELECT count(*) FROM tenant_subscriptions WHERE \"Status\" = 1"));

        // A tenant created after the upgrade starts without a subscription (creating a tenant never depends on one).
        await using (var context = Context())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO tenants (\"Id\", \"Name\") VALUES ({Guid.NewGuid()}, 'Tenant C')");
        }
        Assert.Equal(2, await ScalarAsync("SELECT count(*) FROM tenant_subscriptions"));
    }

    [Fact]
    public async Task TheMigrationIsDeterministicAcrossARollbackAndReapply()
    {
        var tenant = Guid.NewGuid();
        await using (var context = Context())
        {
            await MigratorOf(context).MigrateAsync(PreviousMigration);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO tenants (\"Id\", \"Name\") VALUES ({tenant}, 'Tenant')");
            await context.Database.MigrateAsync();
        }
        var firstPlans = await ScalarAsync("SELECT count(*) FROM plans");
        var first = await SubscriptionsAsync();
        var firstPlanIds = await ScalarAsync("SELECT count(*) FROM plans WHERE \"Id\" IN ('61ff8c7d-5cf1-5b91-8be8-a9e25d5f53ea', 'a6a9a7d1-293a-5ec9-a30f-603428c86559', '16aeeb9a-192d-518f-861c-fd3e417ca8ec', 'bd58c99e-3efa-5c02-920c-b54e1230f3e6', 'f31c79cf-c393-53c2-969d-90bcf0f63ef7')");

        await using (var context = Context())
        {
            await MigratorOf(context).MigrateAsync(PreviousMigration);
            await context.Database.MigrateAsync();
        }
        var second = await SubscriptionsAsync();
        Assert.Equal(first.Single().Id, second.Single().Id);
        Assert.Equal(5, firstPlans);
        Assert.Equal(5, firstPlanIds);
        Assert.Equal(firstPlans, await ScalarAsync("SELECT count(*) FROM plans"));
    }

    [Fact]
    public async Task TheIdempotentReleaseScriptAppliesTwiceAndKeepsASingleSubscriptionPerTenant()
    {
        var tenant = Guid.NewGuid();
        await using (var context = Context())
        {
            await MigratorOf(context).MigrateAsync(PreviousMigration);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO tenants (\"Id\", \"Name\") VALUES ({tenant}, 'Tenant')");
        }
        string script;
        await using (var context = Context())
        {
            script = MigratorOf(context).GenerateScript(null, null, MigrationsSqlGenerationOptions.Idempotent);
        }
        for (var pass = 0; pass < 2; pass++)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(script, connection);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, await ScalarAsync($"SELECT count(*) FROM tenant_subscriptions WHERE \"TenantId\" = '{tenant}'"));
        Assert.Equal(5, await ScalarAsync("SELECT count(*) FROM plans"));
    }
}
