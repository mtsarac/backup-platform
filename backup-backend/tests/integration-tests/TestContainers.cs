using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Renci.SshNet;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// The servers integration tests run against: one PostgreSQL, one SQL Server (plus a second one for cross-instance restores) and one
/// SFTP server container per test process, each started on first use and removed by the Testcontainers reaper when the process ends.
/// Docker is the only prerequisite. The SQL Server and SFTP tests accept a real server through BACKUP_PLATFORM_TEST_* variables instead
/// (<see cref="MsSqlServer.FromEnvironment"/>, <see cref="SftpServer.FromEnvironment"/>); nothing needs to be set.
/// </summary>
internal static class TestContainers
{
    // Same major version as docker-compose.yml, so the tests run against the server the product ships with.
    private const string PostgresImage = "postgres:18-alpine";
    private const string MsSqlImage = "mcr.microsoft.com/mssql/server:2025-latest";
    private const string SftpImage = "atmoz/sftp:alpine";
    private const string SftpUser = "testuser";
    private const string SftpPassword = "testpass";

    private static readonly Lazy<PostgreSqlContainer> Postgres = new(StartPostgres);
    private static readonly Lazy<MsSqlServer> MsSql = new(() => StartMsSql(CreateMsSqlStagingDirectory()));
    private static readonly Lazy<MsSqlServer> MsSqlSecond = new(() => StartMsSql(MsSql.Value.StagingAgentDirectory));
    private static readonly Lazy<MsSqlServer> AgentHostSql = new(CreateAgentHostStaging);
    private static readonly Lazy<SftpServer> Sftp = new(StartSftp);

    /// <summary>A superuser connection (database <c>postgres</c>) to the shared PostgreSQL container; tests create their own databases on it.</summary>
    public static string PostgresAdministrativeConnectionString =>
        new NpgsqlConnectionStringBuilder(Postgres.Value.GetConnectionString()) { SslMode = SslMode.Disable }.ConnectionString;

    public static MsSqlServer MsSqlServer => MsSql.Value;

    /// <summary>A second, independent SQL Server instance that mounts the same staging directory as <see cref="MsSqlServer"/>: the target of a cross-instance restore.</summary>
    public static MsSqlServer MsSqlRestoreTarget => MsSqlSecond.Value;

    /// <summary><see cref="MsSqlServer"/> with a staging subdirectory of its own for the real Agent.Host runs, so their backup and probe files never show up in the staging assertions of the in-process tests that use the directory's root at the same time.</summary>
    public static MsSqlServer AgentHostMsSqlServer => AgentHostSql.Value;

    public static SftpServer SftpServer => Sftp.Value;

    // Blocking is deliberate: Lazy needs a synchronous factory and a failed start must fail every test that asks, loudly.
    private static T Start<T>(T container) where T : IContainer
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

    private static string CreateMsSqlStagingDirectory()
    {
        // The server (uid 10001) writes backups into a host directory this process also reads and deletes, and reads files this process
        // writes there. Joining the server to this user's group, with a world-writable setgid directory, makes both directions work.
        var staging = Directory.CreateTempSubdirectory("bp_it_mssql_staging_");
        MakeStagingDirectoryShared(staging.FullName);
        return staging.FullName;
    }

