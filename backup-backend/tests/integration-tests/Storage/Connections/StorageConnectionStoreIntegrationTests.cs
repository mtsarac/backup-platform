using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.Storage;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.Quota.UsageTestKit;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

/// <summary>
/// The connection store on real PostgreSQL: default provisioning, tenant isolation, uniqueness, limits, reference-checked deletion, and the audit
/// event and usage recomputation that travel in the same transaction as the change.
/// </summary>
public sealed class StorageConnectionStoreIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset Due = QuotaWorld.T0.AddDays(2);
    private readonly ConnectionWorld _world = new(fixture);

    // ---- default provisioning --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFirstListMaterializesExactlyTheTwoSystemDefaultsAndLaterListsChangeNothing()
    {
        var tenantId = await _world.TenantAsync();

        var first = await _world.Store().ListAsync(tenantId, default);
        var second = await _world.Store().ListAsync(tenantId, default);

        Assert.NotNull(first);
        Assert.Equal(["Local", "Managed Storage"], first.Select(record => record.Connection.Name).Order().ToArray());
        var local = first.Single(record => record.Connection.ProviderType == "local").Connection;
        var central = first.Single(record => record.Connection.ProviderType == "central").Connection;
        Assert.Equal((StorageOwnership.TenantManaged, "tenant", true, true), (local.Ownership, local.QuotaScope, local.IsDefault, local.Enabled));
        Assert.Equal((StorageOwnership.PlatformManaged, "managed", true, true), (central.Ownership, central.QuotaScope, central.IsDefault, central.Enabled));
        Assert.Equal(first.Select(record => record.Connection.Id).Order(), second!.Select(record => record.Connection.Id).Order());
        Assert.Equal(2, (await _world.RowsAsync(tenantId)).Count);
    }

    [Fact]
    public async Task AnUnknownTenantHasNoConnectionsAndGetsNoRows()
    {
        var unknown = TenantId.New();

        Assert.Null(await _world.Store().ListAsync(unknown, default));
        Assert.Empty(await _world.RowsAsync(unknown));
    }

    [Fact]
    public async Task EightCallersRacingToProvisionTheSameTenantCreateEachDefaultExactlyOnce()
    {
        var tenantId = await _world.TenantAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _world.Store().ListAsync(tenantId, default))));

        var rows = await _world.RowsAsync(tenantId);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, row => row.ProviderType == "local");
        Assert.Single(rows, row => row.ProviderType == "central");
        // Every racer, including the ones that lost, reads the very same two connections.
        Assert.All(results, list => Assert.Equal(rows.Select(row => row.Id).Order(), list!.Select(record => record.Connection.Id).Order()));
    }

    [Fact]
    public async Task AManuallyNamedLocalConnectionOfAnotherProviderDoesNotBreakTheLocalDefaultItGetsASystemSuffix()
    {
        var tenantId = await _world.TenantAsync();
        var mine = await _world.AddAsync(tenantId, "local", "sftp");

        var list = await _world.Store().ListAsync(tenantId, default);

        Assert.Equal(3, list!.Count);
        var local = list.Single(record => record.Connection.ProviderType == "local").Connection;
        Assert.Equal(("Local (system)", true), (local.Name, local.IsDefault));
        Assert.Equal("local", (await _world.RowAsync(mine.Id)).Name);
        Assert.False((await _world.RowAsync(mine.Id)).IsDefault);
        Assert.Equal("Managed Storage", list.Single(record => record.Connection.ProviderType == "central").Connection.Name);
    }

    [Fact]
    public async Task ATenantThatMadeItsOwnConnectionOfADefaultProviderTypeKeepsItAndGetsNoSecondDefault()
    {
        var tenantId = await _world.TenantAsync();
        var own = await _world.AddAsync(tenantId, "My managed target", "central", StorageOwnership.PlatformManaged, "premium");

        var list = await _world.Store().ListAsync(tenantId, default);
        Assert.NotNull(list);
        var centrals = list.Where(record => record.Connection.ProviderType == "central").ToList();
        Assert.Equal(own.Id, Assert.Single(centrals).Connection.Id);
        Assert.Equal(2, list.Count);
    }

    // ---- tenant scope ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnotherTenantsConnectionIsNotFoundMutatedOrDeletedThroughATenantScope()
    {
        var owner = await _world.TenantAsync();
        var stranger = await _world.TenantAsync();
        var (local, _) = await _world.DefaultsAsync(owner);
        var mutationRan = false;

        var found = await _world.Store().FindAsync(local.Id, stranger, default);
        var mutated = await _world.Store().MutateAsync(local.Id, stranger, (connection, now) =>
        {
            mutationRan = true;
            return connection.Rename("Hijacked", now) ? new StorageMutationResult(true, []) : StorageMutationResult.NoChange;
        }, await _world.PlatformActorAsync(), ConnectionWorld.Now, default);
        var deleted = await _world.Store().DeleteAsync(local.Id, stranger, await _world.PlatformActorAsync(), ConnectionWorld.Now, default);

        Assert.Null(found);
        Assert.Equal(StorageConnectionOutcome.NotFound, mutated.Outcome);
        Assert.False(mutationRan);
        Assert.Equal(StorageConnectionOutcome.NotFound, deleted.Outcome);
        Assert.Equal("Local", (await _world.RowAsync(local.Id)).Name);
        Assert.Empty(await _world.AuditOfTenantAsync(owner));
        // The owner's own scope and the platform (no scope) both see it.
        Assert.NotNull(await _world.Store().FindAsync(local.Id, owner, default));
        Assert.NotNull(await _world.Store().FindAsync(local.Id, null, default));
    }

    // ---- uniqueness and limits -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConnectionNamesAreUniquePerTenantIgnoringCaseButNotAcrossTenants()
    {
        var tenantId = await _world.TenantAsync();
        var other = await _world.TenantAsync();
        await _world.AddAsync(tenantId, "Archive", "sftp");

        var clash = await _world.Store().CreateAsync(ConnectionWorld.NewConnection(tenantId, "  ARCHIVE ", "sftp"), null, await _world.PlatformActorAsync(), new Dictionary<string, object?>(), ConnectionWorld.Now, default);
        var elsewhere = await _world.AddAsync(other, "archive", "sftp");

        Assert.Equal(StorageConnectionOutcome.NameTaken, clash.Outcome);
        Assert.Equal("archive", elsewhere.Name);
        Assert.Single(await _world.RowsAsync(tenantId));
        Assert.Single(await _world.AuditOfTenantAsync(tenantId));
    }

    [Fact]
    public async Task RenamingToAnotherConnectionsNameIgnoringCaseIsRefusedAndLeavesNoAuditEvent()
    {
        var tenantId = await _world.TenantAsync();
        await _world.AddAsync(tenantId, "Archive", "sftp");
        var second = await _world.AddAsync(tenantId, "Second", "googledrive");

        var result = await _world.Store().MutateAsync(second.Id, tenantId, (connection, now) =>
            connection.Rename("aRCHIVE", now) ? new StorageMutationResult(true, [new StorageAuditEntry(StorageConnectionAuditActions.Updated, new Dictionary<string, object?>())]) : StorageMutationResult.NoChange,
            await _world.PlatformActorAsync(), ConnectionWorld.Now, default);

        Assert.Equal(StorageConnectionOutcome.NameTaken, result.Outcome);
        Assert.Equal("Second", (await _world.RowAsync(second.Id)).Name);
        Assert.DoesNotContain(await _world.AuditOfTenantAsync(tenantId), audit => audit.Action == StorageConnectionAuditActions.Updated);
    }

    [Fact]
    public async Task ThePerProviderLimitCountsTheTenantsConnectionsOfThatProviderOnly()
    {
        var tenantId = await _world.TenantAsync();
        var other = await _world.TenantAsync();
        await _world.AddAsync(tenantId, "First sftp", "sftp");
        var actor = await _world.PlatformActorAsync();

        var overLimit = await _world.Store().CreateAsync(ConnectionWorld.NewConnection(tenantId, "Second sftp", "sftp"), 1, actor, new Dictionary<string, object?>(), ConnectionWorld.Now, default);
        var otherProvider = await _world.Store().CreateAsync(ConnectionWorld.NewConnection(tenantId, "A drive", "googledrive"), 1, actor, new Dictionary<string, object?>(), ConnectionWorld.Now, default);
        var otherTenant = await _world.Store().CreateAsync(ConnectionWorld.NewConnection(other, "First sftp", "sftp"), 1, actor, new Dictionary<string, object?>(), ConnectionWorld.Now, default);
        var unlimited = await _world.Store().CreateAsync(ConnectionWorld.NewConnection(tenantId, "Third sftp", "sftp"), null, actor, new Dictionary<string, object?>(), ConnectionWorld.Now, default);

        Assert.Equal(StorageConnectionOutcome.LimitReached, overLimit.Outcome);
        Assert.Equal(StorageConnectionOutcome.Changed, otherProvider.Outcome);
        Assert.Equal(StorageConnectionOutcome.Changed, otherTenant.Outcome);
        Assert.Equal(StorageConnectionOutcome.Changed, unlimited.Outcome);
        Assert.DoesNotContain(await _world.RowsAsync(tenantId), row => row.Name == "Second sftp");
    }

    [Fact]
    public async Task CreatingForAnUnknownTenantIsTenantNotFound()
    {
        var result = await _world.Store().CreateAsync(ConnectionWorld.NewConnection(TenantId.New(), "Orphan", "sftp"), null, await _world.PlatformActorAsync(), new Dictionary<string, object?>(), ConnectionWorld.Now, default);

        Assert.Equal(StorageConnectionOutcome.TenantNotFound, result.Outcome);
    }

    // ---- deletion --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeleteIsRefusedWhileAReplicaStillPointsAtTheConnectionEvenAfterTheReplicaWasDeleted()
    {
        var agent = await _world.Quota.AgentAsync();
        var job = await _world.Quota.JobAsync(agent);
        var (_, artifactId) = await _world.Quota.PublishCopiesAsync(agent, job, 1000, ReplicaSpec.Central);
        var (_, central) = await _world.DefaultsAsync(agent.TenantId);

        var whileAvailable = await _world.Store().DeleteAsync(central.Id, agent.TenantId, await _world.PlatformActorAsync(), ConnectionWorld.Now, default);
        var token = Guid.NewGuid();
        await _world.Quota.ChangeArtifactAsync(artifactId, artifact => artifact.RequestDelete(ConnectionWorld.Now, null));
        await _world.Quota.ChangeArtifactAsync(artifactId, artifact => Assert.True(artifact.Replicas.Single().BeginDelete(token, "node-a", Due, TimeSpan.FromMinutes(5))));
        await _world.Quota.ChangeArtifactAsync(artifactId, artifact => Assert.True(artifact.Replicas.Single().CompleteDelete(token, Due)));
        var afterDeleted = await _world.Store().DeleteAsync(central.Id, agent.TenantId, await _world.PlatformActorAsync(), ConnectionWorld.Now, default);

        Assert.Equal(StorageConnectionOutcome.InUse, whileAvailable.Outcome);
        Assert.Equal(StorageConnectionOutcome.InUse, afterDeleted.Outcome);
        Assert.NotNull(await _world.Store().FindAsync(central.Id, agent.TenantId, default));
        Assert.DoesNotContain(await _world.AuditOfTenantAsync(agent.TenantId), audit => audit.Action == StorageConnectionAuditActions.Deleted);
    }

    [Fact]
    public async Task ADeleteIsRefusedWhileAJobConfigurationNamesTheConnection()
    {
        var agent = await _world.Quota.AgentAsync();
        var target = await _world.AddAsync(agent.TenantId, "Second central", "central", StorageOwnership.PlatformManaged);
        await CreateJobTargetingAsync(agent, target.Id);

        var result = await _world.Store().DeleteAsync(target.Id, agent.TenantId, await _world.PlatformActorAsync(), ConnectionWorld.Now, default);

        Assert.Equal(StorageConnectionOutcome.InUse, result.Outcome);
        Assert.Equal(target.Id, (await _world.RowAsync(target.Id)).Id);
    }

    [Fact]
    public async Task ADeleteIsRefusedWhileARunSnapshotNamesTheConnectionEvenAfterTheJobMovedAway()
    {
        var agent = await _world.Quota.AgentAsync();
        var target = await _world.AddAsync(agent.TenantId, "Second central", "central", StorageOwnership.PlatformManaged);
        var job = await CreateJobTargetingAsync(agent, target.Id);
        var started = await _world.Quota.RunStore().StartRunAsync(agent.TenantId, job.Id, "run-1", ConnectionWorld.Now, default);
        Assert.Equal(BackupStartOutcome.Started, started.Outcome);
        Assert.Equal(target.Id, started.Run!.Configuration.StorageConnectionId);
        // The job no longer names the connection; the run created earlier still does.
        var moved = await _world.Quota.RunStore().UpdateJobAsync(agent.TenantId, job.Id, new BackupJobUpdate(job.Name, job.AgentId, job.Configuration with { StorageConnectionId = null }, null, null), default);
        Assert.Null(moved!.Configuration.StorageConnectionId);

        var result = await _world.Store().DeleteAsync(target.Id, agent.TenantId, await _world.PlatformActorAsync(), ConnectionWorld.Now, default);

        Assert.Equal(StorageConnectionOutcome.InUse, result.Outcome);
    }

    [Fact]
    public async Task AnUnreferencedConnectionIsDeletedAndTheDeletionIsAudited()
    {
        var tenantId = await _world.TenantAsync();
        var target = await _world.AddAsync(tenantId, "Disposable", "sftp", StorageOwnership.TenantManaged, "scratch");
        var actor = await _world.PlatformActorAsync();

        var result = await _world.Store().DeleteAsync(target.Id, tenantId, actor, ConnectionWorld.Now, default);

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Assert.Null(await _world.Store().FindAsync(target.Id, tenantId, default));
        var audit = Assert.Single(await _world.AuditOfTenantAsync(tenantId), row => row.Action == StorageConnectionAuditActions.Deleted);
        Assert.Equal((tenantId, "storage_connection", target.Id.ToString("D"), actor.UserId, AuditActorKind.PlatformAdmin), (audit.TenantId, audit.TargetType, audit.TargetId, audit.ActorUserId, audit.ActorKind));
        var details = DetailsOf(audit);
        Assert.Equal(("Disposable", "sftp", "TenantManaged", "scratch"), (details.GetProperty("name").GetString(), details.GetProperty("providerType").GetString(),
            details.GetProperty("ownership").GetString(), details.GetProperty("quotaScope").GetString()));
    }

    // ---- audit in the same transaction -----------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateMutateAndDeleteEachWriteOneAuditEventNamingTheTenantTheTargetAndTheActor()
    {
        var tenantId = await _world.TenantAsync();
        var actor = await _world.PlatformActorAsync();
        var connection = ConnectionWorld.NewConnection(tenantId, "Audited", "sftp");

        await _world.Store().CreateAsync(connection, null, actor, new Dictionary<string, object?> { ["name"] = "Audited" }, ConnectionWorld.Now, default);
        await _world.SetEnabledAsync(connection.Id, tenantId, false);
        await _world.Store().DeleteAsync(connection.Id, tenantId, actor, ConnectionWorld.Now, default);

        var events = await _world.AuditOfTenantAsync(tenantId);
        Assert.Equal(3, events.Count);
        Assert.Single(events, audit => audit.Action == StorageConnectionAuditActions.Created);
        Assert.Single(events, audit => audit.Action == StorageConnectionAuditActions.Disabled);
        Assert.Single(events, audit => audit.Action == StorageConnectionAuditActions.Deleted);
        Assert.All(events, audit => Assert.Equal((tenantId, "storage_connection", connection.Id.ToString("D"), actor.UserId), (audit.TenantId, audit.TargetType, audit.TargetId, audit.ActorUserId)));
    }

    [Fact]
    public async Task AMutationThatChangesNothingWritesNoAuditEventAndReportsUnchanged()
    {
        var tenantId = await _world.TenantAsync();
        var target = await _world.AddAsync(tenantId, "Idle", "sftp");

        var enabledAgain = await _world.SetEnabledAsync(target.Id, tenantId, true);

        Assert.Equal(StorageConnectionOutcome.Unchanged, enabledAgain.Outcome);
        Assert.Equal([StorageConnectionAuditActions.Created], (await _world.AuditOfTenantAsync(tenantId)).Select(audit => audit.Action).ToArray());
    }

    [Fact]
    public async Task AnAuditEventTheDatabaseRefusesRollsBackTheChangeItDescribes()
    {
        var tenantId = await _world.TenantAsync();
        var target = await _world.AddAsync(tenantId, "Before", "sftp");
        var tooLongAction = new string('x', 65); // audit_events.Action is varchar(64): rejected by the database at save time

        await Assert.ThrowsAnyAsync<DbUpdateException>(async () => await _world.Store().MutateAsync(target.Id, tenantId, (connection, now) =>
            connection.Rename("After", now) ? new StorageMutationResult(true, [new StorageAuditEntry(tooLongAction, new Dictionary<string, object?>())]) : StorageMutationResult.NoChange,
            await _world.PlatformActorAsync(), ConnectionWorld.Now, default));

        Assert.Equal("Before", (await _world.RowAsync(target.Id)).Name);
        Assert.DoesNotContain(await _world.AuditOfTenantAsync(tenantId, actionPrefix: null), audit => audit.Action == tooLongAction);
    }

    [Fact]
    public async Task ACreateWhoseAuditEventIsRejectedLeavesNoConnectionBehind()
    {
        var tenantId = await _world.TenantAsync();
        var connection = ConnectionWorld.NewConnection(tenantId, "Never stored", "sftp");
        var tooLarge = new Dictionary<string, object?> { ["details"] = new string('x', AuditEvent.MaximumDetailsLength) };

        await Assert.ThrowsAsync<ArgumentException>(async () => await _world.Store().CreateAsync(connection, null, await _world.PlatformActorAsync(), tooLarge, ConnectionWorld.Now, default));

        Assert.Empty(await _world.RowsAsync(tenantId));
    }

    // ---- quota scope recomputes usage in the same transaction -------------------------------------------------------------------

    [Fact]
    public async Task ChangingAQuotaScopeRecomputesTheTenantsUsageUnderItsPolicyAtOnceAndChangingItBackRestoresTheFigure()
    {
        var (agent, _) = await _world.Quota.TenantOnPolicyAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeOf("managed"));
        var job = await _world.Quota.JobAsync(agent);
        await _world.Quota.PublishCopiesAsync(agent, job, 2 * QuotaWorld.Gb, ReplicaSpec.Central);
        await _world.Quota.AssertUsedAsync(agent.TenantId, 2 * QuotaWorld.Gb);
        var (_, central) = await _world.DefaultsAsync(agent.TenantId);
        var service = _world.Service();
        var actor = (await _world.PlatformActorAsync()).UserId;

        var moved = await service.UpdateAsync(StorageScope.Platform, central.Id, new UpdateStorageConnectionCommand(null, "archive", null), actor, default);

        Assert.Equal(StorageConnectionOutcome.Changed, moved.Outcome);
        // The projection row itself changed inside the connection change; no read had to notice and repair it.
        Assert.Equal(0, (await _world.Quota.StoredUsageAsync(agent.TenantId))!.UsedBytes);
        await _world.Quota.AssertUsedAsync(agent.TenantId, 0);
        await _world.Quota.AssertOracleAsync(agent.TenantId, 2 * QuotaWorld.Gb, 2 * QuotaWorld.Gb);
        await _world.Quota.AssertNoDriftAsync(agent.TenantId);
        var changed = Assert.Single(await _world.AuditOfTenantAsync(agent.TenantId), audit => audit.Action == StorageConnectionAuditActions.QuotaScopeChanged);
        Assert.Equal(("archive", "managed"), (DetailsOf(changed).GetProperty("quotaScope").GetString(), DetailsOf(changed).GetProperty("previousQuotaScope").GetString()));

        var back = await service.UpdateAsync(StorageScope.Platform, central.Id, new UpdateStorageConnectionCommand(null, "managed", null), actor, default);

        Assert.Equal(StorageConnectionOutcome.Changed, back.Outcome);
        Assert.Equal(2 * QuotaWorld.Gb, (await _world.Quota.StoredUsageAsync(agent.TenantId))!.UsedBytes);
        await _world.Quota.AssertNoDriftAsync(agent.TenantId);
    }

    [Fact]
    public async Task ChangingAQuotaScopeToTheSameLabelRecordsNothingAndLeavesTheProjectionAlone()
    {
        var (agent, _) = await _world.Quota.TenantOnPolicyAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeOf("managed"));
        var job = await _world.Quota.JobAsync(agent);
        await _world.Quota.PublishCopiesAsync(agent, job, QuotaWorld.Gb, ReplicaSpec.Central);
        var before = await _world.Quota.StoredUsageAsync(agent.TenantId);
        var (_, central) = await _world.DefaultsAsync(agent.TenantId);

        var result = await _world.Service().UpdateAsync(StorageScope.Platform, central.Id, new UpdateStorageConnectionCommand(null, "managed", null), (await _world.PlatformActorAsync()).UserId, default);

        Assert.Equal(StorageConnectionOutcome.Unchanged, result.Outcome);
        Assert.Equal(before!.Revision, (await _world.Quota.StoredUsageAsync(agent.TenantId))!.Revision);
        Assert.DoesNotContain(await _world.AuditOfTenantAsync(agent.TenantId), audit => audit.Action == StorageConnectionAuditActions.QuotaScopeChanged);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------------

    private static QuotaScopeRule QuotaScopeOf(params string[] labels) =>
        QuotaScopeRule.Create([new QuotaScopeSelector(QuotaScopeKind.QuotaScope, labels)]);

    private async Task<BackupJob> CreateJobTargetingAsync(StoreAgent agent, Guid connectionId)
    {
        var configuration = BackupStoreHarness.Configuration() with { StorageExecutionLocation = BackupStorageExecutionLocation.Backend, StorageProvider = "central", StorageConnectionId = connectionId };
        var job = new BackupJob(Guid.NewGuid(), agent.TenantId, "Targeted", agent.AgentId, configuration, ConnectionWorld.Now, null, BackupOverlapPolicy.Queue);
        return (await _world.Quota.RunStore().CreateJobAsync(job, default))!;
    }

    private static JsonElement DetailsOf(AuditEvent audit) => JsonDocument.Parse(audit.DetailsJson).RootElement;
}
