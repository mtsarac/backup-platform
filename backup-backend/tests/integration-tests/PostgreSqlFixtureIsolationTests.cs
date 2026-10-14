using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BackupPlatform.IntegrationTests;

public sealed class PostgreSqlFixtureIsolationTests
{
    [Fact]
    public async Task ParallelFixturesShareOneMigratedClosedTemplateButNeverTestData()
    {
        var template = PostgreSqlFixture.SharedTemplate;
        var fixtures = Enumerable.Range(0, 4).Select(_ => new PostgreSqlFixture(template)).ToArray();
        try
        {
            await Task.WhenAll(fixtures.Select(fixture => fixture.InitializeAsync()));
            var templateName = await template.GetDatabaseNameAsync();
            Assert.All(fixtures, fixture => Assert.False(new NpgsqlConnectionStringBuilder(fixture.ConnectionString).Pooling));
            Assert.Equal(fixtures.Length, fixtures.Select(fixture => new NpgsqlConnectionStringBuilder(fixture.ConnectionString).Database).Distinct().Count());
            var tenantIds = fixtures.Select(_ => TenantId.New()).ToArray();
            await Task.WhenAll(fixtures.Select(async (fixture, index) =>
            {
                await using var context = fixture.CreatePlatformContext();
                Assert.Empty(await context.Tenants.ToListAsync());
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
                context.Tenants.Add(new Tenant(tenantIds[index], "Isolated fixture"));
                await context.SaveChangesAsync();
            }));
            for (var index = 0; index < fixtures.Length; index++)
            {
                await using var context = fixtures[index].CreatePlatformContext();
                Assert.Equal(tenantIds[index], Assert.Single(await context.Tenants.AsNoTracking().ToListAsync()).Id);
            }
            await using var administrative = new NpgsqlConnection(template.AdministrativeConnectionString);
            await administrative.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT datallowconn, (SELECT count(*) FROM pg_stat_activity WHERE datname = @name) FROM pg_database WHERE datname = @name", administrative);
            command.Parameters.AddWithValue("name", templateName);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.Equal(0, reader.GetInt64(1));
        }
        finally { await Task.WhenAll(fixtures.Select(fixture => fixture.DisposeAsync())); }
    }

    [Fact]
    public async Task DowngradingAndUpgradingOneCloneCannotPoisonTheNextFixturesSchemaOrRows()
    {
        var template = PostgreSqlFixture.SharedTemplate;
        var first = new PostgreSqlFixture(template);
        var second = new PostgreSqlFixture(template);
        try
        {
            await first.InitializeAsync();
            await using var context = first.CreatePlatformContext();
            var migrations = context.Database.GetMigrations().ToArray();
            Assert.True(migrations.Length >= 2);
            var tenantId = TenantId.New();
            context.Tenants.Add(new Tenant(tenantId, "Migration clone"));
            await context.SaveChangesAsync();
            await context.GetService<IMigrator>().MigrateAsync(migrations[^2]);
            Assert.Equal(migrations[..^1], (await context.Database.GetAppliedMigrationsAsync()).ToArray());
            await second.InitializeAsync();
            await using var untouched = second.CreatePlatformContext();
            Assert.Equal(migrations, (await untouched.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Empty(await untouched.Tenants.ToListAsync());
            await context.Database.MigrateAsync();
            Assert.Equal(migrations, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(tenantId, Assert.Single(await context.Tenants.AsNoTracking().ToListAsync()).Id);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExplicitTemplateDisposalRemovesOnlyTheSourceAndPreservesIndependentClones()
    {
        using var template = new PostgreSqlTemplateDatabase(PostgreSqlFixture.GetAdministrativeConnectionString());
        var clone = new PostgreSqlFixture(template);
        try
        {
            await clone.InitializeAsync();
            var name = await template.GetDatabaseNameAsync();
            template.Dispose();
            template.Dispose();
            await using var administrative = new NpgsqlConnection(template.AdministrativeConnectionString);
            await administrative.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", administrative);
            command.Parameters.AddWithValue("name", name);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
            await using var context = clone.CreatePlatformContext();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            var tenant = new Tenant(TenantId.New(), "Surviving clone");
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
            Assert.Equal(tenant.Id, Assert.Single(await context.Tenants.AsNoTracking().ToListAsync()).Id);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => template.GetDatabaseNameAsync());
        }
        finally { await clone.DisposeAsync(); }
    }

    [Fact]
    public async Task ExistingParameterlessManualFixturesRemainIsolatedAndDisposeTheirOwnDatabases()
    {
        var first = new PostgreSqlFixture();
        var second = new PostgreSqlFixture();
        string? firstName = null;
        string? secondName = null;
        try
        {
            await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
            firstName = new NpgsqlConnectionStringBuilder(first.ConnectionString).Database;
            secondName = new NpgsqlConnectionStringBuilder(second.ConnectionString).Database;
            Assert.NotEqual(firstName, secondName);
            await using (var context = first.CreatePlatformContext())
            {
                context.Tenants.Add(new Tenant(TenantId.New(), "Manual fixture"));
                await context.SaveChangesAsync();
            }
            await using (var context = second.CreatePlatformContext())
                Assert.Empty(await context.Tenants.ToListAsync());
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
        await first.DisposeAsync();
        await second.DisposeAsync();
        await using var administrative = new NpgsqlConnection(PostgreSqlFixture.GetAdministrativeConnectionString());
        await administrative.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @first OR datname = @second", administrative);
        command.Parameters.AddWithValue("first", firstName!);
        command.Parameters.AddWithValue("second", secondName!);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
