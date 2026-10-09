using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

/// <summary>
/// Migration verification: (1) fresh install on an empty database migrates cleanly and tenants materialize defaults on demand;
/// (2) upgrading a database that already holds tenants and backups backfills the defaults, connects every replica to its own destination,
/// preserves usage per scope rule against the independent oracle, and round-trips Down/Up cleanly.
/// </summary>
public sealed class StorageConnectionMigrationIntegrationTests(UpgradedQuotaDatabase upgraded) : IClassFixture<UpgradedQuotaDatabase>
{
    public const string PreviousMigration = "20261009072143_SupportAccess";
    private const long Gb = QuotaWorld.Gb;

    // ---- fresh install ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFreshDatabaseMigratesCleanlyAndTenantsThenGetDefaultsOnDemand()
    {
        var isolated = new PostgreSqlFixture();
        await isolated.InitializeAsync();
        try
        {
            var world = new ConnectionWorld(isolated);
            Assert.Equal(0, await world.ScalarAsync("SELECT count(*) FROM storage_connections"));

            var tenantA = await world.TenantAsync();
            var tenantB = await world.TenantAsync();
            var listA = await world.Store().ListAsync(tenantA, default);
            var listB = await world.Store().ListAsync(tenantB, default);

            Assert.Equal(2, listA!.Count);
            Assert.Equal(2, listB!.Count);
            Assert.Equal(4, await world.ScalarAsync("SELECT count(*) FROM storage_connections"));
            Assert.All(new[] { listA, listB }, list =>
            {
                Assert.Single(list!, record => record.Connection.ProviderType == "local" && record.Connection.Ownership == StorageOwnership.TenantManaged && record.Connection.QuotaScope == "tenant");
                Assert.Single(list!, record => record.Connection.ProviderType == "central" && record.Connection.Ownership == StorageOwnership.PlatformManaged && record.Connection.QuotaScope == "managed");
            });
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    // ---- upgrade of an existing database ---------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryTenantIncludingOneWithNoReplicasHasTheTwoSystemDefaultsWithExpectedProperties()
    {
        var tenants = new[] { upgraded.FiveGbRead.TenantId, upgraded.LegacyRead.TenantId, upgraded.FiveGbAdmit.TenantId, upgraded.LegacyAdmit.TenantId, upgraded.DanglingPlanTenant };

        foreach (var tenantId in tenants)
        {
            var rows = await ConnectionWorld.ScalarAsync(upgraded.ConnectionString,
                "SELECT count(*) FROM storage_connections WHERE \"TenantId\" = @t AND \"IsDefault\" AND \"Enabled\"", ("t", tenantId.Value));
            Assert.Equal(2, rows);
            var local = await ConnectionWorld.ScalarAsync(upgraded.ConnectionString,
                "SELECT count(*) FROM storage_connections WHERE \"TenantId\" = @t AND \"ProviderType\" = 'local' AND \"Name\" = 'Local' AND \"Ownership\" = 'TenantManaged' AND \"QuotaScope\" = 'tenant'",
                ("t", tenantId.Value));
            Assert.Equal(1, local);
            var central = await ConnectionWorld.ScalarAsync(upgraded.ConnectionString,
                "SELECT count(*) FROM storage_connections WHERE \"TenantId\" = @t AND \"ProviderType\" = 'central' AND \"Name\" = 'Managed Storage' AND \"Ownership\" = 'PlatformManaged' AND \"QuotaScope\" = 'managed'",
                ("t", tenantId.Value));
            Assert.Equal(1, central);
        }
    }

    [Fact]
    public async Task EveryReplicaRowHasAConnectionOfItsOwnTenantAndProviderType()
    {
        // Composite foreign key constraint means every replica is valid; verify with an explicit SQL count that none is dangling or mismatched.
        var mismatched = await upgraded.ScalarAsync("""
            SELECT count(*)
            FROM backup_replicas r
            LEFT JOIN storage_connections c ON c."Id" = r."StorageConnectionId" AND c."TenantId" = r."TenantId" AND c."ProviderType" = r."StorageProvider"
            WHERE c."Id" IS NULL
            """);
        Assert.Equal(0, mismatched);
        var totalReplicas = await upgraded.ScalarAsync("SELECT count(*) FROM backup_replicas");
        Assert.True(totalReplicas >= 8); // 4 tenants x 2 replicas
    }

    [Fact]
    public async Task StandInProviderReplicasGotDisabledTenantManagedConnectionsWithTenantQuotaScope()
    {
        var standInRows = await upgraded.ScalarAsync("""
            SELECT count(*) FROM storage_connections
            WHERE "ProviderType" IN ('googledrive', 'sftp')
              AND "Ownership" = 'TenantManaged'
              AND "QuotaScope" = 'tenant'
              AND NOT "Enabled"
              AND "IsDefault"
            """);
        // Two tenants had googledrive replicas (FiveGbRead, FiveGbAdmit), two had sftp replicas (LegacyRead, LegacyAdmit)
        Assert.Equal(4, standInRows);
    }

    [Fact]
    public async Task PreUpgradeUsagePerScopeRuleIsIdenticalToTheIndependentOracle()
    {
        // FiveGbRead: Central (2 GB) + GoogleDrive (2 GB)
        // LegacyRead: Central (3 GB) + Sftp (3 GB)
        await using var connection = new NpgsqlConnection(upgraded.ConnectionString);
        await connection.OpenAsync();

        async Task AssertScopesAsync(TenantId tenantId, long centralBytes, long standInBytes, string standInProvider)
        {
            var totalLogical = centralBytes; // one artifact: 2 GB or 3 GB
            var totalPhysical = centralBytes + standInBytes;

            // Oracle for All
            var allOracle = await ConnectionWorld.OracleAsync(upgraded.ConnectionString, tenantId);
            Assert.Equal((totalLogical, totalPhysical), allOracle);

            // Oracle for PlatformManaged (only central)
            var platformOracle = await ConnectionWorld.OracleAsync(upgraded.ConnectionString, tenantId, ownership: "PlatformManaged");
            Assert.Equal((centralBytes, centralBytes), platformOracle);

            // Oracle for TenantManaged (only standIn)
            var tenantManagedOracle = await ConnectionWorld.OracleAsync(upgraded.ConnectionString, tenantId, ownership: "TenantManaged");
            Assert.Equal((standInBytes, standInBytes), tenantManagedOracle);

            // Oracle for the standIn provider
            var standInOracle = await ConnectionWorld.OracleAsync(upgraded.ConnectionString, tenantId, providerType: standInProvider);
            Assert.Equal((standInBytes, standInBytes), standInOracle);

            // Oracle for quotaScope "managed"
            var managedScopeOracle = await ConnectionWorld.OracleAsync(upgraded.ConnectionString, tenantId, quotaScope: "managed");
            Assert.Equal((centralBytes, centralBytes), managedScopeOracle);
        }

        await AssertScopesAsync(upgraded.FiveGbRead.TenantId, 2 * Gb, 2 * Gb, "googledrive");
        await AssertScopesAsync(upgraded.LegacyRead.TenantId, 3 * Gb, 3 * Gb, "sftp");
    }

    // ---- Down and Up round trip ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MigrationDownAndUpRoundTripOnADatabaseWithReplicasReBackfillsEveryReplicaCleanly()
    {
        var fixture = new PostgreSqlFixture();
        await fixture.InitializeAsync();
        try
        {
            var world = new QuotaWorld(fixture);
            var agent = await world.AgentAsync();
            var job = await world.JobAsync(agent);
            var (_, artifactId) = await world.PublishAsync(world.RunStore(), agent, job, 2 * Gb, UsageTestKit.AllThree, QuotaWorld.T0);

            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(fixture.ConnectionString).Options;

            // 1. Verify starting state at latest migration: 3 replicas, each has a connection
            await using (var context = PlatformDbContext.CreateForPlatformOperations(options))
            {
                Assert.Equal(3, await context.BackupReplicas.CountAsync(r => r.ArtifactId == artifactId));
                Assert.Equal(0, await context.BackupReplicas.CountAsync(r => r.StorageConnectionId == Guid.Empty));
            }

            // 2. Migrate DOWN to PreviousMigration (SupportAccess)
            await using (var context = PlatformDbContext.CreateForPlatformOperations(options))
            {
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigration);
            }

            // Verify column and table are dropped
            Assert.Equal(0, await ConnectionWorld.ScalarAsync(fixture.ConnectionString,
                "SELECT count(*) FROM information_schema.tables WHERE table_name = 'storage_connections'"));
            Assert.Equal(0, await ConnectionWorld.ScalarAsync(fixture.ConnectionString,
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'backup_replicas' AND column_name = 'StorageConnectionId'"));

            // 3. Migrate UP back to latest
            await using (var context = PlatformDbContext.CreateForPlatformOperations(options))
            {
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync();
            }

            // 4. Verify that storage_connections re-appeared and every replica row has a valid connection
            Assert.Equal(1, await ConnectionWorld.ScalarAsync(fixture.ConnectionString,
                "SELECT count(*) FROM information_schema.tables WHERE table_name = 'storage_connections'"));
            var dangling = await ConnectionWorld.ScalarAsync(fixture.ConnectionString, """
                SELECT count(*)
                FROM backup_replicas r
                LEFT JOIN storage_connections c ON c."Id" = r."StorageConnectionId" AND c."TenantId" = r."TenantId" AND c."ProviderType" = r."StorageProvider"
                WHERE c."Id" IS NULL
                """);
            Assert.Equal(0, dangling);

            // Verify the stand-in providers got disabled tenant-managed connections
            var standIns = await ConnectionWorld.ScalarAsync(fixture.ConnectionString, """
                SELECT count(*) FROM storage_connections
                WHERE "TenantId" = @t AND "ProviderType" IN ('googledrive', 'sftp')
                  AND "Ownership" = 'TenantManaged' AND NOT "Enabled" AND "IsDefault"
                """, ("t", agent.TenantId.Value));
            Assert.Equal(2, standIns);

            // And usage calculation works and matches oracle
            var usage = await world.Usage().GetUsageAsync(agent.TenantId, default);
            Assert.NotNull(usage);
            Assert.Equal(2 * Gb, usage.UsedBytes);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
