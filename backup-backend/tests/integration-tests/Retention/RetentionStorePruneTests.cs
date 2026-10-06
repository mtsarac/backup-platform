using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Backups;
using Microsoft.EntityFrameworkCore;
using Completion = BackupPlatform.Application.Backups.BackupCompletion;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// Pruning decided schedule occurrences. The statement is global (it looks at every tenant), so each test runs against a database of its own and
/// can assert exact row counts. Occurrences are real entities with explicit decision times; no clock is involved.
/// </summary>
public sealed class RetentionStorePruneTests
{
    private static readonly DateTimeOffset T0 = BackupStoreHarness.T0;
    private static readonly DateTimeOffset FarFuture = T0.AddYears(50);

    private static Task RunAsync(Func<PruneWorld, Task> test) =>
        BackupStoreHarness.WithIsolatedDatabaseAsync(harness => test(new PruneWorld(harness)));

    [Fact]
    public async Task OnlyDecisionsOlderThanTheCutoffAreRemovedAndTheCutoffItselfIsExclusive() => await RunAsync(async world =>
    {
        var (agent, job) = await world.NewJobAsync();
        var rows = new List<BackupScheduleOccurrence>();
        for (var index = 0; index < 6; index++) rows.Add(await world.AddAsync(agent, job, T0.AddHours(index), T0.AddHours(index).AddSeconds(10)));
        var store = world.Store();

        Assert.Equal(0, await store.PruneScheduleOccurrencesAsync(rows[0].DecidedAt, 100, default));
        Assert.Equal(3, await store.PruneScheduleOccurrencesAsync(rows[3].DecidedAt, 100, default));

        Assert.Equal(rows.Skip(3).Select(row => row.Id).ToArray(), await world.RemainingAsync(agent));
    });

    [Fact]
    public async Task TheNewestOccurrenceOfAJobIsNeverPrunedHoweverOldItsDecisionIsBecauseItIsTheSchedulersWatermark() => await RunAsync(async world =>
    {
        var (agent, job) = await world.NewJobAsync();
        // The newest scheduled instant carries the OLDEST decision time: age of the decision must not matter, only recency of the instant.
        var watermark = await world.AddAsync(agent, job, T0.AddHours(3), T0.AddDays(-30));
        var older = new List<BackupScheduleOccurrence>();
        for (var index = 0; index < 3; index++) older.Add(await world.AddAsync(agent, job, T0.AddHours(index), T0.AddDays(-20 + index)));
        var store = world.Store();

        Assert.Equal(3, await store.PruneScheduleOccurrencesAsync(FarFuture, 100, default));

        Assert.Equal(new[] { watermark.Id }, await world.RemainingAsync(agent));
        Assert.Equal(0, await store.PruneScheduleOccurrencesAsync(FarFuture, 100, default));
        Assert.Equal(new[] { watermark.Id }, await world.RemainingAsync(agent));
    });

    [Fact]
    public async Task AnOccurrenceWhoseRunHasNotFinishedIsKeptAndPrunedOnceTheRunEnds() => await RunAsync(async world =>
    {
        var (agent, job) = await world.NewJobAsync();
        // The finished run goes first: claiming always takes the agent's oldest queued run.
        var finished = await world.FinishRunAsync(agent, job, "finished");
        var active = await world.QueueRunAsync(job, "active");
        await world.AddAsync(agent, job, T0.AddHours(1), T0.AddHours(1), finished);
        var forActive = await world.AddAsync(agent, job, T0.AddHours(2), T0.AddHours(2), active);
        var blockedByActive = await world.AddAsync(agent, job, T0.AddHours(3), T0.AddHours(3), active, BackupOccurrenceOutcome.SkippedOverlap);
        await world.AddAsync(agent, job, T0.AddHours(4), T0.AddHours(4));
        var newest = await world.AddAsync(agent, job, T0.AddHours(5), T0.AddHours(5));
        var store = world.Store();

        Assert.Equal(2, await store.PruneScheduleOccurrencesAsync(FarFuture, 100, default));
        Assert.Equal(new[] { forActive.Id, blockedByActive.Id, newest.Id }, await world.RemainingAsync(agent));

        await world.FinishQueuedRunAsync(agent, active);

        Assert.Equal(2, await store.PruneScheduleOccurrencesAsync(FarFuture, 100, default));
        Assert.Equal(new[] { newest.Id }, await world.RemainingAsync(agent));
    });

