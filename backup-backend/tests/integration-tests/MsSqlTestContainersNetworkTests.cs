using Npgsql;

namespace BackupPlatform.IntegrationTests.Infrastructure;

public sealed class MsSqlTestContainersNetworkTests
{
    [LocalTestContainersFact]
    public async Task AllSharedServersCanStartConcurrently()
    {
        var ports = await Task.WhenAll(
            Task.Run(() => new NpgsqlConnectionStringBuilder(TestContainers.PostgresAdministrativeConnectionString).Port),
            Task.Run(() => TestContainers.MsSqlServer.Port),
            Task.Run(() => TestContainers.MsSqlRestoreTarget.Port),
            Task.Run(() => TestContainers.SftpServer.Port));

        Assert.All(ports, port => Assert.InRange(port, 1, 65535));
        Assert.Equal(ports.Length, ports.Distinct().Count());
    }
}

public sealed class LocalTestContainersFactAttribute : FactAttribute
{
    public LocalTestContainersFactAttribute()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_HOST")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_HOST")))
        {
            Skip = "Requires local Testcontainers rather than external server overrides.";
        }
    }
}
