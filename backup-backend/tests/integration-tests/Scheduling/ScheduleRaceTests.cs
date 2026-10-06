using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using static BackupPlatform.IntegrationTests.Scheduling.ScheduleKit;

namespace BackupPlatform.IntegrationTests.Scheduling;

/// <summary>
/// Every logical occurrence makes at most one run, however many nodes, wake-ups, sweeps and edits meet at once. Each test builds independent
/// services (a separate node each, with its own connections) and lets them collide on the same job; Queue is used where overlap
/// would otherwise hide a duplicate.
/// </summary>
public sealed class ScheduleRaceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const int Rounds = 8;
    private readonly ScheduleKit _kit = new(fixture);

    private static async Task<BackupScheduleEvaluation[]> RunAllAsync(IEnumerable<Func<Task<BackupScheduleEvaluation>>> contenders) =>
        await Task.WhenAll(contenders.Select(contender => Task.Run(contender)));

    [Fact]
    public async Task ManyWorkersEvaluatingTheSameOccurrenceCreateExactlyOneRun()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        for (var round = 0; round < Rounds; round++)
        {
            await _kit.SeenAsync(agent, H(1, 1));
            var job = await _kit.AddJobAsync(agent, overlap: BackupOverlapPolicy.Queue);

            var results = await RunAllAsync(Enumerable.Range(0, 14).Select<int, Func<Task<BackupScheduleEvaluation>>>(_ =>
                () => _kit.Store().EvaluateAsync(job.TenantId, job.Id, H(1, 1), default)));

            Assert.Single(results, result => result.CreatedRunId is not null);
            var run = Assert.Single(await _kit.RunsOfAsync(job));
            Assert.Equal(H(1), run.ScheduledOccurrence);
            var occurrence = Assert.Single(await _kit.OccurrencesAsync(job));
            Assert.Equal((BackupOccurrenceOutcome.RunCreated, run.Id), (occurrence.Outcome, occurrence.RunId));
        }
    }

    [Fact]
    public async Task AQuartzRefireStormWithDriftingClocksStillCreatesOneRun()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 10));
        var job = await _kit.AddJobAsync(agent, overlap: BackupOverlapPolicy.Queue);

        // The same wake-up delivered again and again by retries and misfire handling, each node believing it is a slightly different time.
        var results = await RunAllAsync(Enumerable.Range(0, 12).Select<int, Func<Task<BackupScheduleEvaluation>>>(index =>
            () => _kit.Store().EvaluateAsync(job.TenantId, job.Id, H(1, 1, index), default)));

        Assert.Single(results, result => result.CreatedRunId is not null);
        Assert.Equal(1, await _kit.Runs.CountRunsAsync(job.Id));
        Assert.Single(await _kit.OccurrencesAsync(job));
    }

    [Fact]
    public async Task ASweepAReconciliationAndAWakeUpRacingOnTheSameJobCreateOneRun()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 1));
        for (var round = 0; round < Rounds; round++)
        {
            var job = await _kit.AddJobAsync(agent, overlap: BackupOverlapPolicy.Queue);
            var clock = new MutableClock(H(1, 1));
            var sweeping = _kit.Service(clock);
            var reconciling = _kit.Service(clock);
            var woken = _kit.Service(clock);

            await Task.WhenAll(
                Task.Run(() => sweeping.SweepAsync(default)),
                Task.Run(() => reconciling.ReconcileAsync(default)),
                Task.Run(() => woken.EvaluateAsync(job.TenantId, job.Id, default)),
                Task.Run(() => _kit.Service(clock).SweepAsync(default)));

            var run = Assert.Single(await _kit.RunsOfAsync(job));
            Assert.Equal(H(1), run.ScheduledOccurrence);
            Assert.Single(await _kit.OccurrencesAsync(job));
        }
    }

    [Fact]
    public async Task AScheduleEditRacingAWakeUpEitherRunsTheOldOccurrenceOnceOrNeverAndNeverReplaysThePast()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 5));
        var ran = 0;
        var dropped = 0;
        for (var round = 0; round < 20; round++)
        {
            var job = await _kit.AddJobAsync(agent, overlap: BackupOverlapPolicy.Queue);
            var clock = new MutableClock(H(1, 5));
            var editing = _kit.Service(clock);
            var request = new BackupScheduleRequest(true, "30 * * * *", "UTC", 1800, "Queue");

            await Task.WhenAll(
                Task.Run(() => editing.ChangeScheduleAsync(job.TenantId, job.Id, request, default)),
                Task.Run(() => _kit.Store().EvaluateAsync(job.TenantId, job.Id, H(1, 5), default)));

            var runs = await _kit.RunsOfAsync(job);
            Assert.True(runs.Count <= 1, "one logical occurrence, at most one run");
            var current = await _kit.JobAsync(job);
            Assert.Equal("30 * * * *", current.Schedule!.CronExpression);
            if (runs.Count == 1)
            {
                // The wake-up won the lock: it ran the OLD schedule's 01:00 occurrence, and the edit left that run alone.
                Assert.Equal(H(1), runs[0].ScheduledOccurrence);
                Assert.Equal(BackupTriggerTypes.Scheduled, runs[0].TriggerType);
                ran++;
            }
            else dropped++;
            // Either way, the new schedule starts from the edit: 01:30 is the first occurrence it ever produces.
            Assert.Equal(H(1, 30), current.Schedule.NextOccurrenceAfter(H(0)));
            await _kit.SeenAsync(agent, H(1, 31));
            var next = await _kit.Store().EvaluateAsync(job.TenantId, job.Id, H(1, 31), default);
            Assert.NotNull(next.CreatedRunId);
            Assert.Equal(H(1, 30), (await _kit.RunAsync(job, next.CreatedRunId!.Value)).ScheduledOccurrence);
            Assert.Equal(runs.Count + 1, await _kit.Runs.CountRunsAsync(job.Id));
        }
        Assert.Equal(20, ran + dropped);
    }

    [Fact]
    public async Task ADisableRacingAWakeUpEitherCreatesTheRunBeforeOrRecordsJobDisabledAndNothingStartsAfter()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(1, 5));
        for (var round = 0; round < 20; round++)
        {
            var job = await _kit.AddJobAsync(agent, overlap: BackupOverlapPolicy.Queue);
            var clock = new MutableClock(H(1, 5));
            var editing = _kit.Service(clock);

            await Task.WhenAll(
                Task.Run(() => editing.SetEnabledAsync(job.TenantId, job.Id, false, default)),
                Task.Run(() => _kit.Store().EvaluateAsync(job.TenantId, job.Id, H(1, 5), default)));

            var occurrence = Assert.Single(await _kit.OccurrencesAsync(job));
            var runs = await _kit.RunsOfAsync(job);
            Assert.Equal(occurrence.Outcome == BackupOccurrenceOutcome.RunCreated ? 1 : 0, runs.Count);
            Assert.Contains(occurrence.Outcome, new[] { BackupOccurrenceOutcome.RunCreated, BackupOccurrenceOutcome.JobDisabled });
            Assert.False((await _kit.JobAsync(job)).Enabled);
            // Once disabled, no later wake-up or sweep starts anything.
            await _kit.SeenAsync(agent, H(5, 5));
            Assert.Null((await _kit.Store().EvaluateAsync(job.TenantId, job.Id, H(5, 5), default)).CreatedRunId);
            Assert.Equal(runs.Count, await _kit.Runs.CountRunsAsync(job.Id));
        }
    }

    [Fact]
    public async Task SeveralBackendsRecoveringFromTheSameOutageCreateOneCatchUpRun()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(3, 20));
        for (var round = 0; round < Rounds; round++)
        {
            var job = await _kit.AddJobAsync(agent, grace: TimeSpan.FromMinutes(90), overlap: BackupOverlapPolicy.Queue);
            var clock = new MutableClock(H(3, 20)); // 01:00, 02:00 and 03:00 all passed while every backend was down

            var reports = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => _kit.Service(clock).SweepAsync(default))));

            Assert.All(reports, report => Assert.Empty(report.Errors));
            var run = Assert.Single(await _kit.RunsOfAsync(job));
            Assert.Equal(H(3), run.ScheduledOccurrence);
            var history = (await _kit.OccurrencesAsync(job)).Select(o => (o.ScheduledAt, o.Outcome)).ToArray();
            Assert.Equal([(H(1), BackupOccurrenceOutcome.MissedGraceExpired), (H(2), BackupOccurrenceOutcome.MissedSuperseded), (H(3), BackupOccurrenceOutcome.RunCreated)], history);
        }
    }

    [Fact]
    public async Task ManyOccurrencesAcrossManyJobsNeverProduceTwoRunsForOneInstant()
    {
        var agent = await _kit.Runs.AddAgentAsync();
        await _kit.SeenAsync(agent, H(2, 1));
        var jobs = new List<BackupJob>();
        for (var index = 0; index < 5; index++) jobs.Add(await _kit.AddJobAsync(agent, cron: "*/10 * * * *", grace: TimeSpan.FromHours(2), overlap: BackupOverlapPolicy.Queue));
        var clock = new MutableClock(H(2, 1));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _kit.Service(clock).SweepAsync(default))));

        foreach (var job in jobs)
        {
            var runs = await _kit.RunsOfAsync(job);
            Assert.Single(runs);
            Assert.Equal(H(2), runs[0].ScheduledOccurrence);
        }
    }
}
