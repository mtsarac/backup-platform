using BackupPlatform.Application.Discovery;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Discovery;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests.Discovery;

public sealed class DiscoveryJobMatcherTests
{
    private static DiscoveredInstance Instance(string engine, string host = "localhost", int? port = 1433) => new(
        Guid.NewGuid(), new TenantId(Guid.NewGuid()), AgentId.New(), "key",
        new InstanceObservation(engine, "Service", "x", null, host, port, null, null, null, null, null, "Reachable", "Authenticated", null, "Supported", null), DateTimeOffset.UnixEpoch);

    private static BackupConfiguration Job(string engine, string database, string host = "localhost", int? port = 1433) =>
        new(engine, database, host, port, false, "ref", true, false, "default", "", true, "Preferred", "Preferred");

    [Theory]
    [InlineData("localhost", "127.0.0.1", true)]
    [InlineData(".", "localhost", true)]
    [InlineData("db.example.com", "localhost", false)]
    public void Only_a_job_addressing_the_local_machine_covers_a_discovered_database(string jobHost, string instanceHost, bool covers) =>
        Assert.Equal(covers, DiscoveryJobMatcher.Covers(Job("mssql", "Sales", jobHost), Instance("mssql", instanceHost), "Sales"));

    [Fact]
    public void The_database_name_is_case_insensitive_for_mssql_and_case_sensitive_for_postgresql()
    {
        Assert.True(DiscoveryJobMatcher.Covers(Job("mssql", "SALES"), Instance("mssql"), "Sales"));
        Assert.False(DiscoveryJobMatcher.Covers(Job("postgres", "SALES", port: 5432), Instance("postgres", port: 5432), "Sales"));
    }

    [Fact]
    public void A_job_without_a_port_uses_the_engine_default_and_a_different_port_or_engine_never_matches()
    {
        Assert.True(DiscoveryJobMatcher.Covers(Job("mssql", "Sales", port: null), Instance("mssql"), "Sales"));
        Assert.True(DiscoveryJobMatcher.Covers(Job("postgres", "app", port: null), Instance("postgres", port: 5432), "app"));
        Assert.False(DiscoveryJobMatcher.Covers(Job("mssql", "Sales", port: 14333), Instance("mssql"), "Sales"));
        Assert.False(DiscoveryJobMatcher.Covers(Job("postgres", "Sales", port: 1433), Instance("mssql"), "Sales"));
    }
}
