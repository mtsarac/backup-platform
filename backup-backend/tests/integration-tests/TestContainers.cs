using Npgsql;
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

    private static readonly Lazy<PostgreSqlContainer> Postgres = new(StartPostgres);
    private static readonly Lazy<MsSqlServer> MsSql = new(StartMsSql);

    /// <summary>A superuser connection (database <c>postgres</c>) to the shared PostgreSQL container; tests create their own databases on it.</summary>
    public static string PostgresAdministrativeConnectionString =>
        new NpgsqlConnectionStringBuilder(Postgres.Value.GetConnectionString()) { SslMode = SslMode.Disable }.ConnectionString;

    public static MsSqlServer MsSqlServer => MsSql.Value;

    // Blocking is deliberate: Lazy needs a synchronous factory and a failed start must fail every test that asks, loudly.
    private static T Start<T>(T container) where T : DotNet.Testcontainers.Containers.IContainer
    {
        container.StartAsync().GetAwaiter().GetResult();
        return container;
    }

    private static PostgreSqlContainer StartPostgres()
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var container = new PostgreSqlBuilder(PostgresImage)
                // Docker's bridge is not container localhost; physical replication needs its own HBA rule.
                // Tune connections and disable synchronous fsync for parallel test performance.
                .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(
                    "#!/bin/sh\n" +
                    "printf '\\nhost replication all samenet scram-sha-256\\n' >> \"$PGDATA/pg_hba.conf\"\n" +
                    "printf '\\nmax_connections = 500\\nfsync = off\\nsynchronous_commit = off\\nfull_page_writes = off\\n' >> \"$PGDATA/postgresql.conf\"\n"),
                    DotNet.Testcontainers.Builders.FilePath.Of("/docker-entrypoint-initdb.d/init-config.sh"))
                .Build();

            try
            {
                return Start(container);
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                try { container.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                Thread.Sleep(500);
            }
        }
        throw new InvalidOperationException("Failed to start PostgreSQL testcontainer after retries.");
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

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var container = builder.Build();
            try
            {
                Start(container);
                return new MsSqlServer(container.Hostname, container.GetMappedPublicPort(1433), MsSqlBuilder.DefaultUsername, MsSqlBuilder.DefaultPassword, serverStaging, staging.FullName);
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                try { container.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                Thread.Sleep(500);
            }
        }
        throw new InvalidOperationException("Failed to start MSSQL testcontainer after retries.");
    }
}

/// <summary>Where the shared SQL Server container is and the one directory the server and this process both see.</summary>
internal sealed record MsSqlServer(string Host, int Port, string User, string Password, string StagingServerDirectory, string StagingAgentDirectory);
