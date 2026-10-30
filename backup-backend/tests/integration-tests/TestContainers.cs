using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// The databases integration tests run against: one PostgreSQL and one SQL Server container per test process, started on first use
/// and removed by the Testcontainers reaper when the process ends. Docker is the only prerequisite; no connection string or
/// environment variable is read.
/// </summary>
internal static class TestContainers
{
    // Same major version as docker-compose.yml, so the tests run against the server the product ships with.
    private const string PostgresImage = "postgres:18-alpine";
    private const string MsSqlImage = "mcr.microsoft.com/mssql/server:2025-latest";

    private static readonly Lazy<PostgreSqlContainer> Postgres = new(() => Start(new PostgreSqlBuilder(PostgresImage).Build()));
    private static readonly Lazy<MsSqlServer> MsSql = new(StartMsSql);

    /// <summary>A superuser connection (database <c>postgres</c>) to the shared PostgreSQL container; tests create their own databases on it.</summary>
    public static string PostgresAdministrativeConnectionString => Postgres.Value.GetConnectionString();

    public static MsSqlServer MsSqlServer => MsSql.Value;

    // Blocking is deliberate: Lazy needs a synchronous factory and a failed start must fail every test that asks, loudly.
    private static T Start<T>(T container) where T : DotNet.Testcontainers.Containers.IContainer
    {
        container.StartAsync().GetAwaiter().GetResult();
        return container;
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern uint getgid();

    private static MsSqlServer StartMsSql()
    {
        // The server (uid 10001) writes backups into a host directory this process also reads and deletes, and reads files this process
        // writes there. Joining the server to this user's group, with a world-writable setgid directory, makes both directions work.
        var staging = Directory.CreateTempSubdirectory("bp_it_mssql_staging_");
        const string serverStaging = "/var/opt/mssql/staging";
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(staging.FullName, UnixFileMode.SetGroup | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }

        var builder = new MsSqlBuilder(MsSqlImage).WithBindMount(staging.FullName, serverStaging);
        if (OperatingSystem.IsLinux())
        {
            builder = builder.WithCreateParameterModifier(parameters => (parameters.HostConfig ??= new()).GroupAdd = [getgid().ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        var container = builder.Build();
        Start(container);
        return new MsSqlServer(container.Hostname, container.GetMappedPublicPort(1433), MsSqlBuilder.DefaultUsername, MsSqlBuilder.DefaultPassword, serverStaging, staging.FullName);
    }
}

/// <summary>Where the shared SQL Server container is and the one directory the server and this process both see.</summary>
internal sealed record MsSqlServer(string Host, int Port, string User, string Password, string StagingServerDirectory, string StagingAgentDirectory);
