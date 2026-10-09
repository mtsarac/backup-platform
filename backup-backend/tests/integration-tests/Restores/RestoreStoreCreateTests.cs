using BackupPlatform.Application.Restores;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Restores;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.RestoreWorld;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// Creating a restore through the real store: what is persisted (operation, first attempt, replica pin), what is refused and why, tenant isolation and
/// idempotency, including the same key arriving from several nodes at once.
/// </summary>
public sealed class RestoreStoreCreateTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly RestoreWorld _world = new(fixture);

    [Fact]
    public async Task CreatingARestorePersistsTheOperationItsFirstAttemptAndTheReplicaPinTogether()
    {
        var seed = await _world.SeedCentralAsync();
        var user = Guid.NewGuid();
        var options = new RestoreOptions(TerminateActiveConnections: true, VerifyChecksum: false, VerifyBackup: true);
        var command = Command(seed.TenantId, seed.ArtifactId, seed.AgentId, "key-1", databaseName: "Sales_Restored", options: options, requestedBy: user);

        var result = await _world.Store().CreateAsync(command, default);

        Assert.Equal(RestoreCreateOutcome.Created, result.Outcome);
        var stored = await _world.OperationAsync(result.Operation!.Id);
        Assert.Equal(RestoreStatus.Pending, stored.Status);
        Assert.Equal(seed.TenantId, stored.TenantId);
        Assert.Equal(seed.ArtifactId, stored.ArtifactId);
        Assert.Equal(seed.ReplicaId, stored.ReplicaId);
        Assert.Equal(seed.AgentId, stored.AgentId);
        Assert.Equal(RestoreSourceAccess.CentralStorage, stored.SourceAccess);
        Assert.Equal("Sales_Restored", stored.DatabaseName);
        Assert.Equal(RestoreMode.NewDatabase, stored.Mode);
        Assert.Equal(RestoreOverwritePolicy.Forbidden, stored.OverwritePolicy);
        Assert.Equal(new RestoreTarget("sql-target", 1433, true, null, true, false), stored.Target);
        Assert.Equal(options, stored.Options);
        Assert.Equal("key-1", stored.IdempotencyKey);
        Assert.Equal(Fingerprint, stored.RequestFingerprint);
        Assert.Equal(user, stored.RequestedByUserId);
        Assert.Equal(Now, stored.CreatedAt);
        Assert.Null(stored.CompletedAt);
        Assert.Null(stored.TargetState);
        var attempt = Assert.Single(stored.Attempts);
        Assert.Equal(1, attempt.Number);
        Assert.Equal(RestoreStatus.Pending, attempt.Status);
        Assert.True(stored.Pin.IsActive);
        Assert.Equal(seed.ReplicaId, stored.Pin.ReplicaId);
        Assert.Equal(1, await _world.ActivePinsAsync(seed.ReplicaId));
    }

    [Fact]
    public async Task AReplaceRestoreStoresTheConfirmedOverwritePolicy()
    {
        var seed = await _world.SeedCentralAsync();

        var operation = await _world.CreateAsync(seed, mode: RestoreMode.ReplaceExistingDatabase, databaseName: "Existing");

        var stored = await _world.OperationAsync(operation.Id);
        Assert.Equal(RestoreMode.ReplaceExistingDatabase, stored.Mode);
        Assert.Equal(RestoreOverwritePolicy.ReplaceExisting, stored.OverwritePolicy);
    }

    [Fact]
    public async Task AnAgentLocalReplicaOfTheTargetAgentIsReadFromItsOwnDisk()
    {
        var seed = await _world.SeedAgentLocalAsync();

        var operation = await _world.CreateAsync(seed);

        Assert.Equal(RestoreSourceAccess.AgentLocal, operation.SourceAccess);
        Assert.Equal(seed.ReplicaId, operation.ReplicaId);
    }

    [Fact]
    public async Task AnotherTenantsArtifactAndAgentAreNotFoundAndNothingIsCreatedOrLeaked()
    {
        var mine = await _world.SeedCentralAsync();
        var theirs = await _world.SeedCentralAsync();
        var store = _world.Store();

        var foreignArtifact = await store.CreateAsync(Command(theirs.TenantId, mine.ArtifactId, theirs.AgentId), default);
        var foreignAgent = await store.CreateAsync(Command(mine.TenantId, mine.ArtifactId, theirs.AgentId), default);
        var unknownArtifact = await store.CreateAsync(Command(mine.TenantId, Guid.NewGuid(), mine.AgentId), default);

        Assert.Equal(RestoreCreateOutcome.ArtifactNotFound, foreignArtifact.Outcome);
        Assert.Equal(RestoreCreateOutcome.AgentNotFound, foreignAgent.Outcome);
        Assert.Equal(RestoreCreateOutcome.ArtifactNotFound, unknownArtifact.Outcome);
        Assert.Equal(0, await _world.CountOperationsAsync(mine.ArtifactId));
        Assert.Equal(0, await _world.ActivePinsAsync(mine.ReplicaId));
    }

    [Fact]
    public async Task AnotherTenantsRestoreIsNeverReadListedOrCancelled()
    {
        var mine = await _world.SeedCentralAsync();
        var theirs = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(mine, store);

        Assert.Null(await store.GetAsync(theirs.TenantId, operation.Id, default));
        Assert.Null(await store.RequestCancellationAsync(theirs.TenantId, operation.Id, Now, default));
        var theirPage = await store.ListAsync(theirs.TenantId, new RestoreQuery(), default);
        Assert.Empty(theirPage.Items);
        Assert.Equal(0, theirPage.TotalCount);
        Assert.Equal(RestoreStatus.Pending, (await _world.OperationAsync(operation.Id)).Status);
        var ownPage = await store.ListAsync(mine.TenantId, new RestoreQuery(), default);
        Assert.Equal(operation.Id, Assert.Single(ownPage.Items).Id);
    }

    [Fact]
    public async Task ReplayingTheKeyWithTheSameRequestReturnsTheOriginalRestoreAndCreatesNothing()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var first = await store.CreateAsync(Command(seed, "same-key"), default);

        var replay = await _world.Store().CreateAsync(Command(seed, "same-key", now: Now.AddMinutes(5)), default);

        Assert.Equal(RestoreCreateOutcome.Created, first.Outcome);
        Assert.Equal(RestoreCreateOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.Operation!.Id, replay.Operation!.Id);
        Assert.Equal(Now, replay.Operation.CreatedAt);
        Assert.Equal(1, await _world.CountOperationsAsync(seed.ArtifactId));
        Assert.Equal(1, await _world.ActivePinsAsync(seed.ReplicaId));
    }

    [Fact]
    public async Task ReplayingTheKeyWithADifferentRequestIsAConflictAndCreatesNothing()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        await store.CreateAsync(Command(seed, "same-key"), default);

        var conflict = await store.CreateAsync(Command(seed, "same-key", fingerprint: new string('0', 64)), default);

        Assert.Equal(RestoreCreateOutcome.IdempotencyConflict, conflict.Outcome);
        Assert.Null(conflict.Operation);
        Assert.Equal(1, await _world.CountOperationsAsync(seed.ArtifactId));
    }

    [Fact]
    public async Task TheKeyIsScopedToTheArtifactAndTheTenant()
    {
        var seed = await _world.SeedCentralAsync();
        var other = await _world.SeedAnotherAsync(seed);
        var store = _world.Store();

        var first = await store.CreateAsync(Command(seed, "shared-key"), default);
        var otherArtifact = await store.CreateAsync(Command(seed.TenantId, other.ArtifactId, seed.AgentId, "shared-key"), default);
        var otherTenant = await _world.SeedCentralAsync();
        var otherTenantResult = await store.CreateAsync(Command(otherTenant, "shared-key"), default);

        Assert.Equal(RestoreCreateOutcome.Created, first.Outcome);
        Assert.Equal(RestoreCreateOutcome.Created, otherArtifact.Outcome);
        Assert.Equal(RestoreCreateOutcome.Created, otherTenantResult.Outcome);
        Assert.Equal(3, new[] { first.Operation!.Id, otherArtifact.Operation!.Id, otherTenantResult.Operation!.Id }.Distinct().Count());
    }

    [Fact]
    public async Task AReplayStillAnswersAfterTheAgentLostItsCapability()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var created = await store.CreateAsync(Command(seed, "same-key"), default);
        await _world.ExecuteAsync("DELETE FROM agent_capabilities WHERE \"AgentId\" = @agent", ("agent", seed.AgentId.Value));

        var replay = await store.CreateAsync(Command(seed, "same-key"), default);

        Assert.Equal(RestoreCreateOutcome.Replayed, replay.Outcome);
        Assert.Equal(created.Operation!.Id, replay.Operation!.Id);
    }

    [Fact]
    public async Task TheSameKeyFromManyNodesAtOnceCreatesExactlyOneRestore()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var seed = await _world.SeedCentralAsync();
            var key = $"race-{iteration}";
            var gate = new RaceGate();
            var racers = Enumerable.Range(0, 5).Select(_ =>
            {
                var store = _world.Store();
                return gate.Run(() => store.CreateAsync(Command(seed, key), default));
            }).ToArray();
            gate.Open();

            var results = await Task.WhenAll(racers);

            Assert.Equal(1, results.Count(result => result.Outcome == RestoreCreateOutcome.Created));
            Assert.Equal(4, results.Count(result => result.Outcome == RestoreCreateOutcome.Replayed));
            Assert.Single(results.Select(result => result.Operation!.Id).Distinct());
            Assert.Equal(1, await _world.CountOperationsAsync(seed.ArtifactId));
            Assert.Equal(1, await _world.ActivePinsAsync(seed.ReplicaId));
        }
    }

    [Fact]
    public async Task TheSameKeyWithDifferentAgentsRacingStillLeavesExactlyOneRestore()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var seed = await _world.SeedCentralAsync();
            var second = await _world.AddAgentAsync(seed.TenantId);
            var key = $"agent-race-{iteration}";
            var gate = new RaceGate();
            var first = gate.Run(() => _world.Store().CreateAsync(Command(seed, key, fingerprint: new string('1', 64)), default));
            var other = gate.Run(() => _world.Store().CreateAsync(Command(seed.TenantId, seed.ArtifactId, second.AgentId, key, fingerprint: new string('2', 64)), default));
            gate.Open();

            var results = await Task.WhenAll(first, other);

            // Different agents hold different locks, so only the unique key decides: one wins, the other sees the winner's request as a different one.
            Assert.Equal(1, results.Count(result => result.Outcome == RestoreCreateOutcome.Created));
            Assert.Equal(1, results.Count(result => result.Outcome == RestoreCreateOutcome.IdempotencyConflict));
            Assert.Equal(1, await _world.CountOperationsAsync(seed.ArtifactId));
        }
    }

    [Theory]
    [InlineData(CapabilitySupport.Unsupported)]
    [InlineData(CapabilitySupport.Unknown)]
    public async Task AnAgentWithoutTheSupportedRestoreCapabilityCannotRestore(CapabilitySupport support)
    {
        var agent = await _world.Harness.AddAgentAsync();
        await _world.EnableRestoreAsync(agent, support);
        var job = await RetentionKit.AddJobAsync(_world.Harness, agent);
        var seed = new RestoreSeed(agent, await RetentionKit.SeedAsync(_world.Harness, agent, job, Accepted, storageProvider: "central", storageTarget: "central"));

        var result = await _world.Store().CreateAsync(Command(seed), default);

        Assert.Equal(RestoreCreateOutcome.AgentCannotRestore, result.Outcome);
        Assert.Equal(0, await _world.CountOperationsAsync(seed.ArtifactId));
        Assert.Equal(0, await _world.ActivePinsAsync(seed.ReplicaId));
    }

    [Fact]
    public async Task AnAgentThatNeverSyncedCapabilitiesCannotRestore()
    {
        var agent = await _world.Harness.AddAgentAsync();
        var job = await RetentionKit.AddJobAsync(_world.Harness, agent);
        var seed = new RestoreSeed(agent, await RetentionKit.SeedAsync(_world.Harness, agent, job, Accepted, storageProvider: "central", storageTarget: "central"));

        var result = await _world.Store().CreateAsync(Command(seed), default);

        Assert.Equal(RestoreCreateOutcome.AgentCannotRestore, result.Outcome);
    }

    [Fact]
    public async Task ADisabledAgentIsNotActiveAndCannotRestore()
    {
        var seed = await _world.SeedCentralAsync();
        await _world.ExecuteAsync("UPDATE agents SET \"Status\" = 3 WHERE \"Id\" = @agent", ("agent", seed.AgentId.Value));

        var result = await _world.Store().CreateAsync(Command(seed), default);

        Assert.Equal(RestoreCreateOutcome.AgentNotActive, result.Outcome);
        Assert.Equal(0, await _world.CountOperationsAsync(seed.ArtifactId));
    }

    [Fact]
    public async Task ASiblingAgentWithTheCapabilityMayRestoreACentralBackup()
    {
        var seed = await _world.SeedCentralAsync();
        var sibling = await _world.AddAgentAsync(seed.TenantId);

        var result = await _world.Store().CreateAsync(Command(seed.TenantId, seed.ArtifactId, sibling.AgentId), default);

        Assert.Equal(RestoreCreateOutcome.Created, result.Outcome);
        Assert.Equal(sibling.AgentId, result.Operation!.AgentId);
        Assert.Equal(RestoreSourceAccess.CentralStorage, result.Operation.SourceAccess);
    }

    [Fact]
    public async Task AnAgentLocalBackupOfAnotherAgentRequiresATransferAndIsRefusedWithoutPinning()
    {
        var seed = await _world.SeedAgentLocalAsync();
        var sibling = await _world.AddAgentAsync(seed.TenantId);

        var automatic = await _world.Store().CreateAsync(Command(seed.TenantId, seed.ArtifactId, sibling.AgentId), default);
        var explicitReplica = await _world.Store().CreateAsync(Command(seed.TenantId, seed.ArtifactId, sibling.AgentId, replicaId: seed.ReplicaId), default);

        Assert.Equal((RestoreCreateOutcome.SourceRejected, RestoreSourceRejection.AgentTransferRequired), (automatic.Outcome, automatic.Rejection!.Value));
        Assert.Equal((RestoreCreateOutcome.SourceRejected, RestoreSourceRejection.AgentTransferRequired), (explicitReplica.Outcome, explicitReplica.Rejection!.Value));
        Assert.Equal(0, await _world.ActivePinsAsync(seed.ReplicaId));
    }

    [Fact]
    public async Task ABackendReplicaOfALocalProviderIsNotReachableByAnAgent()
    {
        var agent = await _world.Harness.AddAgentAsync();
        await _world.EnableRestoreAsync(agent);
        var job = await RetentionKit.AddJobAsync(_world.Harness, agent);
        var backup = await RetentionKit.SeedAsync(_world.Harness, agent, job, Accepted);
        var seed = new RestoreSeed(agent, backup);

        var result = await _world.Store().CreateAsync(Command(seed), default);

        Assert.Equal((RestoreCreateOutcome.SourceRejected, RestoreSourceRejection.NoAccessibleReplica), (result.Outcome, result.Rejection!.Value));
    }

    [Fact]
    public async Task AReplicaThatIsNotVerifiedIsRefused()
    {
        var seed = await _world.SeedCentralAsync();
        await _world.ExecuteAsync("UPDATE backup_replicas SET \"VerificationState\" = 'Unverified', \"VerifiedAt\" = NULL WHERE \"Id\" = @replica", ("replica", seed.ReplicaId));

        var automatic = await _world.Store().CreateAsync(Command(seed), default);
        var named = await _world.Store().CreateAsync(Command(seed, replicaId: seed.ReplicaId), default);

        Assert.Equal(RestoreSourceRejection.ReplicaNotVerified, automatic.Rejection);
        Assert.Equal(RestoreSourceRejection.ReplicaNotVerified, named.Rejection);
        Assert.Equal(0, await _world.ActivePinsAsync(seed.ReplicaId));
    }

    [Fact]
    public async Task ASoftDeletedReplicaIsRestorableInsideItsGracePeriodButNotFromTheMomentItIsDue()
    {
        var seed = await _world.SeedCentralAsync();
        var due = await _world.SoftDeleteAsync(seed, Now);

        var inside = await _world.Store().CreateAsync(Command(seed, now: due - TimeSpan.FromSeconds(1)), default);
        var atDue = await _world.Store().CreateAsync(Command(seed, now: due), default);

        Assert.Equal(RestoreCreateOutcome.Created, inside.Outcome);
        Assert.Equal((RestoreCreateOutcome.SourceRejected, RestoreSourceRejection.ReplicaNotRestorable), (atDue.Outcome, atDue.Rejection!.Value));
    }

    [Fact]
    public async Task AReplicaWhosePhysicalDeleteHasBegunOrFinishedIsRefused()
    {
        var seed = await _world.SeedCentralAsync();
        var due = await _world.SoftDeleteAsync(seed, Now);
        var claims = await _world.Retention.Store().ClaimDueDeletionsAsync("node-a", due, RetentionWorld.Lease, 500, default);
        var claim = RetentionWorld.Mine(claims, seed.Backup).Single();

        var deleting = await _world.Store().CreateAsync(Command(seed, now: due), default);
        Assert.True(await _world.Retention.Store().CompleteDeletionAsync(claim.ReplicaId, claim.LeaseToken, due, default));
        var deleted = await _world.Store().CreateAsync(Command(seed, now: due, key: "other"), default);

        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, deleting.Rejection);
        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, deleted.Rejection);
        Assert.Equal(0, await _world.ActivePinsAsync(seed.ReplicaId));
    }

    [Fact]
    public async Task AReplicaOfAnotherArtifactIsNotFoundEvenInTheSameTenant()
    {
        var seed = await _world.SeedCentralAsync();
        var other = await _world.SeedAnotherAsync(seed);

        var result = await _world.Store().CreateAsync(Command(seed, replicaId: other.ReplicaId), default);

        Assert.Equal((RestoreCreateOutcome.SourceRejected, RestoreSourceRejection.ReplicaNotFound), (result.Outcome, result.Rejection!.Value));
        Assert.Equal(0, await _world.ActivePinsAsync(other.ReplicaId));
    }

    [Fact]
    public async Task AReplicaOfAnotherTenantIsNotFound()
    {
        var seed = await _world.SeedCentralAsync();
        var foreign = await _world.SeedCentralAsync();

        var result = await _world.Store().CreateAsync(Command(seed, replicaId: foreign.ReplicaId), default);

        Assert.Equal(RestoreSourceRejection.ReplicaNotFound, result.Rejection);
    }

    [Fact]
    public async Task EveryRestoreOfTheSameReplicaPinsItAndTheReplicaStaysPinnedUntilTheLastEnds()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var first = await _world.CreateAsync(seed, store);
        var second = await _world.CreateAsync(seed, store);
        Assert.Equal(2, await _world.ActivePinsAsync(seed.ReplicaId));

        await using (var context = _world.Database.CreatePlatformContext())
        {
            Assert.Equal(2, await context.RestoreReplicaPins.CountAsync(pin => pin.ReplicaId == seed.ReplicaId));
        }
        await store.RequestCancellationAsync(seed.TenantId, first.Id, Now, default);

        Assert.Equal(1, await _world.ActivePinsAsync(seed.ReplicaId));
        await store.RequestCancellationAsync(seed.TenantId, second.Id, Now, default);
        Assert.Equal(0, await _world.ActivePinsAsync(seed.ReplicaId));
    }

    [Fact]
    public async Task ListFiltersByStatusArtifactAndAgentAndPagesNewestFirst()
    {
        var seed = await _world.SeedCentralAsync();
        var other = await _world.SeedAnotherAsync(seed);
        var sibling = await _world.AddAgentAsync(seed.TenantId);
        var store = _world.Store();
        var oldest = await _world.CreateAsync(seed, store, Now);
        var middle = (await store.CreateAsync(Command(seed.TenantId, other.ArtifactId, seed.AgentId, now: Now.AddMinutes(1)), default)).Operation!;
        var newest = (await store.CreateAsync(Command(seed.TenantId, seed.ArtifactId, sibling.AgentId, now: Now.AddMinutes(2)), default)).Operation!;
        await store.RequestCancellationAsync(seed.TenantId, middle.Id, Now.AddMinutes(3), default);

        var all = await store.ListAsync(seed.TenantId, new RestoreQuery(), default);
        var pending = await store.ListAsync(seed.TenantId, new RestoreQuery(Status: RestoreStatus.Pending), default);
        var cancelled = await store.ListAsync(seed.TenantId, new RestoreQuery(Status: RestoreStatus.Cancelled), default);
        var byArtifact = await store.ListAsync(seed.TenantId, new RestoreQuery(ArtifactId: seed.ArtifactId), default);
        var byAgent = await store.ListAsync(seed.TenantId, new RestoreQuery(AgentId: sibling.AgentId.Value), default);
        var secondPage = await store.ListAsync(seed.TenantId, new RestoreQuery(Page: 2, PageSize: 2), default);
        var beyond = await store.ListAsync(seed.TenantId, new RestoreQuery(Page: 5, PageSize: 2), default);

        Assert.Equal([newest.Id, middle.Id, oldest.Id], all.Items.Select(item => item.Id));
        Assert.Equal(3, all.TotalCount);
        Assert.Equal([newest.Id, oldest.Id], pending.Items.Select(item => item.Id));
        Assert.Equal([middle.Id], cancelled.Items.Select(item => item.Id));
        Assert.Equal([newest.Id, oldest.Id], byArtifact.Items.Select(item => item.Id));
        Assert.Equal([newest.Id], byAgent.Items.Select(item => item.Id));
        Assert.Equal([oldest.Id], secondPage.Items.Select(item => item.Id));
        Assert.Equal(3, secondPage.TotalCount);
        Assert.Empty(beyond.Items);
        Assert.Equal(3, beyond.TotalCount);
        Assert.All(all.Items, item => Assert.Single(item.Attempts));
    }
}
