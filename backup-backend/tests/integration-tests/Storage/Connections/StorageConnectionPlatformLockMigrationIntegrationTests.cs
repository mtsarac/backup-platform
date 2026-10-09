using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

public sealed class StorageConnectionPlatformLockMigrationIntegrationTests
{
    private const string PreviousMigration = "20261009082434_StorageConnections";

    [Fact]
    public async Task UpgradingTheM7aSchemaPreservesEffectiveAvailabilityAndTenantPreferenceForEveryLegacyState()
    {
        var isolated = new PostgreSqlFixture();
        await isolated.InitializeAsync();
        try
        {
            var world = new ConnectionWorld(isolated);
            var tenant = await world.TenantAsync();
            var tenantEnabled = await world.AddAsync(tenant, "Tenant enabled", "local");
            var tenantDisabled = await world.AddAsync(tenant, "Tenant disabled", "local");
            var platformEnabled = await world.AddAsync(tenant, "Platform enabled", "central", StorageOwnership.PlatformManaged);
            var platformDisabled = await world.AddAsync(tenant, "Platform disabled", "central", StorageOwnership.PlatformManaged);
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(isolated.ConnectionString).Options;
            await using (var context = PlatformDbContext.CreateForPlatformOperations(options))
            {
                await context.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            }
            Assert.Equal(0, await world.ScalarAsync("""
                SELECT count(*) FROM information_schema.columns
                WHERE table_name = 'storage_connections' AND column_name = 'AdministrativelyDisabled'
                """));
            await ConnectionWorld.ExecuteAsync(isolated.ConnectionString, """
                UPDATE storage_connections SET "Enabled" = false
                WHERE "Id" IN (@tenantDisabled, @platformDisabled)
                """, ("tenantDisabled", tenantDisabled.Id), ("platformDisabled", platformDisabled.Id));
            Assert.Equal(2, await world.ScalarAsync("""
                SELECT count(*) FROM storage_connections WHERE "TenantId" = @tenant AND "Enabled"
                """, ("tenant", tenant.Value)));
            Assert.Equal(2, await world.ScalarAsync("""
                SELECT count(*) FROM storage_connections WHERE "TenantId" = @tenant AND NOT "Enabled"
                """, ("tenant", tenant.Value)));

            await using (var context = PlatformDbContext.CreateForPlatformOperations(options))
            {
                await context.GetService<IMigrator>().MigrateAsync();
            }
            var rows = await world.RowsAsync(tenant);
            Assert.Equal(4, rows.Count);
            AssertState(rows.Single(row => row.Id == tenantEnabled.Id), (StorageOwnership.TenantManaged, true, false, true));
            AssertState(rows.Single(row => row.Id == tenantDisabled.Id), (StorageOwnership.TenantManaged, false, false, false));
            AssertState(rows.Single(row => row.Id == platformEnabled.Id), (StorageOwnership.PlatformManaged, true, false, true));
            AssertState(rows.Single(row => row.Id == platformDisabled.Id), (StorageOwnership.PlatformManaged, true, true, false));
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    private static void AssertState(StorageConnection connection, (StorageOwnership Ownership, bool Enabled, bool Locked, bool Effective) expected) =>
        Assert.Equal(expected, (connection.Ownership, connection.Enabled, connection.AdministrativelyDisabled, connection.EffectiveEnabled));
}
