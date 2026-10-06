using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using static BackupPlatform.IntegrationTests.AgentReplicaDeletionTestKit;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentReplicaDeletionRaceTests
{
    [Fact]
    public async Task ConcurrentRetentionWorkersDispatchDurablyAndConcurrentAgentPollsProduceOnlyOnePhysicalClaim()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world);
            await RequestAsync(world, seed);
            var gate = new RaceGate();
            var workers = Enumerable.Range(0, 4).Select(index =>
                gate.Run(() => world.Service($"worker-{index}", new MutableClock(Due), options: Options).RunCycleAsync(default))).ToArray();
            gate.Open();
            var cycles = await Task.WhenAll(workers);
            Assert.All(cycles, cycle => Assert.Equal(0, cycle.DeletionsClaimed));
            var waiting = await world.ReplicaAsync(seed.Backup.ReplicaId);
            Assert.Equal(BackupReplicaStatus.Deleting, waiting.Status);
            Assert.True(waiting.WaitingForAgent);
            Assert.Equal(0, waiting.DeleteAttempts);
            var claimAt = waiting.DeleteNextAttemptAt!.Value;
            var pollsGate = new RaceGate();
            var polls = Enumerable.Range(0, 8).Select(_ =>
                pollsGate.Run(() => Service(world, new MutableClock(claimAt)).ClaimAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), default))).ToArray();
            var rechecks = Enumerable.Range(0, 4).Select(index =>
                pollsGate.Run(() => world.Service($"recheck-{index}", new MutableClock(claimAt), options: Options).RunCycleAsync(default))).ToArray();
            pollsGate.Open();
            var results = await Task.WhenAll(polls);
            Assert.All(await Task.WhenAll(rechecks), cycle => Assert.Equal(0, cycle.DeletionsClaimed));
            var winner = Assert.Single(results, task => task is not null)!;
            var claimed = await world.ReplicaAsync(seed.Backup.ReplicaId);
            Assert.Equal(winner.OperationId, claimed.DeleteOperationId);
            Assert.Equal(winner.LeaseToken, claimed.DeleteLeaseToken);
            Assert.Equal(1, claimed.DeleteAttempts);
            Assert.False(claimed.WaitingForAgent);
            Assert.True(world.Exists(seed.Backup.Key));
            Assert.Equal(0, world.Storage.TotalDeleteCalls);
        });
    }

    [Fact]
    public async Task CancellationIsRefusedAfterDispatchEvenBeforeAnyAgentHasClaimedTheTask()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world);
            var clock = new MutableClock(Due);
            await RequestAsync(world, seed);
            await DispatchAsync(world, clock);
            var before = await world.ReplicaAsync(seed.Backup.ReplicaId);
            Assert.True(before.WaitingForAgent);
            var cancellation = await world.Artifacts(clock).CancelDeleteAsync(seed.Backup.TenantId, seed.Backup.ArtifactId, default);
            Assert.Equal(BackupArtifactCancelKind.NothingToCancel, cancellation.Kind);
            var after = await world.ReplicaAsync(seed.Backup.ReplicaId);
            Assert.Equal(BackupReplicaStatus.Deleting, after.Status);
            Assert.Equal(before.DeleteOperationId, after.DeleteOperationId);
            Assert.True(after.WaitingForAgent);
            Assert.NotNull(await Service(world, clock).ClaimAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), default));
            Assert.Equal(BackupArtifactCancelKind.NothingToCancel,
                (await world.Artifacts(clock).CancelDeleteAsync(seed.Backup.TenantId, seed.Backup.ArtifactId, default)).Kind);
            Assert.Equal(0, world.Storage.TotalDeleteCalls);
        });
    }

    [Fact]
    public async Task CancellationRacingAgentClaimAfterDispatchAlwaysRefusesAndLeavesTheAgentAsSoleHolder()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            for (var iteration = 0; iteration < 12; iteration++)
            {
                var seed = await SeedAsync(world);
                var clock = new MutableClock(Due);
                await RequestAsync(world, seed);
                await DispatchAsync(world, clock);
                var gate = new RaceGate();
                var cancellation = gate.Run(() => world.Store(Options).CancelDeleteAsync(seed.Backup.TenantId, seed.Backup.ArtifactId, Due, default));
                var poll = gate.Run(() => Service(world, clock).ClaimAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), default));
                gate.Open();
                Assert.Equal(BackupArtifactCancelKind.NothingToCancel, (await cancellation).Kind);
                var task = await poll;
                Assert.NotNull(task);
                var replica = await world.ReplicaAsync(task.ReplicaId);
                Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
                Assert.Equal(task.OperationId, replica.DeleteOperationId);
                Assert.Equal(task.LeaseToken, replica.DeleteLeaseToken);
                Assert.Equal(1, replica.DeleteAttempts);
                Assert.Null(await Service(world, clock).ClaimAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), default));
                Assert.Equal(0, world.Storage.TotalDeleteCalls);
            }
        });
    }

    [Fact]
    public async Task CancellationRacingDispatchEitherRestoresAnUnclaimedBackupOrRefusesAnIrreversiblyDispatchedBackup()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            for (var iteration = 0; iteration < 12; iteration++)
            {
                var seed = await SeedAsync(world);
                await RequestAsync(world, seed);
                var gate = new RaceGate();
                var cancellation = gate.Run(() => world.Store(Options).CancelDeleteAsync(seed.Backup.TenantId, seed.Backup.ArtifactId, Due, default));
                var dispatch = gate.Run(() => world.Store(Options).ClaimDueDeletionsAsync("backend", Due, Options.DeleteLeaseDuration, 100, default));
                gate.Open();
                var cancelled = await cancellation;
                Assert.Empty(await dispatch);
                var replica = await world.ReplicaAsync(seed.Backup.ReplicaId);
                var claim = await Service(world, new MutableClock(Due)).ClaimAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), default);
                if (cancelled.Kind == BackupArtifactCancelKind.Restored)
                {
                    Assert.Equal(BackupReplicaStatus.Available, replica.Status);
                    Assert.Null(replica.DeleteOperationId);
                    Assert.Null(replica.DeleteLeaseToken);
                    Assert.Null(claim);
                }
                else
                {
                    Assert.Equal(BackupArtifactCancelKind.NothingToCancel, cancelled.Kind);
                    Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
                    Assert.True(replica.WaitingForAgent);
                    Assert.NotNull(replica.DeleteOperationId);
                    Assert.NotNull(claim);
                }
                Assert.True(world.Exists(seed.Backup.Key));
                Assert.Equal(0, world.Storage.TotalDeleteCalls);
            }
        });
    }

    [Fact]
    public async Task ConcurrentExpiredLeasePollsRedeliverOnceAndFenceEveryOldHolderMutation()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world);
            var clock = new MutableClock(Due);
            var old = await ClaimAsync(world, seed, clock);
            RetentionServiceWorld.SetClock(clock, old.LeaseExpiresAt);
            var gate = new RaceGate();
            var racers = Enumerable.Range(0, 8).Select(_ =>
                gate.Run(() => Service(world, clock).ClaimAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), default))).ToArray();
            gate.Open();
            var winner = Assert.Single(await Task.WhenAll(racers), task => task is not null)!;
            Assert.Equal(old.OperationId, winner.OperationId);
            Assert.NotEqual(old.LeaseToken, winner.LeaseToken);
            Assert.Null(await Service(world, clock).RenewAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), old.ReplicaId, Lease(old), default));
            Assert.Equal(BackupMutationResult.Conflict, await Service(world, clock).CompleteAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), old.ReplicaId, Success(old), default));
            Assert.Equal(2, (await world.ReplicaAsync(old.ReplicaId)).DeleteAttempts);
        });
    }
}