    private static void MakeStagingDirectoryShared(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.SetGroup | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }
    }

    private static MsSqlServer CreateAgentHostStaging()
    {
        var server = MsSql.Value;
        var agentDirectory = Directory.CreateDirectory(Path.Combine(server.StagingAgentDirectory, "agent-host")).FullName;
        MakeStagingDirectoryShared(agentDirectory);
        return server with { StagingServerDirectory = server.StagingServerDirectory + "/agent-host", StagingAgentDirectory = agentDirectory };
    }

    private static MsSqlServer StartMsSql(string stagingDirectory)
    {
        const string serverStaging = "/var/opt/mssql/staging";
        var builder = new MsSqlBuilder(MsSqlImage).WithBindMount(stagingDirectory, serverStaging);
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
                return new MsSqlServer(container.Hostname, container.GetMappedPublicPort(1433), MsSqlBuilder.DefaultUsername, MsSqlBuilder.DefaultPassword, serverStaging, stagingDirectory);
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                try { container.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                Thread.Sleep(500);
            }
        }
        throw new InvalidOperationException("Failed to start MSSQL testcontainer after retries.");
    }

    // atmoz/sftp: OpenSSH internal-sftp chrooted to /home/testuser, with a writable "upload" directory (the base directory the tests use).
    // OpenSSH penalises a source address after failed logins (the authentication-failure tests make some) by dropping its next connections.
    private static SftpServer StartSftp()
    {
        var container = new ContainerBuilder(SftpImage)
            .WithPortBinding(22, assignRandomHostPort: true)
            .WithCommand($"{SftpUser}:{SftpPassword}:::upload")
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes("#!/bin/sh\necho 'PerSourcePenalties no' >> /etc/ssh/sshd_config\n"), "/etc/sftp.d/10-no-penalties.sh",
                fileMode: UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute | UnixFileModes.GroupRead | UnixFileModes.OtherRead) // the entrypoint only runs executable scripts
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server listening on"))
            .Build();
        Start(container);
        var port = container.GetMappedPublicPort(22);
        return new SftpServer(container.Hostname, port, SftpUser, SftpPassword, HostKeyFingerprint(container.Hostname, port), "/upload");
    }

    // The fingerprint of the key the client actually negotiates, computed the way the product pins it (SHA-256 of the key blob).
    private static string HostKeyFingerprint(string host, int port)
    {
        string? fingerprint = null;
        using var client = new SftpClient(host, port, SftpUser, SftpPassword);
        client.HostKeyReceived += (_, e) =>
        {
            fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
            e.CanTrust = true;
        };
        client.Connect();
        return fingerprint ?? throw new InvalidOperationException("The SFTP server presented no host key.");
    }
}


/// <summary>A real SQL Server's address, administrative login, and the one directory the server and the agent both see.</summary>
internal sealed record MsSqlServer(string Host, int Port, string User, string Password, string StagingServerDirectory, string StagingAgentDirectory)
{
    /// <summary>The SQL Server named by BACKUP_PLATFORM_TEST_MSSQL_* (HOST, PORT, USER, PASSWORD, STAGING_SERVER_DIR, STAGING_AGENT_DIR), or null when HOST is unset.</summary>
    public static MsSqlServer? FromEnvironment() => Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_HOST") is not { Length: > 0 } host ? null
        : new(host, int.Parse(Required("PORT"), System.Globalization.CultureInfo.InvariantCulture), Required("USER"), Required("PASSWORD"), Required("STAGING_SERVER_DIR"), Required("STAGING_AGENT_DIR"));

    /// <summary>The SQL Server the real-Agent.Host tests use: the one <see cref="FromEnvironment"/> names, else the Testcontainers one (<see cref="TestContainers.AgentHostMsSqlServer"/>).</summary>
    public static MsSqlServer ForAgentHost => FromEnvironment() ?? TestContainers.AgentHostMsSqlServer;

    private static string Required(string name) => Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_" + name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"BACKUP_PLATFORM_TEST_MSSQL_HOST is set, so BACKUP_PLATFORM_TEST_MSSQL_{name} must be set too.");
}

/// <summary>A real SFTP server: address, password login, host key fingerprint (<c>SHA256:</c>) and the writable base directory.</summary>
internal sealed record SftpServer(string Host, int Port, string User, string Password, string Fingerprint, string BaseDirectory)
{
    /// <summary>The server named by BACKUP_PLATFORM_TEST_SFTP_* (HOST, PORT, USER, PASSWORD, FINGERPRINT, optional BASE_DIR, default /upload), or the Testcontainers one when HOST is unset.</summary>
    public static SftpServer Current => Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_HOST") is not { Length: > 0 } host ? TestContainers.SftpServer
        : new(host, int.Parse(Required("PORT"), System.Globalization.CultureInfo.InvariantCulture), Required("USER"), Required("PASSWORD"), Required("FINGERPRINT"),
            Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_BASE_DIR") is { Length: > 0 } baseDirectory ? baseDirectory : "/upload");

    private static string Required(string name) => Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_" + name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"BACKUP_PLATFORM_TEST_SFTP_HOST is set, so BACKUP_PLATFORM_TEST_SFTP_{name} must be set too.");
}