    [Fact]
    public async Task PruningNeverTouchesRunsAttemptsArtifactsReplicasOrJobs() => await RunAsync(async world =>
    {
        var (agent, job) = await world.NewJobAsync();
        var succeeded = await world.SucceedRunAsync(agent, job, "succeeded");
        var failed = await world.FinishRunAsync(agent, job, "failed");
        await world.AddAsync(agent, job, T0.AddHours(1), T0.AddHours(1), succeeded);
        await world.AddAsync(agent, job, T0.AddHours(2), T0.AddHours(2), failed);
        await world.AddAsync(agent, job, T0.AddHours(3), T0.AddHours(3));
        await world.AddAsync(agent, job, T0.AddHours(4), T0.AddHours(4));
        var before = await world.CountsAsync();
        Assert.True(before.Runs == 2 && before.Artifacts == 1 && before.Replicas == 1);

        Assert.Equal(3, await world.Store().PruneScheduleOccurrencesAsync(FarFuture, 100, default));

        Assert.Equal(before, await world.CountsAsync());
        Assert.Equal(1, await world.Harness.CountArtifactsAsync(succeeded));
    });

    [Fact]
    public async Task EveryTenantAndJobKeepsItsOwnNewestOccurrenceWhileItsOldOnesArePrunedIndependently() => await RunAsync(async world =>
    {
        var (agentA, jobA) = await world.NewJobAsync();
        var (agentB, jobB1) = await world.NewJobAsync();
        var jobB2 = await RetentionKit.AddJobAsync(world.Harness, agentB);
        // Tenant A has by far the most recent instants; they must not make anything of tenant B look superseded or protected.
        var newestA = await world.AddAsync(agentA, jobA, T0.AddDays(10), T0.AddDays(10));
        await world.AddAsync(agentA, jobA, T0.AddDays(9), T0.AddDays(9));
        await world.AddAsync(agentA, jobA, T0.AddDays(8), T0.AddDays(8));
        await world.AddAsync(agentB, jobB1, T0.AddHours(1), T0.AddHours(1));
        await world.AddAsync(agentB, jobB1, T0.AddHours(2), T0.AddHours(2));
        var newestB1 = await world.AddAsync(agentB, jobB1, T0.AddHours(3), T0.AddHours(3));
        var onlyB2 = await world.AddAsync(agentB, jobB2, T0.AddHours(1), T0.AddHours(1));

        Assert.Equal(4, await world.Store().PruneScheduleOccurrencesAsync(FarFuture, 100, default));

        Assert.Equal(new[] { newestA.Id }, await world.RemainingAsync(agentA));
        Assert.Equal(new[] { onlyB2.Id, newestB1.Id }, await world.RemainingAsync(agentB));
    });

    [Fact]
    public async Task EachCallRemovesAtMostTheLimitOldestFirstAndRepeatedCallsConvergeOnTheNewestOnly() => await RunAsync(async world =>
    {
        var (agent, job) = await world.NewJobAsync();
        var rows = new List<BackupScheduleOccurrence>();
        for (var index = 0; index < 11; index++) rows.Add(await world.AddAsync(agent, job, T0.AddHours(index), T0.AddHours(index)));
        var store = world.Store();

        Assert.Equal(4, await store.PruneScheduleOccurrencesAsync(FarFuture, 4, default));
        Assert.Equal(rows.Skip(4).Select(row => row.Id).ToArray(), await world.RemainingAsync(agent));
        Assert.Equal(4, await store.PruneScheduleOccurrencesAsync(FarFuture, 4, default));
        Assert.Equal(rows.Skip(8).Select(row => row.Id).ToArray(), await world.RemainingAsync(agent));
        Assert.Equal(2, await store.PruneScheduleOccurrencesAsync(FarFuture, 4, default));
        Assert.Equal(0, await store.PruneScheduleOccurrencesAsync(FarFuture, 4, default));
        Assert.Equal(new[] { rows[10].Id }, await world.RemainingAsync(agent));
    });

    /// <summary>One isolated database with the helpers to build tenants, jobs, runs and occurrences in it.</summary>
    private sealed class PruneWorld(BackupStoreHarness harness)
    {
        private readonly RetentionWorld _retention = new(harness.Database);

