using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Contracts.Discovery;
using BackupPlatform.Agent.Transport;

namespace BackupPlatform.AgentTests;

public sealed class DiscoveryParserTests
{
    // ---- docker -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("mcr.microsoft.com/mssql/server:2022-latest", 1433, DiscoveryEngines.MsSql)]
    [InlineData("postgres:16", 5432, DiscoveryEngines.PostgreSql)]
    [InlineData("bitnami/postgresql:15", 5432, DiscoveryEngines.PostgreSql)]
    [InlineData("postgis/postgis:16-3.4", 5432, DiscoveryEngines.PostgreSql)]
    [InlineData("localhost:5000/postgres:16", 5432, DiscoveryEngines.PostgreSql)]
    [InlineData("postgres:16", 1433, null)]
    [InlineData("prometheuscommunity/postgres-exporter", 5432, null)]
    [InlineData("redis:7", 5432, null)]
    public void The_engine_needs_a_matching_image_and_container_port(string image, int containerPort, string? engine)
    {
        Assert.Equal(engine, DiscoveryParsers.ClassifyDockerEngine(image, containerPort));
    }

    [Fact]
    public async Task Docker_is_disabled_when_the_plan_excludes_it()
    {
        var result = await new DockerContainerDetector(new DockerEngineHttpTransport()).DetectAsync(new DiscoveryDetectionContext(IncludeDocker: false), default);

        Assert.Equal(DiscoverySourceStates.Disabled, result.Status);
    }

    // ---- TDS / PostgreSQL handshake -----------------------------------------------------------------------------

    [Fact]
    public void The_prelogin_request_is_a_well_formed_47_byte_tds_packet()
    {
        var packet = DiscoveryParsers.BuildMsSqlPreLogin();

        Assert.Equal(47, packet.Length);
        Assert.Equal(0x12, packet[0]);
        Assert.Equal(packet.Length, (packet[2] << 8) | packet[3]);
        Assert.Equal(0xFF, packet[8 + 25]);
    }

    [Fact]
    public void A_prelogin_answer_yields_the_server_version()
    {
        // SQL Server 16.0.4135: header, VERSION option (offset 6, length 6) then ENCRYPTION option, terminator, data.
        byte[] response =
        [
            0x04, 0x01, 0x00, 0x20, 0x00, 0x00, 0x01, 0x00,
            0x00, 0x00, 0x0B, 0x00, 0x06,
            0x01, 0x00, 0x11, 0x00, 0x01,
            0xFF,
            0x10, 0x00, 0x10, 0x27, 0x00, 0x00,
            0x00,
        ];

        // Offsets are relative to the payload, which starts after the 8-byte header.
        Assert.True(DiscoveryParsers.TryParsePreLoginResponse(response, out var version));
        Assert.Equal("16.0.4135", version);
    }

    [Fact]
    public void A_tds_answer_without_a_version_option_is_still_recognised_and_anything_else_is_not()
    {
        byte[] noVersion = [0x04, 0x01, 0x00, 0x0A, 0, 0, 1, 0, 0xFF, 0x00];

        Assert.True(DiscoveryParsers.TryParsePreLoginResponse(noVersion, out var version));
        Assert.Null(version);
        Assert.False(DiscoveryParsers.TryParsePreLoginResponse([0x45, 0, 0, 0, 0, 0, 0, 0, 0], out _));
        Assert.False(DiscoveryParsers.TryParsePreLoginResponse([0x04, 0x01], out _));
        // An offset pointing past the packet is ignored, not read.
        Assert.True(DiscoveryParsers.TryParsePreLoginResponse([0x04, 0x01, 0x00, 0x0F, 0, 0, 1, 0, 0x00, 0x00, 0xF0, 0x00, 0x06, 0xFF, 0x00], out var bogus));
        Assert.Null(bogus);
    }

    [Fact]
    public void The_postgresql_ssl_request_and_its_two_valid_answers()
    {
        byte[] expected = [0x00, 0x00, 0x00, 0x08, 0x04, 0xD2, 0x16, 0x2F];
        Assert.Equal(expected, DiscoveryParsers.PostgreSqlSslRequest.ToArray());
        Assert.True(DiscoveryParsers.IsPostgreSqlSslAnswer((byte)'S'));
        Assert.True(DiscoveryParsers.IsPostgreSqlSslAnswer((byte)'N'));
        Assert.False(DiscoveryParsers.IsPostgreSqlSslAnswer((byte)'E'));
    }

    // ---- configuration files and tools --------------------------------------------------------------------------

    [Theory]
    [InlineData("port = 5433", 5433)]
    [InlineData("#port = 5432\nport = 5433    # comment", 5433)]
    [InlineData("port=5434", 5434)]
    [InlineData("port = '5435'", 5435)]
    [InlineData("port = 5000\nport = 5001", 5001)]
    [InlineData("portal = 1234", null)]
    [InlineData("# port = 5432", null)]
    [InlineData("port = 70000", null)]
    [InlineData("", null)]
    public void The_postgresql_conf_port_is_the_last_uncommented_assignment(string text, int? expected)
    {
        Assert.Equal(expected, DiscoveryParsers.ParsePostgreSqlConfPort(text));
    }

    [Theory]
    [InlineData("[network]\ntcpport = 1434\n", 1434)]
    [InlineData("[sqlagent]\nenabled = true\n\n[network]\r\nTcpPort=14333\r\n", 14333)]
    [InlineData("[other]\ntcpport = 1999\n", null)]
    [InlineData("[network]\nipaddress = 10.0.0.1\n", null)]
    public void The_mssql_conf_tcpport_is_read_from_the_network_section(string text, int? expected)
    {
        Assert.Equal(expected, DiscoveryParsers.ParseMssqlConfTcpPort(text));
    }

    [Fact]
    public void A_pg_lsclusters_line_has_version_cluster_port_and_status()
    {
        var cluster = DiscoveryParsers.ParsePgLsClustersLine("16  main    5432 online postgres /var/lib/postgresql/16/main /var/log/postgresql/postgresql-16-main.log");

        Assert.Equal(new PostgreSqlCluster("16", "main", 5432, "online"), cluster);
        Assert.Equal("down", DiscoveryParsers.ParsePgLsClustersLine("14  test    5433 down   postgres /var/lib/postgresql/14/test /var/log/postgresql/postgresql-14-test.log")!.Status);
        Assert.Null(DiscoveryParsers.ParsePgLsClustersLine("Ver Cluster Port Status Owner"));
        Assert.Null(DiscoveryParsers.ParsePgLsClustersLine(""));
    }

    [Fact]
    public void Only_sql_server_and_postgresql_units_are_taken_from_the_systemctl_listing()
    {
        const string output = """
            cron.service                       loaded active running Regular Background Program Processing Daemon
            mssql-server.service               loaded active running Microsoft SQL Server Database Engine
            postgresql@16-main.service         loaded active running PostgreSQL Cluster 16-main
            postgresql-15.service              loaded active running PostgreSQL 15 database server
            ssh.service                        loaded active running OpenBSD Secure Shell server
            """;

        Assert.Equal(["mssql-server", "postgresql@16-main", "postgresql-15"], DiscoveryParsers.ParseSystemdUnits(output));
        Assert.Empty(DiscoveryParsers.ParseSystemdUnits(""));
    }
}
