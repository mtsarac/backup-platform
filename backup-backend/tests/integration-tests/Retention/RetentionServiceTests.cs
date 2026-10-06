using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// Retention end to end: the real database, the real stores of separate "nodes", a real file system storage and the real services. Backups are
/// accepted at <see cref="RetentionServiceWorld.Accepted"/> and every cycle runs at an explicit instant, so each boundary is exact and nothing sleeps.
/// Every test has its own database, because a cycle acts on everything that is due in it.
/// </summary>
public sealed class RetentionServiceTests
{
    private static readonly DateTimeOffset A = RetentionServiceWorld.Accepted;

    /// <summary>Expires two hours after acceptance; the grace period then lasts one more hour, so the file may go at A + 3h.</summary>
    private static BackupRetentionPolicy Expiring => RetentionKit.Policy(keepFor: TimeSpan.FromHours(2), grace: TimeSpan.FromHours(1));

    private static async Task<RetentionCycleResult> CycleAsync(BackupRetentionService service, MutableClock clock, DateTimeOffset at)
    {
        RetentionServiceWorld.SetClock(clock, at);
        return await service.RunCycleAsync(CancellationToken.None);
    }

    private static async Task AssertHistoryKeptAsync(RetentionServiceWorld world, SeededBackup backup)
    {
        await using var context = world.Harness.Database.CreatePlatformContext();
        Assert.True(await context.BackupRuns.AnyAsync(run => run.Id == backup.RunId));
        Assert.True(await context.BackupRunAttempts.AnyAsync(attempt => attempt.Id == backup.AttemptId));
        Assert.True(await context.BackupArtifacts.AnyAsync(artifact => artifact.Id == backup.ArtifactId));
        Assert.True(await context.BackupReplicas.AnyAsync(replica => replica.Id == backup.ReplicaId));
    }

    [Fact]
    public async Task AnExpiredBackupIsSoftDeletedKeptThroughItsGraceAndThenPhysicallyRemovedWithItsHistoryIntact()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var backup = (await world.SeedAsync(Expiring)).Backup;
            var clock = new MutableClock(A);
            var service = world.Service("node-a", clock);