        public BackupStoreHarness Harness { get; } = harness;

        public PostgreSqlBackupRetentionStore Store() => _retention.Store();

        public async Task<(StoreAgent Agent, BackupJob Job)> NewJobAsync()
        {
            var agent = await Harness.AddAgentAsync();
            return (agent, await RetentionKit.AddJobAsync(Harness, agent));
        }

        public async Task<BackupScheduleOccurrence> AddAsync(StoreAgent agent, BackupJob job, DateTimeOffset scheduledAt, DateTimeOffset decidedAt,
            Guid? runId = null, BackupOccurrenceOutcome? outcome = null)
        {
            var decided = outcome ?? (runId is null ? BackupOccurrenceOutcome.MissedGraceExpired : BackupOccurrenceOutcome.RunCreated);
            var reason = decided switch
            {
                BackupOccurrenceOutcome.RunCreated => BackupOccurrenceReasons.RunCreated,
                BackupOccurrenceOutcome.SkippedOverlap => BackupOccurrenceReasons.ActiveRun,
                _ => BackupOccurrenceReasons.GraceElapsed
            };
            var occurrence = new BackupScheduleOccurrence(Guid.NewGuid(), agent.TenantId, job.Id, scheduledAt, decided, reason, runId, decidedAt);
            await using var context = Harness.Database.CreatePlatformContext();
            context.BackupScheduleOccurrences.Add(occurrence);
            await context.SaveChangesAsync();
            return occurrence;
        }

        /// <summary>The ids of the tenant's remaining occurrences, in the order they were scheduled.</summary>
        public async Task<Guid[]> RemainingAsync(StoreAgent agent)
        {
            await using var context = Harness.Database.CreatePlatformContext();
            return await context.BackupScheduleOccurrences.AsNoTracking().Where(occurrence => occurrence.TenantId == agent.TenantId)
                .OrderBy(occurrence => occurrence.ScheduledAt).Select(occurrence => occurrence.Id).ToArrayAsync();
        }

        public async Task<Guid> QueueRunAsync(BackupJob job, string key) => (await Harness.StartRunAsync(Harness.Store(), job, key, T0)).Id;

        /// <summary>Queues a run and takes it to a terminal Failed state (not retryable).</summary>
        public async Task<Guid> FinishRunAsync(StoreAgent agent, BackupJob job, string key)
        {
            var runId = await QueueRunAsync(job, key);
            await FinishQueuedRunAsync(agent, runId);
            return runId;
        }

        /// <summary>Claims the agent's oldest queued run, which must be <paramref name="runId"/>, and ends it as Failed.</summary>
        public Task FinishQueuedRunAsync(StoreAgent agent, Guid runId) => RunToCompletionAsync(agent, runId, BackupStoreHarness.Failed(BackupFailureCodes.DatabaseNotFound));

        /// <summary>A run that ended successfully and published an artifact with one replica.</summary>
        public async Task<Guid> SucceedRunAsync(StoreAgent agent, BackupJob job, string key)
        {
            var runId = await QueueRunAsync(job, key);
            await RunToCompletionAsync(agent, runId, BackupStoreHarness.Success(T0.AddSeconds(3)));
            return runId;
        }

        private async Task RunToCompletionAsync(StoreAgent agent, Guid runId, Completion completion)
        {
            var runs = Harness.Store();
            var claim = await BackupStoreHarness.ClaimAsync(runs, agent, T0.AddSeconds(1));
            Assert.NotNull(claim);
            Assert.Equal(runId, claim.Run.Id);
            await BackupStoreHarness.StartAttemptAsync(runs, claim, T0.AddSeconds(2));
            await BackupStoreHarness.CompleteAsync(runs, claim, completion, T0.AddSeconds(3));
        }

        public async Task<TableCounts> CountsAsync()
        {
            await using var context = Harness.Database.CreatePlatformContext();
            return new TableCounts(await context.BackupJobs.CountAsync(), await context.BackupRuns.CountAsync(), await context.BackupRunAttempts.CountAsync(),
                await context.BackupArtifacts.CountAsync(), await context.BackupReplicas.CountAsync(), await context.BackupLifecycleFacts.CountAsync());
        }
    }

    private sealed record TableCounts(int Jobs, int Runs, int Attempts, int Artifacts, int Replicas, int Facts);
}
