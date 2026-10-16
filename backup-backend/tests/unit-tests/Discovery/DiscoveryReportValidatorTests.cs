using BackupPlatform.Application.Discovery;
using BackupPlatform.Contracts.Discovery;

namespace BackupPlatform.UnitTests.Discovery;

public sealed class DiscoveryReportValidatorTests
{
    private static AgentDiscoveredInstance Instance(string key = "mssql|service|default") => new()
    {
        InstanceKey = key, Engine = DiscoveryEngines.MsSql, Source = DiscoverySources.Service, DisplayName = "SQL Server", Port = 1433,
        Reachability = DiscoveryReachability.Reachable, Access = DiscoveryAccess.CredentialsRequired
    };

    private static AgentDiscoveryReport Report(params AgentDiscoveredInstance[] instances) => new()
    {
        Outcome = DiscoveryOutcomes.Completed,
        Sources = [new AgentDiscoverySourceStatus { Source = DiscoverySources.Service, Status = DiscoverySourceStates.Scanned }],
        Instances = instances
    };

    [Fact]
    public void A_well_formed_report_is_accepted_including_one_without_instances()
    {
        Assert.True(DiscoveryReportValidator.TryValidate(Report(Instance()), out var field));
        Assert.Null(field);
        Assert.True(DiscoveryReportValidator.TryValidate(Report(), out _));
    }

    [Theory]
    [InlineData("outcome")]
    [InlineData("engine")]
    [InlineData("access")]
    [InlineData("port")]
    [InlineData("control-character")]
    [InlineData("long-name")]
    [InlineData("free-text-reason")]
    public void Anything_outside_the_vocabulary_or_bounds_is_refused_with_the_field_path(string defect)
    {
        var instance = defect switch
        {
            "engine" => Instance() with { Engine = "oracle" },
            "access" => Instance() with { Access = "Granted" },
            "port" => Instance() with { Port = 70000 },
            "control-character" => Instance() with { DisplayName = "bad\nname" },
            "long-name" => Instance() with { DisplayName = new string('x', 201) },
            "free-text-reason" => Instance() with { AccessReasonCode = "login failed for user 'sa'" },
            _ => Instance()
        };
        var report = Report(instance);
        if (defect == "outcome") report = report with { Outcome = "Done" };

        Assert.False(DiscoveryReportValidator.TryValidate(report, out var field));
        Assert.NotNull(field);
        // The error names the field, never the offending value.
        Assert.DoesNotContain("login failed", field, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_instances_and_duplicate_databases_are_refused()
    {
        Assert.False(DiscoveryReportValidator.TryValidate(Report(Instance(), Instance()), out var duplicateInstance));
        Assert.Equal("Instances[1].InstanceKey", duplicateInstance);

        var database = new AgentDiscoveredDatabase { Name = "Sales", State = DiscoveryDatabaseStates.Online, FullBackup = DiscoverySupportNames.Supported };
        Assert.False(DiscoveryReportValidator.TryValidate(Report(Instance() with { Databases = [database, database] }), out var duplicateDatabase));
        Assert.Equal("Instances[0].Databases[1].Name", duplicateDatabase);
    }

    [Fact]
    public void Cardinality_limits_are_enforced()
    {
        var tooMany = Enumerable.Range(0, DiscoveryProtocol.MaximumInstancesPerReport + 1).Select(index => Instance($"mssql|listener|{index}")).ToArray();
        Assert.False(DiscoveryReportValidator.TryValidate(Report(tooMany), out var instances));
        Assert.Equal("Instances", instances);

        var databases = Enumerable.Range(0, DiscoveryProtocol.MaximumDatabasesPerInstance + 1)
            .Select(index => new AgentDiscoveredDatabase { Name = $"db{index}", State = DiscoveryDatabaseStates.Online, FullBackup = DiscoverySupportNames.Supported }).ToList();
        Assert.False(DiscoveryReportValidator.TryValidate(Report(Instance() with { Databases = databases }), out var field));
        Assert.Equal("Instances[0].Databases", field);
    }

    [Fact]
    public void The_total_number_of_databases_in_a_report_is_bounded()
    {
        AgentDiscoveredDatabase[] Databases(int count) => [.. Enumerable.Range(0, count)
            .Select(index => new AgentDiscoveredDatabase { Name = $"db{index}", State = DiscoveryDatabaseStates.Online, FullBackup = DiscoverySupportNames.Supported })];
        var instances = Enumerable.Range(0, 5).Select(index => Instance($"mssql|listener|{index}") with { Databases = Databases(DiscoveryProtocol.MaximumDatabasesPerInstance) }).ToArray();

        Assert.False(DiscoveryReportValidator.TryValidate(Report(instances), out var field));
        Assert.Equal("Instances", field);
        Assert.True(DiscoveryReportValidator.TryValidate(Report(instances.Take(4).ToArray()), out _));
    }

    [Theory]
    [InlineData("sql-main", true)]
    [InlineData("Prod_DB.01", true)]
    [InlineData("has space", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void Credential_references_are_plain_names(string reference, bool valid) =>
        Assert.Equal(valid, DiscoveryReportValidator.IsValidReference(reference));
}