            // One second before the expiry nothing happens.
            Assert.Equal(new RetentionCycleResult(0, 0, 0, 0, 0, 0), await CycleAsync(service, clock, A + TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1)));
            Assert.Equal(BackupReplicaStatus.Available, (await world.ReplicaAsync(backup.ReplicaId)).Status);

            // The expiry instant itself is expired: the replica is soft deleted with the artifact's own grace period, and stays intact.
            Assert.Equal(new RetentionCycleResult(1, 1, 0, 0, 0, 0), await CycleAsync(service, clock, A + TimeSpan.FromHours(2)));
            var pending = await world.ReplicaAsync(backup.ReplicaId);
            Assert.Equal(BackupReplicaStatus.DeletePending, pending.Status);
            Assert.Equal(A + TimeSpan.FromHours(3), pending.DeleteAfter);
            Assert.Equal(BackupDeleteReason.RetentionExpired, pending.DeleteReason);
            Assert.True(pending.IsUsable);
            Assert.True(world.Exists(backup.Key));

            // Nothing is deleted before the grace period is over, however often the worker looks.
            Assert.Equal(new RetentionCycleResult(0, 0, 0, 0, 0, 0), await CycleAsync(service, clock, A + TimeSpan.FromHours(3) - TimeSpan.FromSeconds(1)));
            Assert.True(world.Exists(backup.Key));
            Assert.Equal(0, world.Storage.TotalDeleteCalls);

            // The grace period is over: the file is removed and only then is the replica recorded Deleted.
            Assert.Equal(new RetentionCycleResult(0, 0, 1, 1, 0, 0), await CycleAsync(service, clock, A + TimeSpan.FromHours(3)));
            Assert.False(world.Exists(backup.Key));
            var deleted = await world.ReplicaAsync(backup.ReplicaId);
            Assert.Equal(BackupReplicaStatus.Deleted, deleted.Status);
            Assert.Equal(A + TimeSpan.FromHours(3), deleted.DeletedAt);
            Assert.Equal(1, deleted.DeleteAttempts);
            Assert.False(deleted.IsUsable);
            // The history stays: the replica keeps its location and checksum, the artifact reads Deleted, runs and attempts are untouched.
            Assert.Equal(backup.Key, deleted.Key);
            Assert.Equal(BackupStoreHarness.ArtifactHash, deleted.Checksum);
            Assert.Equal(BackupArtifactState.Deleted, (await world.ArtifactAsync(backup.ArtifactId)).State);
            await AssertHistoryKeptAsync(world, backup);

            // Further cycles have nothing left to do and never touch the storage again.
            Assert.Equal(new RetentionCycleResult(0, 0, 0, 0, 0, 0), await CycleAsync(service, clock, A + TimeSpan.FromDays(30)));
            Assert.Equal(1, world.Storage.DeleteCalls(backup.Key));
        });
    }

    [Fact]
    public async Task RetentionThatIsDisabledKeepsTheFileForever()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var backup = (await world.SeedAsync(RetentionKit.Policy(enabled: false))).Backup;
            var clock = new MutableClock(A);
            var service = world.Service("node-a", clock);

            var result = await CycleAsync(service, clock, A + TimeSpan.FromDays(3650));

            Assert.Equal(new RetentionCycleResult(0, 0, 0, 0, 0, 0), result);
            Assert.True(world.Exists(backup.Key));
            Assert.Equal(BackupReplicaStatus.Available, (await world.ReplicaAsync(backup.ReplicaId)).Status);
            Assert.Null((await world.ArtifactAsync(backup.ArtifactId)).RetentionExpiresAt);
        });
    }

    [Fact]
    public async Task ThreeNodesCyclingAtOnceDeleteEachFileExactlyOnce()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var backups = new List<SeededBackup>();
            for (var i = 0; i < 5; i++) backups.Add((await world.SeedAsync(Expiring)).Backup);
            var marking = new MutableClock(A);
            await CycleAsync(world.Service("marker", marking), marking, A + TimeSpan.FromHours(2));

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cycles = new[] { "node-a", "node-b", "node-c" }.Select(node =>
            {
                var clock = new MutableClock(A + TimeSpan.FromHours(3));
                var service = world.Service(node, clock);
                return Task.Run(async () =>
                {
                    await gate.Task;
                    return await service.RunCycleAsync(CancellationToken.None);
                });
            }).ToArray();
            gate.SetResult();
            var results = await Task.WhenAll(cycles);

            Assert.Equal(5, results.Sum(result => result.DeletionsClaimed));
            Assert.Equal(5, results.Sum(result => result.Deleted));
            Assert.Equal(0, results.Sum(result => result.LeaseLost));
            Assert.Equal(0, results.Sum(result => result.Failed));
            foreach (var backup in backups)
            {
                Assert.False(world.Exists(backup.Key));
                Assert.Equal(1, world.Storage.DeleteCalls(backup.Key));
                Assert.Equal(1, world.Storage.Deletions(backup.Key));
                var replica = await world.ReplicaAsync(backup.ReplicaId);
                Assert.Equal(BackupReplicaStatus.Deleted, replica.Status);
                Assert.Equal(1, replica.DeleteAttempts);
            }
        });
    }

    [Fact]
    public async Task AManualDeleteAndAnAutomaticExpiryAreBothHandledInOneCycle()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var manual = (await world.SeedAsync(RetentionKit.Policy(keepFor: TimeSpan.FromDays(30), grace: TimeSpan.FromHours(1)))).Backup;
            var expiring = (await world.SeedAsync(Expiring)).Backup;
            var requestClock = new MutableClock(A + TimeSpan.FromMinutes(10));
            var user = Guid.NewGuid();

            var requested = await world.Artifacts(requestClock).RequestDeleteAsync(manual.TenantId, manual.ArtifactId, user, CancellationToken.None);

            Assert.Equal(BackupArtifactDeleteKind.Requested, requested.Kind);
            var soft = await world.ReplicaAsync(manual.ReplicaId);
            Assert.Equal(BackupReplicaStatus.DeletePending, soft.Status);
            Assert.Equal(BackupDeleteReason.Manual, soft.DeleteReason);
            Assert.Equal(user, soft.DeleteRequestedByUserId);
            Assert.Equal(A + TimeSpan.FromMinutes(70), soft.DeleteAfter);
            Assert.True(world.Exists(manual.Key));

            var clock = new MutableClock(A);
            var service = world.Service("node-a", clock);
            // At A + 2h the manual delete's grace is over and the other backup has just expired.
            var result = await CycleAsync(service, clock, A + TimeSpan.FromHours(2));

            Assert.Equal(new RetentionCycleResult(1, 1, 1, 1, 0, 0), result);
            Assert.False(world.Exists(manual.Key));
            Assert.Equal(BackupReplicaStatus.Deleted, (await world.ReplicaAsync(manual.ReplicaId)).Status);
            Assert.True(world.Exists(expiring.Key));
            Assert.Equal(BackupReplicaStatus.DeletePending, (await world.ReplicaAsync(expiring.ReplicaId)).Status);

            await CycleAsync(service, clock, A + TimeSpan.FromHours(3));
            Assert.False(world.Exists(expiring.Key));
            Assert.Equal(BackupReplicaStatus.Deleted, (await world.ReplicaAsync(expiring.ReplicaId)).Status);
        });
    }

    [Fact]
    public async Task ACancelledDeleteSurvivesTheGracePeriodAndIsNotDeletedAgainBecauseItIsPinned()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var backup = (await world.SeedAsync(Expiring)).Backup;
            var clock = new MutableClock(A);
            var service = world.Service("node-a", clock);
            await CycleAsync(service, clock, A + TimeSpan.FromHours(2));
            Assert.Equal(BackupReplicaStatus.DeletePending, (await world.ReplicaAsync(backup.ReplicaId)).Status);

            var cancelClock = new MutableClock(A + TimeSpan.FromMinutes(150));
            var cancelled = await world.Artifacts(cancelClock).CancelDeleteAsync(backup.TenantId, backup.ArtifactId, CancellationToken.None);

            Assert.Equal(BackupArtifactCancelKind.Restored, cancelled.Kind);
            Assert.Equal(BackupReplicaStatus.Available, (await world.ReplicaAsync(backup.ReplicaId)).Status);
            // It had already expired, so it is pinned: the scan must not immediately take it back.
            Assert.Equal(A + TimeSpan.FromMinutes(150), (await world.ArtifactAsync(backup.ArtifactId)).RetentionPinnedAt);
            foreach (var at in new[] { A + TimeSpan.FromHours(3), A + TimeSpan.FromDays(1), A + TimeSpan.FromDays(400) })
            {
                Assert.Equal(new RetentionCycleResult(0, 0, 0, 0, 0, 0), await CycleAsync(service, clock, at));
            }

            Assert.True(world.Exists(backup.Key));
            Assert.Equal(0, world.Storage.TotalDeleteCalls);
            Assert.Equal(BackupReplicaStatus.Available, (await world.ReplicaAsync(backup.ReplicaId)).Status);
        });
    }

    [Fact]
    public async Task AProviderFailureIsRecordedWithItsCodeAndTheDeleteSucceedsOnceTheRetryIsDue()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var backup = (await world.SeedAsync(Expiring)).Backup;
            var clock = new MutableClock(A);
            var service = world.Service("node-a", clock);
            await CycleAsync(service, clock, A + TimeSpan.FromHours(2));
            world.Storage.FailNextDelete(new StorageException(StorageFailureKind.AccessDenied, "denied"));
            var due = A + TimeSpan.FromHours(3);

            var failed = await CycleAsync(service, clock, due);

            Assert.Equal(new RetentionCycleResult(0, 0, 1, 0, 1, 0), failed);
            var replica = await world.ReplicaAsync(backup.ReplicaId);
            Assert.Equal(BackupReplicaStatus.DeleteFailed, replica.Status);
            Assert.Equal("access_denied", replica.DeleteFailureCode);
            Assert.Equal(due + TimeSpan.FromMinutes(1), replica.DeleteNextAttemptAt);
            Assert.Equal(1, replica.DeleteAttempts);
            Assert.True(world.Exists(backup.Key));

            Assert.Equal(0, (await CycleAsync(service, clock, due + TimeSpan.FromSeconds(59))).DeletionsClaimed);
            Assert.True(world.Exists(backup.Key));

            var retried = await CycleAsync(service, clock, due + TimeSpan.FromMinutes(1));

            Assert.Equal(new RetentionCycleResult(0, 0, 1, 1, 0, 0), retried);
            Assert.False(world.Exists(backup.Key));
            var deleted = await world.ReplicaAsync(backup.ReplicaId);
            Assert.Equal(BackupReplicaStatus.Deleted, deleted.Status);
            Assert.Equal(2, deleted.DeleteAttempts);
            Assert.Null(deleted.DeleteFailureCode);
        });
    }

    [Fact]
    public async Task AStorageTheBackendCannotReachFailsWithAStableCodeRetriesAtACappedCadenceAndNeverTouchesAnyFile()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var agent = await world.Harness.AddAgentAsync();
            var job = await RetentionKit.AddJobAsync(world.Harness, agent, Expiring);
            var backup = await RetentionKit.SeedAsync(world.Harness, agent, job, A, storageProvider: "agent-disk", storageTarget: "D:");
            // The same key exists on the backend's own storage: a delete that guessed a path would remove it.
            world.Place(backup.Key);
            var options = new BackupRetentionOptions { DeleteRetryInitialDelay = TimeSpan.FromMinutes(1), DeleteRetryMaxDelay = TimeSpan.FromMinutes(4) };
            var clock = new MutableClock(A);
            var service = world.Service("node-a", clock, options: options);
            await CycleAsync(service, clock, A + TimeSpan.FromHours(2));

            var at = A + TimeSpan.FromHours(3);
            var expectedDelays = new[] { 1, 2, 4, 4, 4, 4 };
            for (var attempt = 1; attempt <= expectedDelays.Length; attempt++)
            {
                Assert.Equal(1, (await CycleAsync(service, clock, at)).Failed);
                var replica = await world.ReplicaAsync(backup.ReplicaId);
                Assert.Equal(BackupReplicaStatus.DeleteFailed, replica.Status);
                Assert.Equal("storage_not_configured", replica.DeleteFailureCode);
                Assert.Equal(attempt, replica.DeleteAttempts);
                Assert.Equal(at + TimeSpan.FromMinutes(expectedDelays[attempt - 1]), replica.DeleteNextAttemptAt);
                // Not due a moment before.
                Assert.Equal(0, (await CycleAsync(service, clock, replica.DeleteNextAttemptAt!.Value - TimeSpan.FromSeconds(1))).DeletionsClaimed);
                at = replica.DeleteNextAttemptAt!.Value;
            }

            Assert.True(world.Exists(backup.Key));
            Assert.Equal(0, world.Storage.TotalDeleteCalls);
        });
    }

    [Fact]
    public async Task ADeleteThatHappenedButWasNotRecordedIsFinishedAfterTheLeaseExpiresBecauseTheFileIsAlreadyAbsent()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var backup = (await world.SeedAsync(Expiring)).Backup;
            var marking = new MutableClock(A);
            await CycleAsync(world.Service("marker", marking), marking, A + TimeSpan.FromHours(2));
            var flaky = new FlakyRetentionStore(world.Store());
            flaky.FailNextCompletions(1);
            var due = A + TimeSpan.FromHours(3);
            var lease = new BackupRetentionOptions().DeleteLeaseDuration;
            var clockA = new MutableClock(A);

            var first = await CycleAsync(world.Service("node-a", clockA, flaky), clockA, due);

            // The file is gone but the commit failed: the replica is still Deleting, not Deleted and not Failed.
            Assert.Equal(new RetentionCycleResult(0, 0, 1, 0, 0, 0), first);
            Assert.False(world.Exists(backup.Key));
            var stuck = await world.ReplicaAsync(backup.ReplicaId);
            Assert.Equal(BackupReplicaStatus.Deleting, stuck.Status);
            Assert.Equal(due + lease, stuck.DeleteLeaseExpiresAt);

            var clockB = new MutableClock(A);
            var nodeB = world.Service("node-b", clockB);
            Assert.Equal(0, (await CycleAsync(nodeB, clockB, due + lease - TimeSpan.FromSeconds(1))).DeletionsClaimed);
            Assert.Equal(BackupReplicaStatus.Deleting, (await world.ReplicaAsync(backup.ReplicaId)).Status);

            var recovered = await CycleAsync(nodeB, clockB, due + lease);

            Assert.Equal(new RetentionCycleResult(0, 0, 1, 1, 0, 0), recovered);
            var deleted = await world.ReplicaAsync(backup.ReplicaId);
            Assert.Equal(BackupReplicaStatus.Deleted, deleted.Status);
            Assert.Equal(2, deleted.DeleteAttempts);
            // The provider was asked twice; the second time the file was already gone.
            Assert.Equal(2, world.Storage.DeleteCalls(backup.Key));
            Assert.Equal(1, world.Storage.Deletions(backup.Key));
        });
    }

    [Fact]
    public async Task ADeleteAClaimingNodeNeverFinishedIsTakenOverAfterItsLeaseAndTheOldHolderCannotFinishIt()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var backup = (await world.SeedAsync(Expiring)).Backup;
            var marking = new MutableClock(A);
            await CycleAsync(world.Service("marker", marking), marking, A + TimeSpan.FromHours(2));
            var due = A + TimeSpan.FromHours(3);
            var lease = TimeSpan.FromMinutes(10);
            var crashing = world.Store();

            // A node claims the delete and dies before doing anything.
            var claimed = Assert.Single(await crashing.ClaimDueDeletionsAsync("crashed-node", due, lease, 50, CancellationToken.None));
            Assert.Equal(backup.ReplicaId, claimed.ReplicaId);

            // While the lease is alive the delete is refused to everybody, including a cancel.
            var rescueClock = new MutableClock(A);
            var rescuer = world.Service("node-b", rescueClock);
            Assert.Equal(0, (await CycleAsync(rescuer, rescueClock, due + lease - TimeSpan.FromSeconds(1))).DeletionsClaimed);
            var cancelClock = new MutableClock(due + TimeSpan.FromMinutes(1));
            var cancel = await world.Artifacts(cancelClock).CancelDeleteAsync(backup.TenantId, backup.ArtifactId, CancellationToken.None);
            Assert.NotEqual(BackupArtifactCancelKind.Restored, cancel.Kind);
            Assert.True(world.Exists(backup.Key));

            var result = await CycleAsync(rescuer, rescueClock, due + lease);

            Assert.Equal(new RetentionCycleResult(0, 0, 1, 1, 0, 0), result);
            Assert.False(world.Exists(backup.Key));
            var deleted = await world.ReplicaAsync(backup.ReplicaId);
            Assert.Equal((BackupReplicaStatus.Deleted, 2), (deleted.Status, deleted.DeleteAttempts));
            // The crashed node wakes up late: its token no longer fences anything.
            Assert.False(await crashing.CompleteDeletionAsync(claimed.ReplicaId, claimed.LeaseToken, due + lease + TimeSpan.FromMinutes(5), CancellationToken.None));
            Assert.False(await crashing.FailDeletionAsync(claimed.ReplicaId, claimed.LeaseToken, due + lease + TimeSpan.FromMinutes(5), "unexpected", due + lease + TimeSpan.FromMinutes(10), CancellationToken.None));
            Assert.Equal(due + lease, (await world.ReplicaAsync(backup.ReplicaId)).DeletedAt);
        });
    }

    [Fact]
    public async Task AnArtifactOfAnotherTenantIsInvisibleAndUntouchable()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var backup = (await world.SeedAsync(Expiring)).Backup;
            var stranger = await world.Harness.AddTenantAsync();
            var artifacts = world.Artifacts(new MutableClock(A));

            Assert.Null(await artifacts.GetAsync(stranger, backup.ArtifactId, CancellationToken.None));
            Assert.Empty(await artifacts.ListAsync(stranger, null, 50, CancellationToken.None));
            var delete = await artifacts.RequestDeleteAsync(stranger, backup.ArtifactId, Guid.NewGuid(), CancellationToken.None);
            var cancel = await artifacts.CancelDeleteAsync(stranger, backup.ArtifactId, CancellationToken.None);

            Assert.Equal(BackupArtifactDeleteKind.NotFound, delete.Kind);
            Assert.Null(delete.Artifact);
            Assert.Equal(BackupArtifactCancelKind.NotFound, cancel.Kind);
            Assert.Equal(BackupReplicaStatus.Available, (await world.ReplicaAsync(backup.ReplicaId)).Status);
            var owner = await artifacts.GetAsync(backup.TenantId, backup.ArtifactId, CancellationToken.None);
            Assert.Equal(backup.RunId, owner!.RunId);
            Assert.Equal(backup.JobId, owner.JobId);
        });
    }

    [Fact]
    public async Task ChangingAJobsRetentionOnlyShapesBackupsAcceptedAfterwards()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seeded = await world.SeedAsync(Expiring);
            var clock = new MutableClock(A);
            var artifacts = world.Artifacts(clock);

            var view = await artifacts.ChangeJobRetentionAsync(
                seeded.Agent.TenantId, seeded.Job.Id, new BackupRetentionPolicyRequest(KeepForSeconds: TimeSpan.FromDays(30).TotalSeconds), CancellationToken.None);

            // Omitted members keep the job's current values.
            Assert.Equal(new BackupRetentionPolicyView(true, TimeSpan.FromDays(30).TotalSeconds, TimeSpan.FromHours(1).TotalSeconds), view!.Retention);
            var later = await world.SeedMoreAsync(seeded, A + TimeSpan.FromHours(1));
            var first = await world.ArtifactAsync(seeded.Backup.ArtifactId);
            var second = await world.ArtifactAsync(later.ArtifactId);
            Assert.Equal(A + TimeSpan.FromHours(2), first.RetentionExpiresAt);
            Assert.Equal(A + TimeSpan.FromHours(1) + TimeSpan.FromDays(30), second.RetentionExpiresAt);

            var service = world.Service("node-a", clock);
            var result = await CycleAsync(service, clock, A + TimeSpan.FromHours(2));

            Assert.Equal(new RetentionCycleResult(1, 1, 0, 0, 0, 0), result);
            Assert.Equal(BackupReplicaStatus.DeletePending, (await world.ReplicaAsync(seeded.Backup.ReplicaId)).Status);
            Assert.Equal(BackupReplicaStatus.Available, (await world.ReplicaAsync(later.ReplicaId)).Status);
        });
    }

    [Fact]
    public async Task ARetentionBelowThePlatformMinimumIsRefusedAndTheJobIsUnchanged()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seeded = await world.SeedAsync(Expiring);
            var artifacts = world.Artifacts(new MutableClock(A));

            await Assert.ThrowsAsync<ArgumentException>(() => artifacts.ChangeJobRetentionAsync(
                seeded.Agent.TenantId, seeded.Job.Id, new BackupRetentionPolicyRequest(KeepForSeconds: 60), CancellationToken.None));

            await using var context = world.Harness.Database.CreatePlatformContext();
            var job = await context.BackupJobs.AsNoTracking().SingleAsync(candidate => candidate.Id == seeded.Job.Id);
            Assert.Equal(TimeSpan.FromHours(2), job.RetentionPolicy.KeepFor);
            Assert.Null(await artifacts.ChangeJobRetentionAsync(seeded.Agent.TenantId, Guid.NewGuid(), new BackupRetentionPolicyRequest(Enabled: false), CancellationToken.None));
        });
    }
}
