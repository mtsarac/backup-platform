using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.IntegrationTests.PostgreSql;

[Trait("Category", "PostgreSqlProvider")]
public sealed class PostgreSqlUnsupportedBackupIntegrationTests(PostgreSqlProviderFixture fixture) : IClassFixture<PostgreSqlProviderFixture>
{
    [Fact]
    public async Task FullBackupRefusesForeignTablesRatherThanPublishingAnArchiveWithoutTheirRows()
    {
        var source = await fixture.DatabaseAsync();
        await fixture.ExecuteAsync(source,
            """
            CREATE EXTENSION postgres_fdw;
            CREATE SERVER never_contacted FOREIGN DATA WRAPPER postgres_fdw OPTIONS (host '127.0.0.1', port '1', dbname 'unused');
            CREATE FOREIGN TABLE public.foreign_data(id integer, payload text) SERVER never_contacted OPTIONS (schema_name 'unused', table_name 'unused');
            """);
        await AssertBackupRefusedAsync(source);
        Assert.Equal(1L, await fixture.ScalarAsync<long>(source, "SELECT count(*) FROM pg_foreign_table"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(source, "SELECT count(*) FROM pg_user_mappings"));
    }

    [Fact]
    public async Task FullBackupRefusesSubscriptionsThatTheRestoreProviderCannotSafelyRestore()
    {
        var source = await fixture.DatabaseAsync();
        const string subscription = "isolated_unsupported_backup_subscription";
        await fixture.ExecuteAsync(source,
            $"CREATE SUBSCRIPTION {subscription} CONNECTION 'host=127.0.0.1 port=1 dbname=unused user=unused' PUBLICATION unused WITH (connect=false, enabled=false, create_slot=false, slot_name=NONE)");
        try
        {
            await AssertBackupRefusedAsync(source);
            Assert.False(await fixture.ScalarAsync<bool>(source, $"SELECT subenabled FROM pg_subscription WHERE subname='{subscription}'"));
            Assert.Null(await fixture.ScalarAsync<string>(source, $"SELECT subslotname FROM pg_subscription WHERE subname='{subscription}'"));
        }
        finally
        {
            await fixture.ExecuteAsync(source, $"DROP SUBSCRIPTION {subscription}");
        }
    }

    private async Task AssertBackupRefusedAsync(string source)
    {
        var precheck = await fixture.Backup.PreCheckFullBackupAsync(fixture.Settings, fixture.Credential,
            new FullBackupPreCheckRequest(source, staging: StagingArea.SameHost(fixture.DirectoryPath)), CancellationToken.None);
        Assert.False(precheck.CanProceed);
        Assert.Contains(precheck.Items, item => item.Status == BackupPreCheckStatus.Failed && item.Cause == BackupFailureCause.Unsupported);
        var name = $"unsupported_{Guid.NewGuid():N}.dump";
        var result = await fixture.Backup.ExecuteFullBackupAsync(fixture.Settings, fixture.Credential,
            new FullBackupRequest(source, StagingArea.SameHost(fixture.DirectoryPath), name), null, CancellationToken.None);
        Assert.Equal(FullBackupStatus.Failed, result.Status);
        Assert.NotNull(result.Failure);
        Assert.Equal(BackupFailureCause.Unsupported, result.Failure.Cause);
        Assert.Null(result.StagedFile);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, name)));
    }
}
