using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Agent.Platform.Linux.Discovery;
using BackupPlatform.Contracts.Discovery;

namespace BackupPlatform.AgentTests;

/// <summary>Compiled only for the Linux host build (see the project file): the detector exists only in that platform project.</summary>
public sealed class LinuxSystemdDetectorTests
{
    private const string Units = """
        cron.service                loaded active running Regular Background Program Processing Daemon
        mssql-server.service        loaded active running Microsoft SQL Server Database Engine
        postgresql@16-main.service  loaded active running PostgreSQL Cluster 16-main
        postgresql-15.service       loaded active running PostgreSQL 15 database server
        """;

    private const string Clusters = """
        16  main    5433 online postgres /var/lib/postgresql/16/main /var/log/postgresql/postgresql-16-main.log
        14  old     5434 down   postgres /var/lib/postgresql/14/old  /var/log/postgresql/postgresql-14-old.log
        """;

    private static ToolResult Ok(string output) => new(ToolOutcome.Completed, 0, output, string.Empty);

    private static ToolResult Missing => new(ToolOutcome.NotFound, -1, string.Empty, string.Empty);

    private static Func<string, string?> Files(params (string Path, string Text)[] files) =>
        path => files.Where(file => file.Path == path).Select(file => file.Text).FirstOrDefault();

    [Fact]
    public void Running_units_and_online_clusters_become_service_instances_with_ports_from_the_configuration()
    {
        var result = LinuxSystemdDetector.Build(Ok(Units), Ok(Clusters), Files(
            ("/var/opt/mssql/mssql.conf", "[network]\ntcpport = 14333\n"),
            ("/var/lib/pgsql/15/data/postgresql.conf", "port = 5440\n")));

        Assert.Equal(DiscoverySourceStates.Scanned, result.Status);
        var byKey = result.Instances.ToDictionary(instance => instance.Key);
        Assert.Equal(["mssql|service|default", "postgres|service|16-main", "postgres|service|postgresql-15"], byKey.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(14333, byKey["mssql|service|default"].Port);
        Assert.Equal(5433, byKey["postgres|service|16-main"].Port);
        Assert.Equal("16", byKey["postgres|service|16-main"].Version);
        Assert.Equal(5440, byKey["postgres|service|postgresql-15"].Port);
        Assert.All(result.Instances, instance => Assert.Equal("localhost", instance.Host));
        Assert.All(result.Instances, instance => Assert.Equal(DiscoverySources.Service, instance.Source));
    }

    [Fact]
    public void Unreadable_configuration_falls_back_to_the_default_ports_and_stopped_clusters_are_not_instances()
    {
        var result = LinuxSystemdDetector.Build(Ok(Units), Ok(Clusters), Files());

        Assert.Equal(1433, result.Instances.Single(instance => instance.Engine == DiscoveryEngines.MsSql).Port);
        Assert.Equal(5432, result.Instances.Single(instance => instance.Key == "postgres|service|postgresql-15").Port);
        Assert.DoesNotContain(result.Instances, instance => instance.Key.Contains("old", StringComparison.Ordinal));
    }

    [Fact]
    public void A_debian_cluster_unit_without_pg_lsclusters_reads_its_cluster_configuration()
    {
        var result = LinuxSystemdDetector.Build(Ok("postgresql@16-main.service loaded active running PostgreSQL Cluster 16-main"), Missing,
            Files(("/etc/postgresql/16/main/postgresql.conf", "port = 5435\n")));

        var instance = Assert.Single(result.Instances);
        Assert.Equal("postgres|service|16-main", instance.Key);
        Assert.Equal(5435, instance.Port);
    }

    [Fact]
    public void Nothing_running_is_a_scanned_source_without_instances()
    {
        var result = LinuxSystemdDetector.Build(Ok("cron.service loaded active running Cron"), Missing, Files());

        Assert.Equal(DiscoverySourceStates.Scanned, result.Status);
        Assert.Empty(result.Instances);
    }

    [Fact]
    public void Absent_tools_make_the_source_unavailable_and_pg_lsclusters_alone_still_scans()
    {
        var neither = LinuxSystemdDetector.Build(Missing, Missing, Files());
        var clustersOnly = LinuxSystemdDetector.Build(Missing, Ok(Clusters), Files());

        Assert.Equal(DiscoverySourceStates.Unavailable, neither.Status);
        Assert.Equal("systemd_not_found", neither.ReasonCode);
        Assert.Equal(DiscoverySourceStates.Scanned, clustersOnly.Status);
        Assert.Equal("postgres|service|16-main", Assert.Single(clustersOnly.Instances).Key);
    }

    [Fact]
    public void A_host_not_booted_with_systemd_is_unavailable_not_failed()
    {
        var notBooted = new ToolResult(ToolOutcome.Completed, 1, string.Empty, "System has not been booted with systemd as init system (PID 1). Can't operate.");

        var result = LinuxSystemdDetector.Build(notBooted, Missing, Files());

        Assert.Equal(DiscoverySourceStates.Unavailable, result.Status);
        Assert.Equal("systemd_not_running", result.ReasonCode);
    }

    [Theory]
    [InlineData("AccessDenied", DiscoverySourceStates.Unavailable, "systemd_access_denied")]
    [InlineData("TimedOut", DiscoverySourceStates.Failed, "systemd_failed")]
    [InlineData("Failed", DiscoverySourceStates.Failed, "systemd_failed")]
    public void A_tool_that_cannot_run_or_overstays_is_reported_honestly(string outcome, string status, string reason)
    {
        var result = LinuxSystemdDetector.Build(new ToolResult(Enum.Parse<ToolOutcome>(outcome), -1, string.Empty, string.Empty), Ok(Clusters), Files());

        Assert.Equal(status, result.Status);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Empty(result.Instances);
    }
}
