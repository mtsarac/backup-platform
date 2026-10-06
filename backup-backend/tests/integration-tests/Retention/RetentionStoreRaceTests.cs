using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// The retention store under real concurrency: every racer has its own store (a separate backend node as far as the database can tell) and all
/// start together behind a barrier. Each test repeats its race many times with a fresh backup and asserts only what must hold for EVERY outcome
/// the race may produce: exactly one winner, and a final state that is the committed truth of that winner.
/// </summary>
public sealed class RetentionStoreRaceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const int Iterations = 20;
    private static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(6);
    private static readonly TimeSpan Lease = RetentionWorld.Lease;
    private static DateTimeOffset Expiry => RetentionWorld.Accepted + Keep;
    private readonly RetentionWorld _world = new(fixture);

    private Task<SeededBackup> SeedAsync() => _world.SeedAsync(RetentionKit.Policy(keepFor: Keep, grace: Grace));

    [Fact]
    public async Task ManyNodesClaimingTheSameDueReplicaOnlyOneGetsItAndNoReplicaIsEverHandedOutTwice()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var backup = await SeedAsync();
            var due = await _world.RequestDeleteAsync(_world.Store(), backup, RetentionWorld.Accepted.AddDays(1));
            var gate = new RaceGate();
            var racers = Enumerable.Range(0, 4).Select(index =>
            {
                var store = _world.Store();
                var owner = $"node-{index}";
                return gate.Run(() => store.ClaimDueDeletionsAsync(owner, due, Lease, 500, default));
            }).ToArray();
            gate.Open();

            var batches = await Task.WhenAll(racers);

            var everything = batches.SelectMany(batch => batch).ToList();
            Assert.Equal(everything.Count, everything.Select(claim => claim.ReplicaId).Distinct().Count());
            var winner = Assert.Single(RetentionWorld.Mine(everything, backup));
            var replica = await _world.ReplicaAsync(backup.ReplicaId);
            Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
            Assert.Equal(winner.LeaseToken, replica.DeleteLeaseToken);
            Assert.Equal(1, replica.DeleteAttempts);
            Assert.Equal(1, winner.Attempt);
        }
    }

    [Fact]
    public async Task AManualDeleteRacingTheRetentionScanMovesTheReplicaExactlyOnceWithAConsistentReason()
    {
        var scanAt = Expiry;
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var backup = await SeedAsync();
            var user = Guid.NewGuid();
            var scanner = _world.Store();
            var gate = new RaceGate();
            var scan = gate.Run(() => scanner.ApplyExpiredRetentionAsync(scanAt, 1000, default));
            var manual = gate.Run(() => _world.Store().RequestDeleteAsync(backup.TenantId, backup.ArtifactId, user, scanAt, default));
            gate.Open();

            await scan;
            var manualResult = await manual;

            var replica = await _world.ReplicaAsync(backup.ReplicaId);
            Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status);
            Assert.Equal(scanAt, replica.DeleteRequestedAt);
            Assert.Equal(scanAt + Grace, replica.DeleteAfter);
            Assert.Equal(0, replica.DeleteAttempts);
            if (replica.DeleteReason == BackupDeleteReason.Manual)
            {
                Assert.Equal(BackupArtifactDeleteKind.Requested, manualResult.Kind);
                Assert.Equal(user, replica.DeleteRequestedByUserId);
            }
            else
            {
                Assert.Equal(BackupDeleteReason.RetentionExpired, replica.DeleteReason);
                Assert.Equal(BackupArtifactDeleteKind.AlreadyInProgress, manualResult.Kind);
                Assert.Null(replica.DeleteRequestedByUserId);
            }
            // A scan that skipped the artifact because the manual delete held it picks it up next time and must change nothing.
            await scanner.ApplyExpiredRetentionAsync(scanAt, 1000, default);
            var settled = await _world.ReplicaAsync(backup.ReplicaId);
            Assert.Equal(replica.DeleteReason, settled.DeleteReason);
            Assert.Equal(replica.DeleteRequestedByUserId, settled.DeleteRequestedByUserId);
            Assert.Equal(replica.DeleteAfter, settled.DeleteAfter);
            Assert.NotNull((await _world.ArtifactAsync(backup.ArtifactId)).RetentionAppliedAt);
        }
    }

    [Fact]
    public async Task ConcurrentManualDeletesOfOneBackupHaveExactlyOneWinnerWhoseRequesterIsRecorded()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var backup = await SeedAsync();
            var requestedAt = RetentionWorld.Accepted.AddDays(1);
            var users = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
            var gate = new RaceGate();
            var racers = users.Select(user =>
            {
                var store = _world.Store();
                return gate.Run(async () => (User: user, Kind: (await store.RequestDeleteAsync(backup.TenantId, backup.ArtifactId, user, requestedAt, default)).Kind));
            }).ToArray();
            gate.Open();

            var outcomes = await Task.WhenAll(racers);

            var winner = Assert.Single(outcomes, outcome => outcome.Kind == BackupArtifactDeleteKind.Requested);
            Assert.All(outcomes.Where(outcome => outcome.User != winner.User), outcome => Assert.Equal(BackupArtifactDeleteKind.AlreadyInProgress, outcome.Kind));
            var replica = await _world.ReplicaAsync(backup.ReplicaId);
            Assert.Equal(winner.User, replica.DeleteRequestedByUserId);
            Assert.Equal(requestedAt + Grace, replica.DeleteAfter);
        }
    }

    [Fact]
    public async Task ACancelRacingAClaimEndsEitherCancelledAndUnclaimedOrClaimedAndRefusedNeverBoth()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var backup = await SeedAsync();
            var due = await _world.RequestDeleteAsync(_world.Store(), backup, RetentionWorld.Accepted.AddDays(1));
            var gate = new RaceGate();
            var cancelStore = _world.Store();
            var claimStore = _world.Store();
            var cancelRace = gate.Run(() => cancelStore.CancelDeleteAsync(backup.TenantId, backup.ArtifactId, due, default));
            var claimRace = gate.Run(() => claimStore.ClaimDueDeletionsAsync("node-b", due, Lease, 500, default));
            gate.Open();

            var cancel = await cancelRace;
            var claims = RetentionWorld.Mine(await claimRace, backup).ToList();

            var replica = await _world.ReplicaAsync(backup.ReplicaId);
            if (cancel.Kind == BackupArtifactCancelKind.Restored)
            {
                Assert.Empty(claims);
                Assert.Equal(BackupReplicaStatus.Available, replica.Status);
                Assert.Equal(0, replica.DeleteAttempts);
                Assert.Null(replica.DeleteLeaseToken);
                Assert.Null(replica.DeleteAfter);
            }
            else
            {
                Assert.Equal(BackupArtifactCancelKind.NothingToCancel, cancel.Kind);
                var claim = Assert.Single(claims);
                Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
                Assert.Equal(claim.LeaseToken, replica.DeleteLeaseToken);
                Assert.Equal(1, replica.DeleteAttempts);
            }
        }
    }

    [Fact]
    public async Task ATakeoverRacingTheOldHoldersCompletionEndsWithExactlyOneOwnerOfTheOutcome()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var backup = await SeedAsync();
            var nodeA = _world.Store();
            var due = await _world.RequestDeleteAsync(nodeA, backup, RetentionWorld.Accepted.AddDays(1));
            var first = await RetentionWorld.ClaimAsync(nodeA, backup, due, "node-a");
            Assert.NotNull(first);
            var leaseEnds = due + Lease;
            var gate = new RaceGate();
            var nodeB = _world.Store();
            var completion = gate.Run(() => nodeA.CompleteDeletionAsync(first.ReplicaId, first.LeaseToken, leaseEnds, default));
            var takeover = gate.Run(() => nodeB.ClaimDueDeletionsAsync("node-b", leaseEnds, Lease, 500, default));
            gate.Open();

            var completed = await completion;
            var claims = RetentionWorld.Mine(await takeover, backup).ToList();

            var replica = await _world.ReplicaAsync(backup.ReplicaId);
            if (completed)
            {
                Assert.Empty(claims);
                Assert.Equal(BackupReplicaStatus.Deleted, replica.Status);
                Assert.Equal(leaseEnds, replica.DeletedAt);
            }
            else
            {
                var second = Assert.Single(claims);
                Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
                Assert.Equal(second.LeaseToken, replica.DeleteLeaseToken);
                Assert.NotEqual(first.LeaseToken, second.LeaseToken);
                Assert.Equal(2, replica.DeleteAttempts);
            }
        }
    }

    [Fact]
    public async Task TheSameCompletionDeliveredTwiceAtOnceIsAcceptedExactlyOnce()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var backup = await SeedAsync();
            var store = _world.Store();
            var due = await _world.RequestDeleteAsync(store, backup, RetentionWorld.Accepted.AddDays(1));
            var claim = await RetentionWorld.ClaimAsync(store, backup, due);
            Assert.NotNull(claim);
            var doneAt = due + TimeSpan.FromMinutes(1);
            var gate = new RaceGate();
            var racers = Enumerable.Range(0, 3).Select(_ =>
            {
                var racer = _world.Store();
                return gate.Run(() => racer.CompleteDeletionAsync(claim.ReplicaId, claim.LeaseToken, doneAt, default));
            }).ToArray();
            gate.Open();

            var accepted = await Task.WhenAll(racers);

            Assert.Single(accepted, result => result);
            var replica = await _world.ReplicaAsync(backup.ReplicaId);
            Assert.Equal(BackupReplicaStatus.Deleted, replica.Status);
            Assert.Equal(doneAt, replica.DeletedAt);
        }
    }

    [Fact]
    public async Task ConcurrentRetentionScansMoveEveryExpiredReplicaExactlyOnceBetweenThem()
    {
        await BackupStoreHarness.WithIsolatedDatabaseAsync(async harness =>
        {
            var world = new RetentionWorld(harness.Database);
            var policy = RetentionKit.Policy(keepFor: Keep, grace: Grace);
            var backups = new List<SeededBackup>();
            for (var index = 0; index < 6; index++) backups.Add(await world.SeedAsync(policy, RetentionWorld.Accepted.AddMinutes(index)));
            var scanAt = Expiry.AddDays(1);
            var gate = new RaceGate();
            var racers = Enumerable.Range(0, 3).Select(_ =>
            {
                var store = world.Store();
                return gate.Run(() => store.ApplyExpiredRetentionAsync(scanAt, 1000, default));
            }).ToArray();
            gate.Open();

            var results = await Task.WhenAll(racers);

            // Whoever got to an artifact first moved it; the others either skipped it or found nothing left to move.
            Assert.Equal(backups.Count, results.Sum(result => result.ReplicasMoved));
            foreach (var backup in backups)
            {
                var replica = await world.ReplicaAsync(backup.ReplicaId);
                Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status);
                Assert.Equal(scanAt, replica.DeleteRequestedAt);
            }
        });
    }
}
