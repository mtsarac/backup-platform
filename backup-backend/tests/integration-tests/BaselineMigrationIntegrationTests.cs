using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class BaselineMigrationIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task AFreshDatabaseFromTheSingleBaselineMatchesTheCurrentModel()
    {
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(["Baseline"], (await context.Database.GetAppliedMigrationsAsync()).Select(m => m[(m.IndexOf('_') + 1)..]));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task TheBaselineCarriesTheObjectsTheModelCannotExpress()
    {
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(16L, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM pg_trigger WHERE NOT tgisinternal AND tgname LIKE 'trg\\_%'").SingleAsync());
        Assert.Equal(1L, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM quota_policies WHERE \"IsDefault\"").SingleAsync());
        Assert.Equal(5L, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM plans").SingleAsync());
        Assert.Equal(1L, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM information_schema.schemata WHERE schema_name = 'quartz'").SingleAsync());
    }
}
