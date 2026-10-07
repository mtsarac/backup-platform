using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Infrastructure.Restores;
using Npgsql;
using static BackupPlatform.IntegrationTests.RestoreWorld;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// While a restore reads a replica no delete worker may take the replica for physical removal, whatever the clock says: the backend claim and the agent claim
/// skip a pinned replica, and the database itself refuses the transition. Every way a restore can end releases the pin, after which the replica is deletable.
/// </summary>
public sealed class RestoreRetentionPinTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly TimeSpan Grace = TimeSpan.FromHours(24);
    private readonly RestoreWorld _world = new(fixture);
    private static DateTimeOffset At(int seconds) => Now.AddSeconds(seconds);

    private Task<RestoreSeed> SeedAsync() => _world.SeedCentralAsync(RetentionKit.Policy(grace: Grace));

    /// <summary>Everything due at <paramref name="now"/>, as the retention worker of some node would claim it; the replica of <paramref name="seed"/> if it was handed out.</summary>
    private async Task<ClaimedReplicaDeletion?> ClaimDueAsync(RestoreSeed seed, DateTimeOffset now) =>
        RetentionWorld.Mine(await _world.Retention.Store().ClaimDueDeletionsAsync("node-a", now, RetentionWorld.Lease, 500, default), seed.Backup).SingleOrDefault();

    [Fact]
    public async Task APinnedReplicaIsNeverClaimedByTheBackendDeleteWorkerNoMatterHowLongItsDeleteIsDue()
    {
        var seed = await SeedAsync();
        var due = await _world.SoftDeleteAsync(seed, Now);
        var restore = await _world.CreateAsync(seed, now: due - TimeSpan.FromHours(1));

        Assert.Null(await ClaimDueAsync(seed, due));
        Assert.Null(await ClaimDueAsync(seed, due + TimeSpan.FromDays(400)));
        Assert.Equal(BackupReplicaStatus.DeletePending, (await _world.ReplicaAsync(seed.ReplicaId)).Status);

        await _world.Store().RequestCancellationAsync(seed.TenantId, restore.Id, due + TimeSpan.FromDays(401), default);

        Assert.NotNull(await ClaimDueAsync(seed, due + TimeSpan.FromDays(401)));
        Assert.Equal(BackupReplicaStatus.Deleting, (await _world.ReplicaAsync(seed.ReplicaId)).Status);
    }

    [Fact]
    public async Task APinnedAgentLocalReplicaIsNeverDispatchedForDeletion()
    {
        var seed = await _world.SeedAgentLocalAsync(RetentionKit.Policy(grace: Grace));
        var due = await _world.SoftDeleteAsync(seed, Now);
        var restore = await _world.CreateAsync(seed, now: due - TimeSpan.FromHours(1));

        Assert.Null(await ClaimDueAsync(seed, due + TimeSpan.FromDays(1)));
        Assert.Equal(BackupReplicaStatus.DeletePending, (await _world.ReplicaAsync(seed.ReplicaId)).Status);

        await _world.Store().RequestCancellationAsync(seed.TenantId, restore.Id, due + TimeSpan.FromDays(1), default);
        Assert.Null(await ClaimDueAsync(seed, due + TimeSpan.FromDays(2))); // dispatched to the agent: nothing for the backend to do, but the replica moved
        var replica = await _world.ReplicaAsync(seed.ReplicaId);
        Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
        Assert.True(replica.WaitingForAgent);
    }

    [Fact]
    public async Task RetentionMayStillSoftDeleteAPinnedReplicaButNeverRemoveIt()
    {
        var seed = await SeedAsync();
        var restore = await _world.CreateAsync(seed);
        var expiry = Accepted + TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1);

        var applied = await _world.Retention.Store().ApplyExpiredRetentionAsync(expiry, 1000, default);
        var due = expiry + Grace;

        Assert.True(applied.ReplicasMoved >= 1);
        Assert.Equal(BackupReplicaStatus.DeletePending, (await _world.ReplicaAsync(seed.ReplicaId)).Status);
        Assert.Null(await ClaimDueAsync(seed, due + TimeSpan.FromDays(10)));
        Assert.Equal(1, await _world.ActivePinsAsync(seed.ReplicaId));
        Assert.Equal(RestoreStatus.Pending, (await _world.OperationAsync(restore.Id)).Status);
    }

    [Fact]
    public async Task EveryWayARestoreEndsReleasesThePinAndTheReplicaBecomesDeletable()
    {
        var endings = new (string Name, Func<RestoreSeed, PostgreSqlRestoreStore, Guid, Task> End)[]
        {
            ("succeeded", async (seed, store, _) =>
            {
                var claim = await RunToAsync(store, seed.Agent, At(10), RestoreStages.Finalizing);
                Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(20), Succeeded()));
            }),
            ("failed", async (seed, store, _) =>
            {
                var claim = await RunToAsync(store, seed.Agent, At(10));
                Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(20), Failed()));
            }),
            ("cancelled by the agent", async (seed, store, id) =>
            {
                var claim = await RunToAsync(store, seed.Agent, At(10), RestoreStages.Restoring);
                await store.RequestCancellationAsync(seed.TenantId, id, At(15), default);
                Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(20), Cancelled()));
            }),
            ("cancelled while pending", async (seed, store, id) => Assert.NotNull(await store.RequestCancellationAsync(seed.TenantId, id, At(10), default))),
            ("agent lost", async (seed, store, _) =>
            {
                await RunToAsync(store, seed.Agent, At(10), RestoreStages.Restoring);
                Assert.True(await store.ExpireStaleAsync(At(10) + Lease, default) >= 1);
            }),
            ("never claimed", async (seed, store, _) => Assert.True(await store.ExpireUnclaimedAsync(Now + Unclaimed, Unclaimed, default) >= 1))
        };
        foreach (var (name, end) in endings)
        {
            var seed = await SeedAsync();
            var due = await _world.SoftDeleteAsync(seed, Now);
            var store = _world.Store();
            var restore = await _world.CreateAsync(seed, store, Now);
            Assert.True(await ClaimDueAsync(seed, due + TimeSpan.FromDays(1)) is null, $"{name}: pinned");

            await end(seed, store, restore.Id);

            Assert.True(await _world.ActivePinsAsync(seed.ReplicaId) == 0, $"{name}: pin released");
            Assert.True(await ClaimDueAsync(seed, due + TimeSpan.FromDays(1)) is not null, $"{name}: deletable");
        }
    }

    [Fact]
    public async Task AReplicaStaysPinnedUntilTheLastRestoreReadingItHasEnded()
    {
        var seed = await SeedAsync();
        var due = await _world.SoftDeleteAsync(seed, Now);
        var store = _world.Store();
        var first = await _world.CreateAsync(seed, store, Now);
        var second = await _world.CreateAsync(seed, store, Now.AddSeconds(1));
        await store.RequestCancellationAsync(seed.TenantId, first.Id, At(5), default);

        Assert.Null(await ClaimDueAsync(seed, due + TimeSpan.FromDays(1)));

        await store.RequestCancellationAsync(seed.TenantId, second.Id, At(6), default);
        Assert.NotNull(await ClaimDueAsync(seed, due + TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task ARestoreAndTheBackendDeleteClaimRacingForTheSameReplicaNeverBothWin()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var seed = await SeedAsync();
            var due = await _world.SoftDeleteAsync(seed, Now);
            // Alternate between asking for the replica by id and letting the store choose it: both must lock and re-read it.
            var replicaId = iteration % 2 == 0 ? seed.ReplicaId : (Guid?)null;
            var gate = new RaceGate();
            var creating = gate.Run(() => _world.Store().CreateAsync(Command(seed, replicaId: replicaId, now: due - TimeSpan.FromSeconds(1)), default));
            var claiming = gate.Run(async () => RetentionWorld.Mine(await _world.Retention.Store().ClaimDueDeletionsAsync("node-a", due, RetentionWorld.Lease, 500, default), seed.Backup).Count());
            gate.Open();

            var created = await creating;
            var claimed = await claiming;

            var replica = await _world.ReplicaAsync(seed.ReplicaId);
            var pins = await _world.ActivePinsAsync(seed.ReplicaId);
            Assert.False(replica.Status == BackupReplicaStatus.Deleting && pins > 0, "A pinned replica must never be Deleting.");
            if (created.Outcome == RestoreCreateOutcome.Created)
            {
                Assert.Equal(0, claimed);
                Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status);
                Assert.Equal(1, pins);
            }
            else
            {
                Assert.Equal((RestoreCreateOutcome.SourceRejected, RestoreSourceRejection.ReplicaNotRestorable), (created.Outcome, created.Rejection!.Value));
                Assert.Equal(1, claimed);
                Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
                Assert.Equal(0, pins);
            }
        }
    }

    [Fact]
    public async Task ManyRestoresAndManyDeleteClaimsOnTheSameReplicaStayConsistent()
    {
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var seed = await SeedAsync();
            var due = await _world.SoftDeleteAsync(seed, Now);
            var gate = new RaceGate();
            var creations = Enumerable.Range(0, 4).Select(_ => gate.Run(() => _world.Store().CreateAsync(Command(seed, now: due - TimeSpan.FromSeconds(1)), default))).ToArray();
            var claims = Enumerable.Range(0, 3).Select(index => gate.Run(async () =>
                RetentionWorld.Mine(await _world.Retention.Store().ClaimDueDeletionsAsync($"node-{index}", due, RetentionWorld.Lease, 500, default), seed.Backup).Count())).ToArray();
            gate.Open();

            var created = (await Task.WhenAll(creations)).Count(result => result.Outcome == RestoreCreateOutcome.Created);
            var claimed = (await Task.WhenAll(claims)).Sum();

            var replica = await _world.ReplicaAsync(seed.ReplicaId);
            var pins = await _world.ActivePinsAsync(seed.ReplicaId);
            Assert.True(claimed <= 1, "A replica is claimed at most once.");
            Assert.False(replica.Status == BackupReplicaStatus.Deleting && pins > 0);
            Assert.Equal(created, pins);
            Assert.Equal(created > 0, claimed == 0 && replica.Status == BackupReplicaStatus.DeletePending);
        }
    }

    [Fact]
    public async Task TheDatabaseRefusesToMoveAPinnedReplicaToDeletingOrDeletedWhateverTheApplicationDoes()
    {
        var seed = await SeedAsync();
        await _world.SoftDeleteAsync(seed, Now);
        var restore = await _world.CreateAsync(seed, now: Now.AddHours(1));
        var toDeleting = "UPDATE backup_replicas SET \"Status\" = 'Deleting', \"DeleteLeaseToken\" = gen_random_uuid(), \"DeleteLeaseOwner\" = 'raw', " +
            "\"DeleteLeaseExpiresAt\" = now() + interval '1 hour', \"DeleteAttempts\" = 1 WHERE \"Id\" = @replica";
        var toDeleted = "UPDATE backup_replicas SET \"Status\" = 'Deleted', \"DeletedAt\" = now(), \"DeleteAttempts\" = 1 WHERE \"Id\" = @replica";

        var deleting = await Assert.ThrowsAsync<PostgresException>(() => _world.ExecuteAsync(toDeleting, ("replica", seed.ReplicaId)));
        var deleted = await Assert.ThrowsAsync<PostgresException>(() => _world.ExecuteAsync(toDeleted, ("replica", seed.ReplicaId)));

        Assert.Equal("23514", deleting.SqlState);
        Assert.Contains("pinned", deleting.MessageText);
        Assert.Equal("23514", deleted.SqlState);
        Assert.Equal(BackupReplicaStatus.DeletePending, (await _world.ReplicaAsync(seed.ReplicaId)).Status);

        await _world.Store().RequestCancellationAsync(seed.TenantId, restore.Id, Now.AddHours(2), default);

        Assert.Equal(1, await _world.ExecuteAsync(toDeleting, ("replica", seed.ReplicaId)));
        Assert.Equal(BackupReplicaStatus.Deleting, (await _world.ReplicaAsync(seed.ReplicaId)).Status);
    }

    [Fact]
    public async Task TheDatabaseRefusesToPinAReplicaWhosePhysicalDeleteHasBegun()
    {
        var seed = await SeedAsync();
        var due = await _world.SoftDeleteAsync(seed, Now);
        var restore = await _world.CreateAsync(seed, now: Now.AddHours(1));
        await _world.Store().RequestCancellationAsync(seed.TenantId, restore.Id, Now.AddHours(2), default);
        Assert.NotNull(await ClaimDueAsync(seed, due));

        var pin = await Assert.ThrowsAsync<PostgresException>(() => _world.ExecuteAsync(
            "INSERT INTO restore_replica_pins (\"Id\", \"TenantId\", \"OperationId\", \"ReplicaId\", \"CreatedAt\") VALUES (gen_random_uuid(), @tenant, @operation, @replica, now())",
            ("tenant", seed.TenantId.Value), ("operation", restore.Id), ("replica", seed.ReplicaId)));

        Assert.Equal("23514", pin.SqlState);
        Assert.Contains("deleted", pin.MessageText);
    }

    [Fact]
    public async Task AReleasedPinAndATerminalRestoreCannotBeReopenedByADirectWrite()
    {
        var seed = await SeedAsync();
        var store = _world.Store();
        var restore = await _world.CreateAsync(seed, store);
        await store.RequestCancellationAsync(seed.TenantId, restore.Id, At(5), default);

        var reactivate = await Assert.ThrowsAsync<PostgresException>(() => _world.ExecuteAsync(
            "UPDATE restore_replica_pins SET \"ReleasedAt\" = NULL WHERE \"OperationId\" = @operation", ("operation", restore.Id)));
        var reopen = await Assert.ThrowsAsync<PostgresException>(() => _world.ExecuteAsync(
            "UPDATE restore_operations SET \"Status\" = 'Pending', \"CompletedAt\" = NULL, \"TargetState\" = NULL, \"FailureCode\" = NULL WHERE \"Id\" = @operation", ("operation", restore.Id)));
        var reopenAttempt = await Assert.ThrowsAsync<PostgresException>(() => _world.ExecuteAsync(
            "UPDATE restore_attempts SET \"Status\" = 'Pending', \"CompletedAt\" = NULL, \"TargetState\" = NULL, \"FailureCode\" = NULL WHERE \"OperationId\" = @operation", ("operation", restore.Id)));

        Assert.Equal("23514", reactivate.SqlState);
        Assert.Equal("23514", reopen.SqlState);
        Assert.Equal("23514", reopenAttempt.SqlState);
        Assert.Equal(RestoreStatus.Cancelled, (await _world.OperationAsync(restore.Id)).Status);
    }

    [Fact]
    public async Task TheAgentDeleteClaimSkipsAPinnedReplicaEvenIfADeleteWasSomehowDispatched()
    {
        var seed = await _world.SeedAgentLocalAsync(RetentionKit.Policy(grace: Grace));
        var due = await _world.SoftDeleteAsync(seed, Now);
        var store = _world.Store();
        var restore = await _world.CreateAsync(seed, store, due - TimeSpan.FromHours(1));
        // Only a change made behind the platform's back (the guard trigger switched off) can leave a pinned replica in Deleting; the claim must still not hand it to the agent.
        await _world.ExecuteAsync("ALTER TABLE backup_replicas DISABLE TRIGGER trg_backup_replica_pin_guard");
        try
        {
            await _world.ExecuteAsync("UPDATE backup_replicas SET \"Status\" = 'Deleting', \"DeleteOperationId\" = gen_random_uuid(), \"DeleteNextAttemptAt\" = now(), " +
                "\"DeleteFailureCode\" = 'agent_unavailable' WHERE \"Id\" = @replica", ("replica", seed.ReplicaId));
        }
        finally
        {
            await _world.ExecuteAsync("ALTER TABLE backup_replicas ENABLE TRIGGER trg_backup_replica_pin_guard");
        }
        var identity = new AgentIdentity(seed.AgentId, seed.TenantId, Guid.NewGuid(), 1);
        var deletions = _world.AgentDeletionStore();

        Assert.Null(await deletions.ClaimAsync(identity, due + TimeSpan.FromDays(1), TimeSpan.FromMinutes(10), default));
        Assert.Null(await ClaimDueAsync(seed, due + TimeSpan.FromDays(1)));

        await store.RequestCancellationAsync(seed.TenantId, restore.Id, due + TimeSpan.FromDays(1), default);
        var task = await deletions.ClaimAsync(identity, due + TimeSpan.FromDays(1), TimeSpan.FromMinutes(10), default);

        Assert.Equal(seed.ReplicaId, task!.ReplicaId);
    }

    [Fact]
    public async Task ARestoreWhoseReplicaWasRemovedBehindThePlatformsBackIsFailedAtClaimInsteadOfBeingHandedToTheAgent()
    {
        var seed = await _world.SeedCentralAsync();
        var other = await _world.SeedAnotherAsync(seed);
        var store = _world.Store();
        var broken = await _world.CreateAsync(seed, store, At(0));
        var healthy = (await store.CreateAsync(Command(seed.TenantId, other.ArtifactId, seed.AgentId, now: At(1)), default)).Operation!;
        await _world.ExecuteAsync("ALTER TABLE backup_replicas DISABLE TRIGGER trg_backup_replica_pin_guard");
        try
        {
            await _world.ExecuteAsync("UPDATE backup_replicas SET \"Status\" = 'Deleted', \"DeleteRequestedAt\" = now(), \"DeleteReason\" = 'Manual', \"DeleteAfter\" = now(), " +
                "\"DeletedAt\" = now(), \"DeleteAttempts\" = 1 WHERE \"Id\" = @replica", ("replica", seed.ReplicaId));
        }
        finally
        {
            await _world.ExecuteAsync("ALTER TABLE backup_replicas ENABLE TRIGGER trg_backup_replica_pin_guard");
        }

        var claim = await ClaimAsync(store, seed.Agent, At(10));

        Assert.Equal(healthy.Id, claim!.Operation.Id);
        var ended = await _world.OperationAsync(broken.Id);
        Assert.Equal((RestoreStatus.Failed, RestoreFailureCodes.ReplicaUnavailable, RestoreTargetState.NotModified), (ended.Status, ended.FailureCode, ended.TargetState));
        Assert.False(ended.Pin.IsActive);
        Assert.Equal(0, await _world.ActivePinsAsync(seed.ReplicaId));
    }
}
