using BackupPlatform.Application.Discovery;
using BackupPlatform.Contracts.Discovery;
using BackupPlatform.Domain.Discovery;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests.Discovery;

public sealed class DiscoveryMergerTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly AgentId Agent = AgentId.New();
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);

    private static AgentDiscoveredInstance Instance(string key = "mssql|service|default", string version = "16.0.1000", string reachability = DiscoveryReachability.Reachable,
        string source = DiscoverySources.Service, IReadOnlyList<AgentDiscoveredDatabase>? databases = null) => new()
    {
        InstanceKey = key, Engine = DiscoveryEngines.MsSql, Source = source, DisplayName = key, Host = "localhost", Port = 1433, Version = version,
        Reachability = reachability, Access = DiscoveryAccess.Authenticated, Databases = databases
    };

    private static AgentDiscoveredDatabase Database(string name, bool system = false, string fullBackup = DiscoverySupportNames.Supported) => new()
    {
        Name = name, IsSystem = system, State = DiscoveryDatabaseStates.Online, FullBackup = fullBackup
    };

    private static AgentDiscoveryReport Report(string outcome, string[] scannedSources, params AgentDiscoveredInstance[] instances) => new()
    {
        Outcome = outcome,
        Sources = scannedSources.Select(source => new AgentDiscoverySourceStatus { Source = source, Status = DiscoverySourceStates.Scanned }).ToList(),
        Instances = instances
    };

    private static DiscoveryMergeResult Merge(List<DiscoveredInstance> instances, List<DiscoveredDatabase> databases, AgentDiscoveryReport report, DateTimeOffset now)
    {
        var result = DiscoveryMerger.Apply(Tenant, Agent, instances, databases, report, now);
        instances.AddRange(result.NewInstances);
        databases.AddRange(result.NewDatabases);
        return result;
    }

    [Fact]
    public void A_new_instance_is_discovered_with_its_databases_and_a_repeated_report_changes_nothing()
    {
        var instances = new List<DiscoveredInstance>();
        var databases = new List<DiscoveredDatabase>();
        var report = Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance(databases: [Database("master", true), Database("Sales")]));

        var first = Merge(instances, databases, report, T0);
        var second = Merge(instances, databases, report, T0.AddHours(6));

        Assert.Single(first.NewInstances);
        Assert.Equal(2, first.NewDatabases.Count);
        Assert.Empty(second.NewInstances);
        Assert.Empty(second.NewDatabases);
        var instance = Assert.Single(instances);
        Assert.Equal(DiscoveryChangeKind.Discovered, instance.LastChangeKind);
        Assert.Equal(T0, instance.LastChangeAt);
        Assert.Equal(T0.AddHours(6), instance.LastSeenAt);
        Assert.Equal(T0.AddHours(6), instance.DatabasesCheckedAt);
    }

    [Fact]
    public void A_version_change_is_recorded_as_changed_and_an_unreachable_then_reachable_instance_as_recovered()
    {
        var instances = new List<DiscoveredInstance>();
        var databases = new List<DiscoveredDatabase>();
        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance()), T0);

        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance(version: "16.0.4000")), T0.AddHours(1));
        Assert.Equal(DiscoveryChangeKind.Changed, instances[0].LastChangeKind);

        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance(version: "16.0.4000", reachability: DiscoveryReachability.Unreachable)), T0.AddHours(2));
        Assert.Equal(DiscoveryChangeKind.Unreachable, instances[0].LastChangeKind);

        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance(version: "16.0.4000")), T0.AddHours(3));
        Assert.Equal(DiscoveryChangeKind.Recovered, instances[0].LastChangeKind);
    }

    [Fact]
    public void An_instance_becomes_missing_only_after_consecutive_complete_scans_of_its_source_and_reappears_later()
    {
        var instances = new List<DiscoveredInstance>();
        var databases = new List<DiscoveredDatabase>();
        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance()), T0);

        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service]), T0.AddHours(1));
        Assert.Equal(DiscoveryPresence.Present, instances[0].Presence);

        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service]), T0.AddHours(2));
        Assert.Equal(DiscoveryPresence.Missing, instances[0].Presence);
        Assert.Equal(DiscoveryChangeKind.Missing, instances[0].LastChangeKind);

        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance()), T0.AddHours(3));
        Assert.Equal(DiscoveryPresence.Present, instances[0].Presence);
        Assert.Equal(DiscoveryChangeKind.Reappeared, instances[0].LastChangeKind);
        Assert.Equal(0, instances[0].MissCount);
    }

    [Theory]
    [InlineData(DiscoverySourceStates.Unavailable)]
    [InlineData(DiscoverySourceStates.Failed)]
    [InlineData(DiscoverySourceStates.Disabled)]
    public void An_instance_of_a_source_that_was_not_read_is_never_counted_as_missing(string dockerState)
    {
        var instances = new List<DiscoveredInstance>();
        var databases = new List<DiscoveredDatabase>();
        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Docker], Instance("mssql|docker|sql1|1433", source: DiscoverySources.Docker)), T0);
        var partial = new AgentDiscoveryReport
        {
            Outcome = DiscoveryOutcomes.Partial,
            Sources = [new AgentDiscoverySourceStatus { Source = DiscoverySources.Docker, Status = dockerState }]
        };

        for (var scan = 1; scan <= 5; scan++)
        {
            Merge(instances, databases, partial, T0.AddHours(scan));
        }

        Assert.Equal(DiscoveryPresence.Present, instances[0].Presence);
        Assert.Equal(0, instances[0].MissCount);
    }

    [Fact]
    public void A_failed_scan_changes_nothing()
    {
        var instances = new List<DiscoveredInstance>();
        var databases = new List<DiscoveredDatabase>();
        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance(databases: [Database("Sales")])), T0);

        var result = Merge(instances, databases, new AgentDiscoveryReport { Outcome = DiscoveryOutcomes.Failed, FailureCode = "scan_timeout" }, T0.AddHours(1));

        Assert.Empty(result.NewInstances);
        Assert.Equal(0, instances[0].MissCount);
        Assert.Equal(T0, instances[0].LastSeenAt);
    }

    [Fact]
    public void Databases_are_replaced_only_when_enumerated_and_a_vanished_database_is_kept_as_missing_then_purged_after_the_retention()
    {
        var instances = new List<DiscoveredInstance>();
        var databases = new List<DiscoveredDatabase>();
        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance(databases: [Database("Sales"), Database("Old")])), T0);

        // Credentials lost: not enumerated, the list is untouched and the check time says how old it is.
        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance(databases: null)), T0.AddHours(1));
        Assert.All(databases, database => Assert.Equal(DiscoveryPresence.Present, database.Presence));
        Assert.Equal(T0, instances[0].DatabasesCheckedAt);

        var vanished = Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance(databases: [Database("Sales")])), T0.AddHours(2));
        Assert.Equal(DiscoveryPresence.Missing, databases.Single(database => database.Name == "Old").Presence);
        Assert.Empty(vanished.PurgedDatabases);

        var purged = Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance(databases: [Database("Sales")])), T0.AddDays(31));
        Assert.Equal("Old", Assert.Single(purged.PurgedDatabases).Name);
    }

    [Fact]
    public void A_credential_binding_survives_every_observation()
    {
        var instances = new List<DiscoveredInstance>();
        var databases = new List<DiscoveredDatabase>();
        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance()), T0);
        instances[0].Bind("sql-main", false, true, false);

        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance(version: "16.0.4000")), T0.AddHours(1));

        Assert.Equal("sql-main", instances[0].CredentialReference);
        Assert.True(instances[0].HasBinding);
    }

    [Fact]
    public void A_scan_that_did_not_look_at_an_instance_keeps_what_is_known_about_its_access()
    {
        var instances = new List<DiscoveredInstance>();
        var databases = new List<DiscoveredDatabase>();
        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance(databases: [Database("Sales")])), T0);

        // A targeted scan of another instance reports this one with the default Unknown access.
        Merge(instances, databases, Report(DiscoveryOutcomes.Completed, [DiscoverySources.Service], Instance() with { Access = DiscoveryAccess.Unknown, FullBackup = DiscoverySupportNames.Unknown }), T0.AddHours(1));

        Assert.Equal(DiscoveryAccess.Authenticated, instances[0].Access);
        Assert.Equal(T0.AddHours(1), instances[0].LastSeenAt);
    }

    private static DiscoveredInstance InstanceOf(string engine) => new(Guid.NewGuid(), Tenant, Agent, "key",
        new InstanceObservation(engine, DiscoverySources.Service, "x", null, "localhost", 1, null, null, null, null, null, "Unknown", "Unknown", null, "Unknown", null), T0);

    [Theory]
    [InlineData("mssql", "sql-main", false)]
    [InlineData("mssql", null, true)]
    [InlineData("postgres", "pg-main", false)]
    [InlineData("postgres", null, false)]
    public void A_credential_can_be_bound_set_or_cleared(string engine, string? reference, bool integrated)
    {
        var instance = InstanceOf(engine);

        instance.Bind(reference, integrated, true, false);

        Assert.Equal(reference, instance.CredentialReference);
        Assert.Equal(reference is not null || integrated, instance.HasBinding);
    }

    [Theory]
    [InlineData("postgres", null, true)]
    [InlineData("mssql", "sql-main", true)]
    [InlineData("mssql", "has space", false)]
    [InlineData("mssql", "../escape", false)]
    public void An_unusable_credential_binding_is_refused(string engine, string? reference, bool integrated)
    {
        var instance = InstanceOf(engine);

        Assert.Throws<ArgumentException>(() => instance.Bind(reference, integrated, true, false));
        Assert.False(instance.HasBinding);
    }
}
