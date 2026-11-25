using BackupPlatform.Infrastructure.Persistence;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupPlatform.IntegrationTests;

public sealed class QuartzFixedDelayMigrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public void RemovingLegacyOptionsPreservesQuotedCredentialsAndUnrecognizedSettings()
    {
        var normalized = PlatformConnectionString.WithoutRemovedMultiplexingOptions("Host=localhost;Password=\"secret;value\";multiplexing=false;InvalidOption=1");
        var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = normalized };
        Assert.Equal("secret;value", builder["Password"]);
        Assert.Equal("1", builder["InvalidOption"]);
        Assert.False(builder.ContainsKey("Multiplexing"));
        Assert.Throws<ArgumentException>(() => new NpgsqlConnectionStringBuilder(normalized));
    }

    [Fact]
    public async Task LegacyMultiplexingOptionsAllowThePlatformDatabaseToConnect()
    {
        var legacy = fixture.ConnectionString + ";MULTIPLEXING=true;Write Coalescing Buffer Threshold Bytes=8192;WriteCoalescingBufferThresholdBytes=8192";
        var connectionString = PlatformConnectionString.WithoutRemovedMultiplexingOptions(legacy);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        Assert.Equal(1, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UpgradePreservesExistingQuartzJobsAndTriggers()
    {
        await using var context = fixture.CreateMigrationContext();
        var migrator = context.GetService<IMigrator>();
        await context.Database.ExecuteSqlRawAsync("""
            DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261125000000_QuartzFixedDelay';
            ALTER TABLE quartz.qrtz_triggers DROP COLUMN fixed_delay;
            INSERT INTO quartz.qrtz_job_details (sched_name, job_name, job_group, job_class_name, is_durable, is_nonconcurrent, is_update_data, requests_recovery)
            VALUES ('test', 'job', 'group', 'test', true, false, false, false);
            INSERT INTO quartz.qrtz_triggers (sched_name, trigger_name, trigger_group, job_name, job_group, trigger_state, trigger_type, start_time)
            VALUES ('test', 'trigger', 'group', 'job', 'group', 'WAITING', 'SIMPLE', 123);
            """);
        await migrator.MigrateAsync();
        Assert.Equal(1L, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM quartz.qrtz_triggers WHERE sched_name = 'test' AND trigger_name = 'trigger' AND start_time = 123 AND fixed_delay IS NULL").SingleAsync());
        Assert.Equal(1L, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM quartz.qrtz_job_details WHERE sched_name = 'test' AND job_name = 'job'").SingleAsync());
        await context.Database.ExecuteSqlRawAsync("UPDATE quartz.qrtz_triggers SET fixed_delay = true WHERE sched_name = 'test'");
        await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261124010000_TenantNameUnique"));
        Assert.Equal(1L, await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM quartz.qrtz_triggers WHERE sched_name = 'test' AND fixed_delay = true").SingleAsync());
    }
}
