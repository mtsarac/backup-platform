using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BackupPlatform.IntegrationTests.Storage;

public sealed class GoogleDriveReliabilityMigrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public void RestoredRelayedTargetDiffContainsOnlyTheOriginalM7cTablesAndIndexes()
    {
        using var db = fixture.CreatePlatformContext();
        var assembly = db.GetService<IMigrationsAssembly>();
        var initializer = db.GetService<IModelRuntimeInitializer>();
        var before = initializer.Initialize(assembly.CreateMigration(
            assembly.Migrations["20261009140000_BackendRelayedRestoreSources"], db.Database.ProviderName!).TargetModel, designTime: true);
        var after = initializer.Initialize(assembly.CreateMigration(
            assembly.Migrations["20261009160000_GoogleDriveStorageProvider"], db.Database.ProviderName!).TargetModel, designTime: true);
        var delta = db.GetService<IMigrationsModelDiffer>().GetDifferences(before.GetRelationalModel(), after.GetRelationalModel());
        Assert.Equal(new[] { "google_drive_file_metadata", "google_drive_oauth_states" },
            delta.OfType<CreateTableOperation>().Select(operation => operation.Name).Order().ToArray());
        Assert.All(delta, operation => Assert.True(operation is CreateTableOperation or CreateIndexOperation));
    }

    [Fact]
    public async Task UpgradeFromM7cPreservesExistingTenantAndAllowsReservationPersistence()
    {
        var name = $"bp_m7c1_upgrade_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name };
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connection.ConnectionString).Options;
            var tenant = new Tenant(TenantId.New(), "Upgrade survivor");
            await using (var previous = PlatformDbContext.CreateForPlatformOperations(options))
            {
                await previous.GetService<IMigrator>().MigrateAsync("20261009160000_GoogleDriveStorageProvider");
                previous.Tenants.Add(tenant);
                await previous.SaveChangesAsync();
            }
            await using var upgraded = PlatformDbContext.CreateForPlatformOperations(options);
            await upgraded.Database.MigrateAsync();
            Assert.Equal("Upgrade survivor", (await upgraded.Tenants.SingleAsync(row => row.Id == tenant.Id)).Name);
            await using var sql = new NpgsqlConnection(connection.ConnectionString);
            await sql.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT to_regclass('google_drive_staging_reservations') IS NOT NULL", sql);
            Assert.Equal(true, await query.ExecuteScalarAsync());
            Assert.False(upgraded.Database.HasPendingModelChanges());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
