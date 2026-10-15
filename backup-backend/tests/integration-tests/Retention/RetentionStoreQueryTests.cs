using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// The read side of the retention store: what an administrator sees of a tenant's backups and what the cleanup sweeps are allowed to
/// conclude about stored files (is this key referenced, may this attempt still be writing).
/// </summary>
public sealed class RetentionStoreQueryTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly TimeSpan Grace = TimeSpan.FromHours(6);
    private static readonly DateTimeOffset T0 = BackupStoreHarness.T0;
    private readonly RetentionWorld _world = new(fixture);

    private static BackupRetentionPolicy Policy => RetentionKit.Policy(grace: Grace);

    // ---- get and list -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnArtifactIsReturnedWithItsRunJobAndReplicasAndOnlyToItsOwnTenant()
    {
        var backup = await _world.SeedAsync(Policy);
        var other = await _world.SeedAsync(Policy);
        var store = _world.Store();

        var record = await store.GetArtifactAsync(backup.TenantId, backup.ArtifactId, default);

        Assert.NotNull(record);
        Assert.Equal(backup.ArtifactId, record.Artifact.Id);
        Assert.Equal(backup.RunId, record.RunId);
        Assert.Equal(backup.JobId, record.JobId);
        Assert.Equal(BackupArtifactState.Available, record.Artifact.State);
        Assert.Equal(backup.AcceptedAt, record.Artifact.AcceptedAt);
        var replica = Assert.Single(record.Artifact.Replicas);
        Assert.Equal(backup.ReplicaId, replica.Id);
        Assert.Equal(backup.Key, replica.Key);
        Assert.Equal(backup.StorageProvider, replica.StorageProvider);
        Assert.Equal(backup.StorageTarget, replica.StorageTarget);
        Assert.Null(await store.GetArtifactAsync(backup.TenantId, other.ArtifactId, default));
        Assert.Null(await store.GetArtifactAsync(backup.TenantId, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task ListingIsNewestAcceptedFirstFilterableByJobBoundedByTheLimitAndNeverShowsAnotherTenant()
    {
        var agent = await _world.Harness.AddAgentAsync();
        var jobA = await RetentionKit.AddJobAsync(_world.Harness, agent, Policy);
        var jobB = await RetentionKit.AddJobAsync(_world.Harness, agent, Policy);
        // Accepted out of creation order on purpose: the order must follow acceptance, not insertion.
        var middle = await RetentionKit.SeedAsync(_world.Harness, agent, jobA, T0.AddHours(1));
        var newest = await RetentionKit.SeedAsync(_world.Harness, agent, jobA, T0.AddHours(2));
        var oldest = await RetentionKit.SeedAsync(_world.Harness, agent, jobA, T0);
        var elsewhere = await RetentionKit.SeedAsync(_world.Harness, agent, jobB, T0.AddHours(1.5));
        var stranger = await _world.SeedAsync(Policy);
        var store = _world.Store();

        var all = await store.ListArtifactsAsync(agent.TenantId, null, 50, default);
        var onlyA = await store.ListArtifactsAsync(agent.TenantId, jobA.Id, 50, default);
        var onlyB = await store.ListArtifactsAsync(agent.TenantId, jobB.Id, 50, default);

        Assert.Equal(new[] { newest.ArtifactId, elsewhere.ArtifactId, middle.ArtifactId, oldest.ArtifactId }, all.Select(record => record.Artifact.Id).ToArray());
        Assert.Equal(new[] { newest.ArtifactId, middle.ArtifactId, oldest.ArtifactId }, onlyA.Select(record => record.Artifact.Id).ToArray());
        Assert.All(onlyA, record => Assert.Equal(jobA.Id, record.JobId));
        Assert.Equal(elsewhere.RunId, Assert.Single(onlyB).RunId);
        Assert.DoesNotContain(all, record => record.Artifact.Id == stranger.ArtifactId);
        Assert.Equal(new[] { newest.ArtifactId, elsewhere.ArtifactId }, (await store.ListArtifactsAsync(agent.TenantId, null, 2, default)).Select(record => record.Artifact.Id).ToArray());
        // A limit below one is raised to one rather than rejected or returning everything.
        Assert.Equal(newest.ArtifactId, Assert.Single(await store.ListArtifactsAsync(agent.TenantId, null, 0, default)).Artifact.Id);
        Assert.Equal(newest.ArtifactId, Assert.Single(await store.ListArtifactsAsync(agent.TenantId, null, -5, default)).Artifact.Id);
    }

    [Fact]
    public async Task BackupsAcceptedAtTheSameInstantListInAStableOrderByIdentity()
    {
        var agent = await _world.Harness.AddAgentAsync();
        var job = await RetentionKit.AddJobAsync(_world.Harness, agent, Policy);
        var first = await RetentionKit.SeedAsync(_world.Harness, agent, job, T0);
        var second = await RetentionKit.SeedAsync(_world.Harness, agent, job, T0);
        var third = await RetentionKit.SeedAsync(_world.Harness, agent, job, T0);
        var store = _world.Store();
        // PostgreSQL compares uuids byte by byte, which is the order of their big-endian hex form (not .NET's Guid order).
        var expected = new[] { first.ArtifactId, second.ArtifactId, third.ArtifactId }
            .OrderBy(id => Convert.ToHexString(id.ToByteArray(bigEndian: true)), StringComparer.Ordinal).ToArray();

        var listed = (await store.ListArtifactsAsync(agent.TenantId, null, 50, default)).Select(record => record.Artifact.Id).ToArray();
        var again = (await store.ListArtifactsAsync(agent.TenantId, null, 50, default)).Select(record => record.Artifact.Id).ToArray();

        Assert.Equal(expected, listed);
        Assert.Equal(listed, again);
    }

    [Fact]
    public async Task AFullyDeletedBackupStaysInTheListing()
    {
        var agent = await _world.Harness.AddAgentAsync();
        var job = await RetentionKit.AddJobAsync(_world.Harness, agent, Policy);
        var gone = await RetentionKit.SeedAsync(_world.Harness, agent, job, T0);
        var kept = await RetentionKit.SeedAsync(_world.Harness, agent, job, T0.AddHours(1));
        var store = _world.Store();
        var due = await _world.RequestDeleteAsync(store, gone, T0.AddDays(1));
        var claim = await RetentionWorld.ClaimAsync(store, gone, due);
        Assert.NotNull(claim);
        Assert.True(await store.CompleteDeletionAsync(claim.ReplicaId, claim.LeaseToken, due, default));

        var listed = await store.ListArtifactsAsync(agent.TenantId, job.Id, 50, default);

        Assert.Equal(new[] { kept.ArtifactId, gone.ArtifactId }, listed.Select(record => record.Artifact.Id).ToArray());
        Assert.Equal(BackupArtifactState.Deleted, listed[1].Artifact.State);
        Assert.Equal(BackupReplicaStatus.Deleted, Assert.Single(listed[1].Artifact.Replicas).Status);
        Assert.Equal(BackupArtifactState.Available, listed[0].Artifact.State);
    }

    [Fact]
    public async Task PagedHistoryTraversesBeyondLegacyCapAndFiltersCreatedTimeJobAgentAndTenant()
    {
        var agent = await _world.Harness.AddAgentAsync();
        var job = await RetentionKit.AddJobAsync(_world.Harness, agent, Policy);
        var times = Enumerable.Range(0, 205).Select(index => T0.AddMinutes(index)).ToList();
        var expected = await RetentionKit.SeedBatchAsync(_world.Harness, agent, job, times);
        var stranger = await _world.SeedAsync(Policy);
        var store = _world.Store();
        var query = new BackupArtifactQuery(JobId: job.Id, AgentId: agent.AgentId.Value, State: "Available", PageSize: 100);
        var first = await store.QueryArtifactsAsync(agent.TenantId, query, default);
        var second = await store.QueryArtifactsAsync(agent.TenantId, query with { Page = 2 }, default);
        var third = await store.QueryArtifactsAsync(agent.TenantId, query with { Page = 3 }, default);
        Assert.Equal(205, first.TotalCount);
        Assert.Equal(205, third.TotalCount);
        Assert.Equal(expected.AsEnumerable().Reverse().Select(item => item.ArtifactId),
            first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Artifact.Id));
        Assert.Equal(5, third.Items.Count);
        Assert.DoesNotContain(first.Items, item => item.Artifact.Id == stranger.ArtifactId);
        Assert.All(first.Items, item =>
        {
            Assert.Equal(job.Name, item.JobName);
            Assert.Equal(job.Configuration.Database, item.DatabaseName);
            Assert.Equal(agent.AgentId.Value, item.AgentId);
        });
        var createdAt = expected[100].AcceptedAt.AddDays(-400);
        var bound = await store.QueryArtifactsAsync(agent.TenantId, query with { From = createdAt, To = createdAt }, default);
        Assert.Equal(expected[100].ArtifactId, Assert.Single(bound.Items).Artifact.Id);
        Assert.Equal(1, bound.TotalCount);
        var empty = await store.QueryArtifactsAsync(agent.TenantId, query with { JobId = stranger.JobId }, default);
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.TotalCount);
        var wrongAgent = await store.QueryArtifactsAsync(agent.TenantId, query with { AgentId = Guid.NewGuid() }, default);
        Assert.Empty(wrongAgent.Items);
    }

    [Fact]
    public async Task StateFiltersReflectPendingPhysicalFailureAndDeletedHistory()
    {
        var backup = await _world.SeedAsync(Policy);
        var store = _world.Store();
        var query = new BackupArtifactQuery(JobId: backup.JobId);
        var due = await _world.RequestDeleteAsync(store, backup, T0.AddDays(1));
        Assert.Single((await store.QueryArtifactsAsync(backup.TenantId, query with { State = "DeletePending" }, default)).Items);
        var claim = await RetentionWorld.ClaimAsync(store, backup, due);
        Assert.NotNull(claim);
        Assert.Single((await store.QueryArtifactsAsync(backup.TenantId, query with { State = "Deleting" }, default)).Items);
        Assert.Empty((await store.QueryArtifactsAsync(backup.TenantId, query with { State = "DeletePending" }, default)).Items);
        Assert.True(await store.FailDeletionAsync(claim.ReplicaId, claim.LeaseToken, due, BackupDeleteFailureCodes.IoError, due.AddMinutes(1), default));
        Assert.Single((await store.QueryArtifactsAsync(backup.TenantId, query with { State = "DeleteFailed" }, default)).Items);
        var retry = await RetentionWorld.ClaimAsync(store, backup, due.AddMinutes(1));
        Assert.NotNull(retry);
        Assert.True(await store.CompleteDeletionAsync(retry.ReplicaId, retry.LeaseToken, due.AddMinutes(1), default));
        Assert.Single((await store.QueryArtifactsAsync(backup.TenantId, query with { State = "Deleted" }, default)).Items);
        Assert.Empty((await store.QueryArtifactsAsync(backup.TenantId, query with { State = "DeleteFailed" }, default)).Items);
    }

    [Fact]
    public async Task AgentUnavailableDispatchIsDeletingRatherThanAPhysicalDeleteFailure()
    {
        var agent = await _world.Harness.AddAgentAsync();
        var job = await RetentionKit.AddJobAsync(_world.Harness, agent, Policy, BackupStorageExecutionLocation.Agent);
        var backup = await RetentionKit.SeedAsync(_world.Harness, agent, job, T0);
        var store = _world.Store();
        var due = await _world.RequestDeleteAsync(store, backup, T0.AddDays(1));
        await store.ClaimDueDeletionsAsync("worker", due, TimeSpan.FromMinutes(5), 100, default);
        var query = new BackupArtifactQuery(JobId: job.Id);
        Assert.Single((await store.QueryArtifactsAsync(agent.TenantId, query with { State = "Deleting" }, default)).Items);
        Assert.Empty((await store.QueryArtifactsAsync(agent.TenantId, query with { State = "DeleteFailed" }, default)).Items);
    }

    // ---- which stored keys are referenced ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AKeyIsReferencedWhateverTheStatusOfTheReplicaAndInWhicheverTenantButOnlyUnderItsOwnProvider()
    {
        var store = _world.Store();
        var available = await _world.SeedAsync(Policy);
        var pending = await _world.SeedAsync(Policy);
        // Requested far later than the others, so the claims below (which take everything due) never reach it.
        await _world.RequestDeleteAsync(store, pending, RetentionWorld.Accepted.AddDays(5));
        var deleted = await _world.SeedAsync(Policy);
        var deletedDue = await _world.RequestDeleteAsync(store, deleted, RetentionWorld.Accepted.AddDays(1));
        var deletedClaim = await RetentionWorld.ClaimAsync(store, deleted, deletedDue);
        Assert.NotNull(deletedClaim);
        Assert.True(await store.CompleteDeletionAsync(deletedClaim.ReplicaId, deletedClaim.LeaseToken, deletedDue, default));
        var failed = await _world.SeedAsync(Policy);
        var failedDue = await _world.RequestDeleteAsync(store, failed, RetentionWorld.Accepted.AddDays(1));
        var failedClaim = await RetentionWorld.ClaimAsync(store, failed, failedDue);
        Assert.NotNull(failedClaim);
        Assert.True(await store.FailDeletionAsync(failedClaim.ReplicaId, failedClaim.LeaseToken, failedDue, BackupDeleteFailureCodes.IoError, failedDue.AddMinutes(1), default));
        var agent = await _world.Harness.AddAgentAsync();
        var archiveJob = await RetentionKit.AddJobAsync(_world.Harness, agent, Policy);
        var archived = await RetentionKit.SeedAsync(_world.Harness, agent, archiveJob, T0, storageProvider: "archive");
        var asked = new[] { available.Key, pending.Key, deleted.Key, failed.Key, archived.Key, "no/such/file.bak" };

        var underLocal = await store.FindReferencedKeysAsync("local", asked, default);
        var underArchive = await store.FindReferencedKeysAsync("archive", asked, default);
        var underNothing = await store.FindReferencedKeysAsync("elsewhere", asked, default);

        Assert.True(underLocal.SetEquals([available.Key, pending.Key, deleted.Key, failed.Key]));
        Assert.True(underArchive.SetEquals([archived.Key]));
        Assert.Empty(underNothing);
    }

    [Fact]
    public async Task LookingUpManyKeysFindsTheReferencedOnesInEveryChunkAndNothingForNoKeys()
    {
        var first = await _world.SeedAsync(Policy);
        var last = await _world.SeedAsync(Policy);
        var store = _world.Store();
        var keys = new List<string> { first.Key };
        keys.AddRange(Enumerable.Range(0, 1300).Select(index => $"unreferenced/{index}/file.bak"));
        keys.Add(last.Key);
        keys.Add(first.Key);

        var found = await store.FindReferencedKeysAsync("local", keys, default);

        Assert.True(found.SetEquals([first.Key, last.Key]));
        Assert.Empty(await store.FindReferencedKeysAsync("local", [], default));
    }

    // ---- what is known about attempts -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACompletedAttemptIsReportedAsNotActiveWithItsRunAndJob()
    {
        var backup = await _world.SeedAsync(Policy);
        var store = _world.Store();

        var activity = await store.GetAttemptActivityAsync([backup.AttemptId], default);

        var attempt = Assert.Single(activity).Value;
        Assert.Equal(backup.AttemptId, attempt.AttemptId);
        Assert.Equal(backup.TenantId, attempt.TenantId);
        Assert.Equal(backup.JobId, attempt.JobId);
        Assert.Equal(backup.RunId, attempt.RunId);
        Assert.Equal(BackupStatus.Succeeded, attempt.AttemptStatus);
        Assert.Equal(BackupStatus.Succeeded, attempt.RunStatus);
        Assert.False(attempt.RunIsActive);
        Assert.False(attempt.AttemptIsLive);
    }

    [Fact]
    public async Task ARunInFlightIsActiveAndAnAttemptHoldingALeaseIsLiveWhileAFailedAttemptWaitingForARetryIsActiveButNotLive()
    {
        var harness = _world.Harness;
        var runs = harness.Store();
        var store = _world.Store();
        var agent = await harness.AddAgentAsync();
        var job = await harness.AddJobAsync(agent);

        var queued = await harness.StartRunAsync(runs, job, "queued", T0);
        var queuedActivity = (await store.GetAttemptActivityAsync([queued.CurrentAttempt.Id], default))[queued.CurrentAttempt.Id];
        Assert.Equal(BackupStatus.Pending, queuedActivity.RunStatus);
        Assert.True(queuedActivity.RunIsActive);
        Assert.True(queuedActivity.AttemptIsLive);

        var claim = await BackupStoreHarness.ClaimAsync(runs, agent, T0.AddSeconds(1));
        Assert.NotNull(claim);
        var leased = (await store.GetAttemptActivityAsync([claim.Attempt.Id], default))[claim.Attempt.Id];
        Assert.Equal(BackupStatus.Leased, leased.AttemptStatus);
        Assert.True(leased.RunIsActive);
        Assert.True(leased.AttemptIsLive);

        await BackupStoreHarness.StartAttemptAsync(runs, claim, T0.AddSeconds(2));
        var running = (await store.GetAttemptActivityAsync([claim.Attempt.Id], default))[claim.Attempt.Id];
        Assert.Equal(BackupStatus.Running, running.AttemptStatus);
        Assert.True(running.AttemptIsLive);

        // A retryable failure: this attempt is over, but its run is not, so the files of the failed attempt must still be protected.
        var retryAgent = await harness.AddAgentAsync();
        var retryJob = await harness.AddJobAsync(retryAgent, retry: new BackupRetryPolicy
        {
            MaxAttempts = 3, InitialDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromMinutes(10), Backoff = BackupBackoffStrategy.Exponential, Jitter = 0
        });
        await harness.StartRunAsync(runs, retryJob, "retry", T0);
        var retryClaim = await BackupStoreHarness.ClaimAsync(runs, retryAgent, T0.AddSeconds(1));
        Assert.NotNull(retryClaim);
        await BackupStoreHarness.StartAttemptAsync(runs, retryClaim, T0.AddSeconds(2));
        await BackupStoreHarness.CompleteAsync(runs, retryClaim, BackupStoreHarness.Failed(BackupFailureCodes.DatabaseUnavailable), T0.AddSeconds(3));
        var waiting = (await store.GetAttemptActivityAsync([retryClaim.Attempt.Id], default))[retryClaim.Attempt.Id];
        Assert.Equal(BackupStatus.Failed, waiting.AttemptStatus);
        Assert.Equal(BackupStatus.RetryPending, waiting.RunStatus);
        Assert.True(waiting.RunIsActive);
        Assert.False(waiting.AttemptIsLive);
    }

    [Fact]
    public async Task AFailedRunThatWillNotBeRetriedIsNoLongerActive()
    {
        var harness = _world.Harness;
        var runs = harness.Store();
        var agent = await harness.AddAgentAsync();
        var job = await harness.AddJobAsync(agent);
        await harness.StartRunAsync(runs, job, "terminal", T0);
        var claim = await BackupStoreHarness.ClaimAsync(runs, agent, T0.AddSeconds(1));
        Assert.NotNull(claim);
        await BackupStoreHarness.StartAttemptAsync(runs, claim, T0.AddSeconds(2));
        await BackupStoreHarness.CompleteAsync(runs, claim, BackupStoreHarness.Failed(BackupFailureCodes.DatabaseNotFound), T0.AddSeconds(3));

        var activity = (await _world.Store().GetAttemptActivityAsync([claim.Attempt.Id], default))[claim.Attempt.Id];

        Assert.Equal(BackupStatus.Failed, activity.RunStatus);
        Assert.False(activity.RunIsActive);
        Assert.False(activity.AttemptIsLive);
    }

    [Fact]
    public async Task UnknownAttemptIdsAreSimplyAbsentEvenAmongManyLookedUpAtOnce()
    {
        var backup = await _world.SeedAsync(Policy);
        var store = _world.Store();
        var ids = Enumerable.Range(0, 1200).Select(_ => Guid.NewGuid()).ToList();
        ids.Insert(700, backup.AttemptId);

        var activity = await store.GetAttemptActivityAsync(ids, default);

        Assert.Equal(backup.AttemptId, Assert.Single(activity).Key);
        Assert.Empty(await store.GetAttemptActivityAsync([Guid.NewGuid()], default));
        Assert.Empty(await store.GetAttemptActivityAsync([], default));
    }
}
