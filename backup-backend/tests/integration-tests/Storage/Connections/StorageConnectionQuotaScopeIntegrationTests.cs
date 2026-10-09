using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.Storage;
using static BackupPlatform.IntegrationTests.Quota.UsageTestKit;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

/// <summary>
/// Quota selectors by provider type, ownership, quota scope label and storage connection, against the roadmap example: one 2 GB artifact stored
/// on the managed (central) default, on a Google-Drive-like and on an SFTP-like destination. Usage must equal the independent SQL oracle.
/// </summary>
public sealed class StorageConnectionQuotaScopeIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const long Gb = QuotaWorld.Gb;
    private readonly ConnectionWorld _world = new(fixture);

    private sealed record Roadmap(StoreAgent Agent, Guid ArtifactId, Guid Central, Guid GoogleDrive, Guid Sftp);

    /// <summary>A tenant (100 GB, default policy) with one 2 GB artifact copied to the managed default and to two stand-in providers, published through the real lifecycle.</summary>
    private async Task<Roadmap> RoadmapAsync()
    {
        var agent = await _world.Quota.AgentAsync(100 * Gb);
        var job = await _world.Quota.JobAsync(agent);
        var (_, artifactId) = await _world.Quota.PublishCopiesAsync(agent, job, 2 * Gb, UsageTestKit.AllThree);
        var connections = await _world.RowsAsync(agent.TenantId);
        return new Roadmap(agent, artifactId, Id(connections, "central"), Id(connections, "googledrive"), Id(connections, "sftp"));

        static Guid Id(List<StorageConnection> rows, string provider) => rows.Single(row => row.ProviderType == provider).Id;
    }

    /// <summary>Counts by <paramref name="mode"/> and <paramref name="scope"/> from now on: a new plan whose policy is the new one.</summary>
    private async Task CountByAsync(StoreAgent agent, QuotaAccountingMode mode, QuotaScopeRule scope)
    {
        var policy = await _world.Quota.CreatePolicyAsync(mode, scope);
        await _world.Quota.AssignAsync(agent.TenantId, await _world.Quota.PlanAsync(100 * Gb, policy.Key));
        Assert.NotNull(await _world.Quota.Usage().GetUsageAsync(agent.TenantId, default));
    }

    private static QuotaScopeRule RuleOf(QuotaScopeKind kind, params string[] values) => QuotaScopeRule.Create([new QuotaScopeSelector(kind, values)]);

    private static string Token(Roadmap roadmap, string token) => token switch
    {
        "@central" => roadmap.Central.ToString("D"),
        "@googledrive" => roadmap.GoogleDrive.ToString("D"),
        "@sftp" => roadmap.Sftp.ToString("D"),
        "@unknown" => Guid.NewGuid().ToString("D"),
        _ => token
    };

    public static TheoryData<QuotaAccountingMode, QuotaScopeKind, string, long> Cases() => new()
    {
        // Logical: the artifact is counted once if ANY of its replicas is in scope.
        { QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeKind.All, "", 2 },
        { QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeKind.QuotaScope, "managed", 2 },
        { QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeKind.QuotaScope, "tenant", 2 },
        { QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeKind.QuotaScope, "nothing", 0 },
        { QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeKind.OwnershipType, "PlatformManaged", 2 },
        { QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeKind.OwnershipType, "TenantManaged", 2 },
        { QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeKind.ProviderType, "sftp", 2 },
        { QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeKind.ProviderType, "local", 0 },
        { QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeKind.StorageConnection, "@sftp", 2 },
        { QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeKind.StorageConnection, "@unknown", 0 },
        // Physical: every replica in scope adds its own bytes.
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.All, "", 6 },
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.QuotaScope, "managed", 2 },
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.QuotaScope, "tenant", 4 },
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.QuotaScope, "nothing", 0 },
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.OwnershipType, "PlatformManaged", 2 },
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.OwnershipType, "TenantManaged", 4 },
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.ProviderType, "central", 2 },
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.ProviderType, "sftp", 2 },
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.ProviderType, "central,sftp", 4 },
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.ProviderType, "local", 0 },
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.StorageConnection, "@sftp", 2 },
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.StorageConnection, "@central", 2 },
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.StorageConnection, "@googledrive,@sftp", 4 },
        { QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeKind.StorageConnection, "@unknown", 0 }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task APolicyCountsExactlyTheReplicasOnTheConnectionsItsSelectorMatchesAndTheFigureIsTheOracles(
        QuotaAccountingMode mode, QuotaScopeKind kind, string values, long expectedGb)
    {
        var roadmap = await RoadmapAsync();
        var selected = values.Length == 0 ? [] : values.Split(',').Select(value => Token(roadmap, value)).ToArray();

        await CountByAsync(roadmap.Agent, mode, kind == QuotaScopeKind.All ? QuotaScopeRule.Create([new QuotaScopeSelector(QuotaScopeKind.All, [])]) : RuleOf(kind, selected));

        await _world.Quota.AssertUsedAsync(roadmap.Agent.TenantId, expectedGb * Gb);
        await _world.Quota.AssertNoDriftAsync(roadmap.Agent.TenantId);
        // The independent oracle (joined on the replica's connection row, no accumulator) agrees: one filter per selector value, never summed across replicas twice.
        var oracle = await OracleAsync(roadmap, kind, values);
        Assert.Equal(expectedGb * Gb, mode == QuotaAccountingMode.LogicalArtifactBytes ? oracle.Logical : oracle.Physical);
    }

    private async Task<(long Logical, long Physical)> OracleAsync(Roadmap roadmap, QuotaScopeKind kind, string values)
    {
        var tenant = roadmap.Agent.TenantId;
        var connection = fixture.ConnectionString;
        var providerOfToken = new Dictionary<string, string> { ["@central"] = "central", ["@googledrive"] = "googledrive", ["@sftp"] = "sftp" };
        var filters = values.Length == 0 ? [""] : values.Split(',');
        long physical = 0;
        long logical = 0;
        foreach (var value in filters)
        {
            var (l, p) = kind switch
            {
                QuotaScopeKind.All => await ConnectionWorld.OracleAsync(connection, tenant),
                QuotaScopeKind.ProviderType => await ConnectionWorld.OracleAsync(connection, tenant, providerType: value),
                QuotaScopeKind.OwnershipType => await ConnectionWorld.OracleAsync(connection, tenant, ownership: value),
                QuotaScopeKind.QuotaScope => await ConnectionWorld.OracleAsync(connection, tenant, quotaScope: value),
                _ => value == "@unknown" ? (0, 0) : await ConnectionWorld.OracleAsync(connection, tenant, providerType: providerOfToken[value])
            };
            physical += p;
            logical = Math.Max(logical, l); // one artifact in the example: a union of selectors still counts it once
        }
        return (logical, physical);
    }

    [Fact]
    public async Task TheRoadmapExampleHasTheStatedFiguresUnderEveryAccountingModeAndScope()
    {
        var roadmap = await RoadmapAsync();

        await _world.Quota.AssertOracleAsync(roadmap.Agent.TenantId, 2 * Gb, 6 * Gb);
        await CountByAsync(roadmap.Agent, QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.Create([new QuotaScopeSelector(QuotaScopeKind.All, [])]));
        await _world.Quota.AssertUsedAsync(roadmap.Agent.TenantId, 2 * Gb);
        await CountByAsync(roadmap.Agent, QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.Create([new QuotaScopeSelector(QuotaScopeKind.All, [])]));
        await _world.Quota.AssertUsedAsync(roadmap.Agent.TenantId, 6 * Gb);
        await CountByAsync(roadmap.Agent, QuotaAccountingMode.PhysicalReplicaBytes, RuleOf(QuotaScopeKind.QuotaScope, "managed"));
        await _world.Quota.AssertUsedAsync(roadmap.Agent.TenantId, 2 * Gb);
        await CountByAsync(roadmap.Agent, QuotaAccountingMode.LogicalArtifactBytes, RuleOf(QuotaScopeKind.QuotaScope, "managed"));
        await _world.Quota.AssertUsedAsync(roadmap.Agent.TenantId, 2 * Gb);
    }

    [Fact]
    public async Task ChangingAConnectionsQuotaScopeMovesTheFigureOfAPolicyThatSelectsThatLabelEachTimeAndAlwaysEqualsTheOracle()
    {
        var roadmap = await RoadmapAsync();
        await CountByAsync(roadmap.Agent, QuotaAccountingMode.PhysicalReplicaBytes, RuleOf(QuotaScopeKind.QuotaScope, "managed"));
        await _world.Quota.AssertUsedAsync(roadmap.Agent.TenantId, 2 * Gb);

        async Task MoveAsync(Guid connectionId, string label, long expectedGb)
        {
            var result = await _world.Service().UpdateAsync(StorageScope.Platform, connectionId, new UpdateStorageConnectionCommand(null, label, null), (await _world.PlatformActorAsync()).UserId, default);
            Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
            await _world.Quota.AssertUsedAsync(roadmap.Agent.TenantId, expectedGb * Gb);
            var oracle = await ConnectionWorld.OracleAsync(fixture.ConnectionString, roadmap.Agent.TenantId, quotaScope: "managed");
            Assert.Equal(expectedGb * Gb, oracle.Physical);
            await _world.Quota.AssertNoDriftAsync(roadmap.Agent.TenantId);
        }

        await MoveAsync(roadmap.Central, "archive", 0);
        await MoveAsync(roadmap.Sftp, "managed", 2);
        await MoveAsync(roadmap.GoogleDrive, "managed", 4);
        await MoveAsync(roadmap.Central, "managed", 6);
        // The physical footprint never moved: only what the policy counts did.
        await _world.Quota.AssertOracleAsync(roadmap.Agent.TenantId, 2 * Gb, 6 * Gb);
    }

    [Fact]
    public async Task ARunTargetingASecondConnectionPublishesItsReplicaThereAndOnlyAPolicyThatSelectsThatScopeCountsIt()
    {
        var (agent, _) = await _world.Quota.TenantOnPolicyAsync(QuotaAccountingMode.LogicalArtifactBytes, RuleOf(QuotaScopeKind.QuotaScope, "premium"));
        await _world.DefaultsAsync(agent.TenantId);
        var premium = await _world.AddAsync(agent.TenantId, "Premium target", "central", StorageOwnership.PlatformManaged, "premium");
        var defaultJob = await _world.Quota.JobAsync(agent);
        var premiumConfiguration = BackupStoreHarness.Configuration() with { StorageProvider = "central", StorageExecutionLocation = BackupStorageExecutionLocation.Backend, StorageConnectionId = premium.Id };
        var premiumJob = (await _world.Quota.RunStore().CreateJobAsync(new BackupJob(Guid.NewGuid(), agent.TenantId, "Premium", agent.AgentId, premiumConfiguration, QuotaWorld.T0, null, BackupOverlapPolicy.Queue), default))!;

        var (defaultRun, defaultArtifact) = await _world.Quota.PublishCopiesAsync(agent, defaultJob, 3 * Gb, ReplicaSpec.Central);
        var (premiumRun, premiumArtifact) = await _world.Quota.PublishCopiesAsync(agent, premiumJob, Gb, ReplicaSpec.Central);

        var (_, defaultCentral) = await _world.DefaultsAsync(agent.TenantId);
        Assert.Equal(defaultCentral.Id, Assert.Single(await _world.ReplicasAsync(defaultArtifact)).StorageConnectionId);
        Assert.Equal(premium.Id, Assert.Single(await _world.ReplicasAsync(premiumArtifact)).StorageConnectionId);
        Assert.Equal(defaultCentral.Id, defaultRun.Configuration.StorageConnectionId);
        Assert.Equal(premium.Id, premiumRun.Configuration.StorageConnectionId);
        await _world.Quota.AssertUsedAsync(agent.TenantId, Gb);
        await _world.Quota.AssertOracleAsync(agent.TenantId, 4 * Gb, 4 * Gb);
        Assert.Equal((Gb, Gb), await ConnectionWorld.OracleAsync(fixture.ConnectionString, agent.TenantId, quotaScope: "premium"));

        await CountByAsync(agent, QuotaAccountingMode.LogicalArtifactBytes, RuleOf(QuotaScopeKind.StorageConnection, premium.Id.ToString("D")));
        await _world.Quota.AssertUsedAsync(agent.TenantId, Gb);
        await CountByAsync(agent, QuotaAccountingMode.LogicalArtifactBytes, RuleOf(QuotaScopeKind.StorageConnection, defaultCentral.Id.ToString("D")));
        await _world.Quota.AssertUsedAsync(agent.TenantId, 3 * Gb);
        await CountByAsync(agent, QuotaAccountingMode.LogicalArtifactBytes, RuleOf(QuotaScopeKind.ProviderType, "central"));
        await _world.Quota.AssertUsedAsync(agent.TenantId, 4 * Gb);
    }

    [Fact]
    public async Task AReplicaOfAProviderWithNoConnectionGetsADisabledTenantManagedOneOfThatTypeCountedAsTenantScopeStorage()
    {
        var roadmap = await RoadmapAsync();

        var rows = await _world.RowsAsync(roadmap.Agent.TenantId);

        foreach (var provider in new[] { "googledrive", "sftp" })
        {
            var row = rows.Single(connection => connection.ProviderType == provider);
            Assert.Equal((StorageOwnership.TenantManaged, "tenant", false, true), (row.Ownership, row.QuotaScope, row.Enabled, row.IsDefault));
        }
        Assert.True(rows.Single(connection => connection.ProviderType == "central").Enabled);
        var replicas = await _world.ReplicasAsync(roadmap.ArtifactId);
        Assert.Equal(new[] { roadmap.Central, roadmap.GoogleDrive, roadmap.Sftp }.Order(), replicas.Select(replica => replica.StorageConnectionId).Order());
        Assert.All(replicas, replica => Assert.Equal(replica.StorageProvider, rows.Single(row => row.Id == replica.StorageConnectionId).ProviderType));
    }
}
