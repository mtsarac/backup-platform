using BackupPlatform.Agent.Host;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Capabilities;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.AgentTests;

public sealed class SqlServerEligibilityTests
{
    private static string Abs(string name) => Path.Combine(Path.GetTempPath(), name);
    private static bool Writable(string _) => true;
    private static bool NotWritable(string _) => false;

    [Fact]
    public void A_Linux_agent_with_a_valid_mapped_staging_area_is_eligible() =>
        Assert.True(SqlServerEligibility.IsEligible(new() { Mapping = StagingMappingKind.Mapped, AgentDirectory = Abs("a"), ServerDirectory = "/var/opt/mssql/staging" }, windows: false, Writable));

    [Theory]
    [InlineData(null, null)]
    [InlineData(StagingMappingKind.SameHost, null)]
    [InlineData(StagingMappingKind.Mapped, null)]
    [InlineData(StagingMappingKind.Mapped, "SAME")]
    public void A_Linux_agent_without_a_valid_mapped_staging_area_is_not_eligible(StagingMappingKind? mapping, string? server)
    {
        var agent = Abs("a");
        var staging = mapping is null ? null : new AgentBackupStagingOptions { Mapping = mapping, AgentDirectory = agent, ServerDirectory = server == "SAME" ? agent : server };
        Assert.False(SqlServerEligibility.IsEligible(staging, windows: false, Writable));
    }

    [Fact]
    public void A_mapped_area_whose_agent_directory_is_missing_or_read_only_is_not_eligible() =>
        Assert.False(SqlServerEligibility.IsEligible(new() { Mapping = StagingMappingKind.Mapped, AgentDirectory = Abs("a"), ServerDirectory = "/srv/sql" }, windows: false, NotWritable));

    [Fact]
    public void A_Windows_agent_is_always_eligible() => Assert.True(SqlServerEligibility.IsEligible(null, windows: true, NotWritable));

    [Fact]
    public void The_writability_probe_needs_a_real_writable_directory_and_leaves_nothing_behind()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.True(SqlServerEligibility.IsWritableDirectory(directory));
            Assert.Empty(Directory.GetFileSystemEntries(directory));
            Assert.False(SqlServerEligibility.IsWritableDirectory(Path.Combine(directory, "missing")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void An_ineligible_agent_reports_every_SQL_Server_capability_unsupported_and_changes_its_revision()
    {
        var eligible = AgentCapabilityCatalog.Create(sqlServer: true);
        var ineligible = AgentCapabilityCatalog.Create(sqlServer: false);
        string[] sql = [SqlServerCapabilityIds.FullBackup.Value, SqlServerCapabilityIds.BackupChecksum.Value, SqlServerCapabilityIds.RestoreDatabase.Value];
        Assert.All(sql, id => Assert.Equal(AgentCapabilitySupportNames.Supported, eligible.Capabilities.Single(c => c.Identifier == id).Support));
        Assert.All(sql, id => Assert.Equal(AgentCapabilitySupportNames.Unsupported, ineligible.Capabilities.Single(c => c.Identifier == id).Support));
        Assert.Equal(eligible.Capabilities.Count, ineligible.Capabilities.Count);
        Assert.NotEqual(eligible.Revision, ineligible.Revision);
        Assert.Equal(AgentCapabilitySupportNames.Supported, ineligible.Capabilities.Single(c => c.Identifier == "file-system.backup.full").Support);
    }
}
