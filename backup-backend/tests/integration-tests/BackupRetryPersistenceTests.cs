using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Completion = BackupPlatform.Application.Backups.BackupCompletion;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// The retry lifecycle exercised through the real store and database: durable scheduling, the exactly-once start of a due retry
/// under concurrent workers, races against cancellation and manual retries, stale results of superseded attempts and the
/// database guards behind all of it. Time is a plain value passed to the store, so every scenario is deterministic.
/// </summary>
public sealed class BackupRetryPersistenceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset FailedAt = Now.AddSeconds(5);
    private static readonly DateTimeOffset Due = FailedAt + Delay;
    private static readonly string SourceHash = new('a', 64);
    private static readonly string ArtifactHash = new('b', 64);
    // Three attempts in total, 30s then 60s between them; jitter is neutralised by the store's sampler (() => 0).
    private static readonly BackupRetryPolicy Policy = new()
    {
        MaxAttempts = 3, InitialDelay = Delay, MaxDelay = TimeSpan.FromMinutes(10), Backoff = BackupBackoffStrategy.Exponential, Jitter = 0.2
    };
    // The GLOBAL limit counts live attempts of every tenant in the database; this suite shares one database and leaves leases
    // behind, so it opts out of that limit and relies on the default agent limit (1) only.
    private static readonly BackupExecutionOptions Limits = new() { GlobalMaxConcurrentBackups = 1_000_000, TenantMaxConcurrentBackups = 1_000 };

    [Fact]
    public async Task RetryableFailureSchedulesDurableRetryThatCreatesExactlyOneAttemptOnTheSameRunOnceDue()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = (await ScheduleRetryAsync(store, world)).Run;

        var scheduled = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.RetryPending, scheduled.Status);
        Assert.Equal(BackupRetryState.Scheduled, scheduled.RetryState);
        Assert.Equal(Due, scheduled.NextAttemptAt);
        Assert.Null(scheduled.CompletedAt);
        Assert.Null(scheduled.TerminalReason);
        var failed = Assert.Single(scheduled.Attempts);
        Assert.Equal(BackupStatus.Failed, failed.Status);
        Assert.Equal("DatabaseUnavailable", failed.TerminalReason);
        Assert.Single(scheduled.Facts, fact => fact.Kind == "BackupRetryScheduled" && fact.AttemptId == failed.Id);

        // Before the backoff elapsed nothing is created and nothing can be claimed.
        Assert.Empty(Mine(await store.StartDueRetriesAsync(Due.AddMilliseconds(-1), 500, default), run.Id));
        Assert.Null(await store.ClaimNextAsync(world.TenantId, world.AgentId, Due.AddMilliseconds(-1), Lease, default));
        Assert.Equal(BackupStatus.RetryPending, (await env.LoadAsync(world, run.Id)).Status);
        await AssertAttemptNumbersAsync(env, run.Id, 1);

        // Late scanners still anchor the attempt to the time it became eligible, not to when the scan got to it.
        var scanAt = Due.AddSeconds(7);
        var started = Assert.Single(Mine(await store.StartDueRetriesAsync(scanAt, 500, default), run.Id));
        Assert.Equal(world.AgentId, started.AgentId);
        var retried = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.Pending, retried.Status);
        Assert.Equal(BackupRetryState.None, retried.RetryState);
        Assert.Null(retried.NextAttemptAt);
        Assert.Null(retried.CompletedAt);
        Assert.Equal(2, retried.CurrentAttemptNumber);
        await AssertAttemptNumbersAsync(env, run.Id, 2);
        var second = retried.CurrentAttempt;
        Assert.Equal(BackupStatus.Pending, second.Status);
        Assert.Equal(scanAt, second.CreatedAt);
        Assert.Equal(Due, second.EligibleAt);
        Assert.Equal(BackupStatus.Failed, retried.Attempts.Single(attempt => attempt.Number == 1).Status);
        Assert.Single(retried.Facts, fact => fact.Kind == "BackupRetryStarted" && fact.AttemptId == second.Id);
        Assert.Empty(Mine(await store.StartDueRetriesAsync(scanAt.AddSeconds(1), 500, default), run.Id));

        var claim = await ClaimAsync(store, world, scanAt.AddSeconds(1));
        Assert.Equal(run.Id, claim.Run.Id);
        Assert.Equal(second.Id, claim.Attempt.Id);
        Assert.Equal(2, claim.Attempt.Number);
    }

    [Theory]
    [InlineData(BackupBackoffStrategy.Fixed, 27, 27, 27)]
    [InlineData(BackupBackoffStrategy.Exponential, 27, 54, 90)]
    public async Task PersistedRetryScheduleFollowsPolicyBackoffCapAndJitter(BackupBackoffStrategy backoff, int firstSeconds, int secondSeconds, int thirdSeconds)
    {
        var env = new Env(fixture);
        // A sample of 0.5 against Jitter 0.2 shortens every delay by 10%; the cap (100s) applies before jitter.
        var store = env.Store(() => 0.5);
        var policy = new BackupRetryPolicy { MaxAttempts = 4, InitialDelay = Delay, MaxDelay = TimeSpan.FromSeconds(100), Backoff = backoff, Jitter = 0.2 };
        var world = await env.SeedAsync(policy);
        var run = await StartAsync(store, world);
        var at = Now;
        foreach (var seconds in new[] { firstSeconds, secondSeconds, thirdSeconds })
        {
            var claim = await ClaimAsync(store, world, at);
            await FailAsync(store, claim, "OperationTimedOut", at);
            var persisted = await env.LoadAsync(world, run.Id);
            Assert.Equal(BackupStatus.RetryPending, persisted.Status);
            Assert.Equal(at.AddSeconds(seconds), persisted.NextAttemptAt);
            at = persisted.NextAttemptAt!.Value;
            Assert.Single(Mine(await store.StartDueRetriesAsync(at, 500, default), run.Id));
        }
        Assert.Equal(4, (await env.LoadAsync(world, run.Id)).CurrentAttemptNumber);
    }

    [Fact]
    public async Task RetryScheduleSurvivesAProcessRestart()
    {
        var env = new Env(fixture);
        var world = await env.SeedAsync();
        var run = (await ScheduleRetryAsync(env.Store(), world)).Run;

        // A restarted backend has no memory and its own connections: the schedule is nothing but a row.
        var restartedConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = "restarted-backend" }.ConnectionString;
        var restarted = Env.StoreFor(restartedConnection);
        Assert.Empty(Mine(await restarted.StartDueRetriesAsync(Due.AddMilliseconds(-1), 500, default), run.Id));
        var started = Assert.Single(Mine(await restarted.StartDueRetriesAsync(Due, 500, default), run.Id));
        Assert.Equal(world.AgentId, started.AgentId);

        var claim = await ClaimAsync(restarted, world, Due);
        Assert.Equal(2, claim.Attempt.Number);
        var command = await StartAttemptAsync(restarted, claim, Due);
        Assert.Equal(BackupMutationResult.Accepted, await restarted.CompleteAttemptAsync(command, Success(Due), default));
        var finished = await Env.StoreFor(restartedConnection).GetRunAsync(world.TenantId, run.Id, default);
        Assert.Equal(BackupStatus.Succeeded, finished!.Status);
        Assert.Equal(2, finished.CurrentAttemptNumber);
    }

    [Fact]
    public async Task ConcurrentWorkersStartExactlyOneRetryAttemptPerDueRun()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var runs = new List<(World World, Guid RunId)>();
        for (var index = 0; index < 5; index++)
        {
            var world = await env.SeedAsync();
            runs.Add((world, (await ScheduleRetryAsync(store, world)).Run.Id));
        }
        var results = await RaceAsync(10, _ => env.Store().StartDueRetriesAsync(Due, 500, default));
        foreach (var (world, runId) in runs)
        {
            Assert.Equal(1, results.SelectMany(result => result).Count(item => item.RunId == runId));
            await AssertAttemptNumbersAsync(env, runId, 2);
            var run = await env.LoadAsync(world, runId);
            Assert.Equal(BackupStatus.Pending, run.Status);
            Assert.Equal(2, run.CurrentAttemptNumber);
            Assert.Single(run.Facts, fact => fact.Kind == "BackupRetryStarted");
        }
    }

    [Fact]
    public async Task AutomaticAndManualRetryRacingCreateExactlyOneAttempt()
    {
        var env = new Env(fixture);
        for (var round = 0; round < 6; round++)
        {
            var world = await env.SeedAsync();
            var run = (await ScheduleRetryAsync(env.Store(), world)).Run;
            var results = await RaceAsync<object?>(8, index => index < 3
                ? Box(env.Store().StartDueRetriesAsync(Due, 500, default))
                : Box(env.Store().RetryRunAsync(world.TenantId, run.Id, Due, default)));
            var automatic = results.OfType<IReadOnlyList<BackupRetryStarted>>().SelectMany(list => list).Count(item => item.RunId == run.Id);
            var manual = results.OfType<BackupRetryResult>().Select(result => result.Outcome).ToList();
            Assert.Equal(1, automatic + manual.Count(outcome => outcome == BackupRetryOutcome.Started));
            Assert.All(manual.Where(outcome => outcome != BackupRetryOutcome.Started), outcome => Assert.Equal(BackupRetryOutcome.InProgress, outcome));
            await AssertAttemptNumbersAsync(env, run.Id, 2);
            var persisted = await env.LoadAsync(world, run.Id);
            Assert.Equal(BackupStatus.Pending, persisted.Status);
            Assert.Single(persisted.Facts, fact => fact.Kind == "BackupRetryStarted");
        }
    }

    [Theory]
    [InlineData(false, BackupOverlapPolicy.Queue)]
    [InlineData(false, BackupOverlapPolicy.Skip)]
    [InlineData(true, BackupOverlapPolicy.Queue)]
    [InlineData(true, BackupOverlapPolicy.Skip)]
    public async Task ConcurrentManualRetriesStartExactlyOneAttempt(bool fromTerminalFailure, BackupOverlapPolicy overlap)
    {
        var env = new Env(fixture);
        var world = await env.SeedAsync(overlap: overlap);
        var store = env.Store();
        // Either a run waiting for its backoff ("retry now") or a run that ended Failed and is reopened by the operator.
        var run = fromTerminalFailure
            ? await FailedTerminallyAsync(store, world)
            : (await ScheduleRetryAsync(store, world)).Run;
        var retryAt = Now.AddSeconds(10);
        var results = await RaceAsync(8, _ => env.Store().RetryRunAsync(world.TenantId, run.Id, retryAt, default));
        Assert.Single(results, result => result.Outcome == BackupRetryOutcome.Started);
        Assert.All(results.Where(result => result.Outcome != BackupRetryOutcome.Started), result => Assert.Equal(BackupRetryOutcome.InProgress, result.Outcome));
        await AssertAttemptNumbersAsync(env, run.Id, 2);
        var persisted = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.Pending, persisted.Status);
        Assert.Equal(BackupRetryState.None, persisted.RetryState);
        Assert.Null(persisted.NextAttemptAt);
        Assert.Null(persisted.CompletedAt);
        Assert.Equal(retryAt, persisted.CurrentAttempt.EligibleAt);
        // The scheduled retry that "retry now" replaced must not fire a second time later.
        Assert.Empty(Mine(await env.Store().StartDueRetriesAsync(Due.AddDays(1), 500, default), run.Id));
        await AssertAttemptNumbersAsync(env, run.Id, 2);
    }

    [Fact]
    public async Task CancellingARetryPendingRunWithdrawsTheRetryForEveryRetryPath()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = (await ScheduleRetryAsync(store, world)).Run;
        var cancelled = await store.RequestCancellationAsync(world.TenantId, run.Id, FailedAt.AddSeconds(1), default);
        Assert.Equal(BackupStatus.Cancelled, cancelled!.Status);

        var persisted = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.Cancelled, persisted.Status);
        Assert.Equal(BackupRetryState.None, persisted.RetryState);
        Assert.Null(persisted.NextAttemptAt);
        Assert.Equal(FailedAt.AddSeconds(1), persisted.CompletedAt);
        Assert.Empty(Mine(await store.StartDueRetriesAsync(Due.AddDays(1), 500, default), run.Id));
        Assert.Equal(BackupRetryOutcome.Cancelled, (await store.RetryRunAsync(world.TenantId, run.Id, Due.AddDays(1), default)).Outcome);
        Assert.Null(await store.ClaimNextAsync(world.TenantId, world.AgentId, Due.AddDays(1), Lease, default));
        await AssertAttemptNumbersAsync(env, run.Id, 1);
    }

    [Fact]
    public async Task CancellingAnImmediatelyRetriedRunCancelsTheQueuedAttemptSoItNeverExecutes()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = (await ScheduleRetryAsync(store, world)).Run;
        Assert.Equal(BackupRetryOutcome.Started, (await store.RetryRunAsync(world.TenantId, run.Id, FailedAt.AddSeconds(1), default)).Outcome);
        Assert.Equal(BackupStatus.Cancelled, (await store.RequestCancellationAsync(world.TenantId, run.Id, FailedAt.AddSeconds(2), default))!.Status);

        var persisted = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.Cancelled, persisted.Status);
        Assert.Equal(BackupStatus.Cancelled, persisted.CurrentAttempt.Status);
        Assert.Equal(2, persisted.CurrentAttemptNumber);
        Assert.Null(await store.ClaimNextAsync(world.TenantId, world.AgentId, Due, Lease, default));
        Assert.Empty(Mine(await store.StartDueRetriesAsync(Due.AddDays(1), 500, default), run.Id));
        await AssertAttemptNumbersAsync(env, run.Id, 2);
    }

    [Fact]
    public async Task CancellationRacingAnyRetryLeavesACancelledRunWithNoLiveAttemptAndNoAttemptAfterIt()
    {
        var env = new Env(fixture);
        for (var round = 0; round < 15; round++)
        {
            var world = await env.SeedAsync();
            var run = (await ScheduleRetryAsync(env.Store(), world)).Run;
            var results = await RaceAsync<object?>(4, index => index switch
            {
                0 => Box(env.Store().RequestCancellationAsync(world.TenantId, run.Id, Due, default)),
                1 => Box(env.Store().StartDueRetriesAsync(Due, 500, default)),
                _ => Box(env.Store().RetryRunAsync(world.TenantId, run.Id, Due, default))
            });
            Assert.Single(results.OfType<BackupRun>());
            var manual = results.OfType<BackupRetryResult>().Select(result => result.Outcome).ToList();
            Assert.All(manual, outcome => Assert.Contains(outcome, new[] { BackupRetryOutcome.Started, BackupRetryOutcome.InProgress, BackupRetryOutcome.Cancelled }));
            var retriesStarted = results.OfType<IReadOnlyList<BackupRetryStarted>>().SelectMany(list => list).Count(item => item.RunId == run.Id)
                + manual.Count(outcome => outcome == BackupRetryOutcome.Started);

            // Whoever got the agent lock first, cancellation takes effect and nothing stays alive under it.
            var final = await env.LoadAsync(world, run.Id);
            Assert.Equal(BackupStatus.Cancelled, final.Status);
            Assert.Equal(BackupRetryState.None, final.RetryState);
            Assert.Null(final.NextAttemptAt);
            Assert.NotNull(final.CompletedAt);
            Assert.Single(final.Facts, fact => fact.Kind == "BackupRunCancelled");
            var attempts = await env.AttemptsAsync(run.Id);
            Assert.InRange(retriesStarted, 0, 1);
            Assert.Equal(1 + retriesStarted, attempts.Count);
            Assert.DoesNotContain(attempts, attempt => attempt.Status is BackupStatus.Pending or BackupStatus.Leased or BackupStatus.Running);
            if (retriesStarted == 1) Assert.Equal(BackupStatus.Cancelled, attempts[1].Status);

            // Afterwards no path can bring the run back.
            Assert.Empty(Mine(await env.Store().StartDueRetriesAsync(Due.AddDays(1), 500, default), run.Id));
            Assert.Equal(BackupRetryOutcome.Cancelled, (await env.Store().RetryRunAsync(world.TenantId, run.Id, Due.AddDays(1), default)).Outcome);
            Assert.Null(await env.Store().ClaimNextAsync(world.TenantId, world.AgentId, Due.AddDays(1), Lease, default));
            Assert.Equal(attempts.Count, (await env.AttemptsAsync(run.Id)).Count);
        }
    }

    [Fact]
    public async Task RetryableFailureCompletedAfterCancellationWasRequestedEndsCancelledAndNeverSchedulesRetry()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = await StartAsync(store, world);
        var claim = await ClaimAsync(store, world, Now);
        var command = await StartAttemptAsync(store, claim, Now);
        var cancelling = await store.RequestCancellationAsync(world.TenantId, run.Id, Now.AddSeconds(2), default);
        Assert.Equal(BackupStatus.Running, cancelling!.Status);
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(command with { Now = FailedAt }, new Completion("Failed", "DatabaseUnavailable", null), default));

        var persisted = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.Cancelled, persisted.Status);
        Assert.Equal(BackupRetryState.None, persisted.RetryState);
        Assert.Null(persisted.NextAttemptAt);
        Assert.Equal(FailedAt, persisted.CompletedAt);
        var attempt = Assert.Single(persisted.Attempts);
        Assert.Equal(BackupStatus.Failed, attempt.Status);
        Assert.Equal("DatabaseUnavailable", attempt.TerminalReason);
        Assert.DoesNotContain(persisted.Facts, fact => fact.Kind is "BackupRetryScheduled" or "BackupRetryStarted" or "BackupRunFailed");
        Assert.Empty(Mine(await store.StartDueRetriesAsync(Due.AddDays(1), 500, default), run.Id));
        Assert.Equal(BackupRetryOutcome.Cancelled, (await store.RetryRunAsync(world.TenantId, run.Id, Due.AddDays(1), default)).Outcome);
        Assert.Null(await store.ClaimNextAsync(world.TenantId, world.AgentId, Due.AddDays(1), Lease, default));
    }

    [Fact]
    public async Task CancellationRacingRetryableFailureCompletionAlwaysEndsCancelledWithoutANewAttempt()
    {
        var env = new Env(fixture);
        for (var round = 0; round < 12; round++)
        {
            var world = await env.SeedAsync();
            var store = env.Store();
            var run = await StartAsync(store, world);
            var claim = await ClaimAsync(store, world, Now);
            var command = await StartAttemptAsync(store, claim, Now);
            var results = await RaceAsync<object?>(3, index => index == 1
                ? Box(env.Store().CompleteAttemptAsync(command with { Now = FailedAt }, new Completion("Failed", "DatabaseUnavailable", null), default))
                : Box(env.Store().RequestCancellationAsync(world.TenantId, run.Id, FailedAt, default)));
            // Completion first: RetryPending, which the cancellation then withdraws. Cancellation first: the failure ends the run Cancelled.
            Assert.Equal(BackupMutationResult.Accepted, Assert.Single(results.OfType<BackupMutationResult>()));
            Assert.Equal(2, results.OfType<BackupRun>().Count());
            var persisted = await env.LoadAsync(world, run.Id);
            Assert.Equal(BackupStatus.Cancelled, persisted.Status);
            Assert.Equal(BackupRetryState.None, persisted.RetryState);
            Assert.Null(persisted.NextAttemptAt);
            var attempt = Assert.Single(persisted.Attempts);
            Assert.Equal(BackupStatus.Failed, attempt.Status);
            Assert.Empty(Mine(await store.StartDueRetriesAsync(Due.AddDays(1), 500, default), run.Id));
            await AssertAttemptNumbersAsync(env, run.Id, 1);
        }
    }

    [Fact]
    public async Task StaleLeaseDetectedByTheStaleDetectorSchedulesARetryThatBecomesClaimable()
    {
        await using var env = await Env.IsolatedAsync();
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = await StartAsync(store, world);
        var claim = await ClaimAsync(store, world, Now);
        await StartAttemptAsync(store, claim, Now);

        Assert.Equal(0, await store.ExpireStaleAsync(Now.AddSeconds(119), default));
        Assert.Equal(BackupStatus.Running, (await env.LoadAsync(world, run.Id)).Status);
        var lostAt = Now + Lease;
        Assert.Equal(1, await store.ExpireStaleAsync(lostAt, default));
        var lost = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.RetryPending, lost.Status);
        Assert.Equal(BackupRetryState.Scheduled, lost.RetryState);
        Assert.Equal(lostAt + Delay, lost.NextAttemptAt);
        var attempt = Assert.Single(lost.Attempts);
        Assert.Equal(BackupStatus.AgentLost, attempt.Status);
        Assert.Equal("AgentLost", attempt.TerminalReason);
        Assert.Single(lost.Facts, fact => fact.Kind == "BackupRetryScheduled");
        Assert.Equal(0, await store.ExpireStaleAsync(lostAt.AddMinutes(1), default));

        var due = lostAt + Delay;
        Assert.Empty(await store.StartDueRetriesAsync(due.AddMilliseconds(-1), 500, default));
        Assert.Null(await store.ClaimNextAsync(world.TenantId, world.AgentId, due.AddMilliseconds(-1), Lease, default));
        Assert.Equal(run.Id, Assert.Single(await store.StartDueRetriesAsync(due, 500, default)).RunId);
        var retry = await ClaimAsync(store, world, due);
        Assert.Equal(run.Id, retry.Run.Id);
        Assert.Equal(2, retry.Attempt.Number);
        var command = await StartAttemptAsync(store, retry, due);
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(command, Success(due), default));
        var finished = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.Succeeded, finished.Status);
        Assert.Equal(new[] { BackupStatus.AgentLost, BackupStatus.Succeeded }, finished.Attempts.OrderBy(a => a.Number).Select(a => a.Status));
    }

    [Fact]
    public async Task StaleLeaseOfACancellingRunWithARetainedCheckpointEndsCancelledAndReleasesTheCheckpoint()
    {
        await using var env = await Env.IsolatedAsync();
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = await StartAsync(store, world);
        var claim = await ClaimAsync(store, world, Now);
        var command = await StartAttemptAsync(store, claim, Now);
        var destination = Assert.Single((await env.LoadAsync(world, run.Id)).Configuration.Destinations);
        Assert.Equal(BackupMutationResult.Accepted, await store.ReportProgressAsync(command, new BackupProgress("Uploading", 10)
            { Checkpoint = new() { StorageProvider = destination.StorageProvider, DestinationId = destination.StorageConnectionId!.Value, VerifiedByteOffset = 4096, ProviderState = "session" } }, default));
        Assert.Equal(BackupStatus.Running, (await store.RequestCancellationAsync(world.TenantId, run.Id, Now.AddSeconds(1), default))!.Status);

        Assert.Equal(1, await store.ExpireStaleAsync(Now + Lease, default));

        var ended = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.Cancelled, ended.Status);
        Assert.Equal(BackupStatus.AgentLost, ended.CurrentAttempt.Status);
        Assert.Empty(await store.GetCheckpointsAsync(world.TenantId, claim.Attempt.Id, default));
    }

    [Fact]
    public async Task StaleLeaseDetectedByTheAgentsNextPollSchedulesTheSameRetry()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = await StartAsync(store, world);
        var claim = await ClaimAsync(store, world, Now);
        await StartAttemptAsync(store, claim, Now);

        // The agent comes back after its lease ran out: the poll itself flips the dead attempt and schedules the retry.
        var pollAt = Now + Lease;
        Assert.Null(await store.ClaimNextAsync(world.TenantId, world.AgentId, pollAt, Lease, default));
        var lost = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.RetryPending, lost.Status);
        Assert.Equal(pollAt + Delay, lost.NextAttemptAt);
        Assert.Equal(BackupStatus.AgentLost, Assert.Single(lost.Attempts).Status);
        Assert.Single(Mine(await store.StartDueRetriesAsync(pollAt + Delay, 500, default), run.Id));
        Assert.Equal(2, (await ClaimAsync(store, world, pollAt + Delay)).Attempt.Number);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AgentLostAtTheLastAllowedAttemptEndsTheRunAgentLostAndExhausted(int maxAttempts)
    {
        await using var env = await Env.IsolatedAsync();
        var store = env.Store();
        var world = await env.SeedAsync(Policy with { MaxAttempts = maxAttempts });
        var run = await StartAsync(store, world);
        var at = Now;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var claim = await ClaimAsync(store, world, at);
            Assert.Equal(attempt, claim.Attempt.Number);
            await StartAttemptAsync(store, claim, at);
            at += Lease;
            Assert.Equal(1, await store.ExpireStaleAsync(at, default));
            if (attempt == maxAttempts) break;
            var scheduled = await env.LoadAsync(world, run.Id);
            Assert.Equal(BackupStatus.RetryPending, scheduled.Status);
            at = scheduled.NextAttemptAt!.Value;
            Assert.Single(await store.StartDueRetriesAsync(at, 500, default));
        }

        var terminal = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.AgentLost, terminal.Status);
        Assert.Equal("AgentLost", terminal.TerminalReason);
        Assert.Equal(BackupRetryState.Exhausted, terminal.RetryState);
        Assert.Null(terminal.NextAttemptAt);
        Assert.Equal(at, terminal.CompletedAt);
        Assert.Equal(maxAttempts, terminal.Attempts.Count);
        Assert.All(terminal.Attempts, attempt => Assert.Equal(BackupStatus.AgentLost, attempt.Status));
        Assert.Equal(maxAttempts - 1, terminal.Facts.Count(fact => fact.Kind == "BackupRetryScheduled"));
        Assert.Single(terminal.Facts, fact => fact.Kind == "BackupRetryExhausted");
        Assert.Equal(BackupRetryOutcome.AttemptsExhausted, (await store.RetryRunAsync(world.TenantId, run.Id, at.AddHours(1), default)).Outcome);
        Assert.Empty(await store.StartDueRetriesAsync(at.AddDays(1), 500, default));
        Assert.Null(await store.ClaimNextAsync(world.TenantId, world.AgentId, at.AddDays(1), Lease, default));
        await AssertAttemptNumbersAsync(env, run.Id, maxAttempts);
    }

    [Fact]
    public async Task RetryableFailuresUntilMaxAttemptsEndFailedExhaustedAndManualRetryIsRefused()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = await StartAsync(store, world);
        var at = Now;
        for (var attempt = 1; attempt <= Policy.MaxAttempts; attempt++)
        {
            var claim = await ClaimAsync(store, world, at);
            Assert.Equal(attempt, claim.Attempt.Number);
            await FailAsync(store, claim, "StorageUnavailable", at);
            if (attempt == Policy.MaxAttempts) break;
            var scheduled = await env.LoadAsync(world, run.Id);
            Assert.Equal(BackupStatus.RetryPending, scheduled.Status);
            // Exponential: 30s after the first failure, 60s after the second.
            Assert.Equal(at + Delay * Math.Pow(2, attempt - 1), scheduled.NextAttemptAt);
            at = scheduled.NextAttemptAt!.Value;
            Assert.Single(Mine(await store.StartDueRetriesAsync(at, 500, default), run.Id));
        }

        var terminal = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.Failed, terminal.Status);
        Assert.Equal("StorageUnavailable", terminal.TerminalReason);
        Assert.Equal(BackupRetryState.Exhausted, terminal.RetryState);
        Assert.Null(terminal.NextAttemptAt);
        Assert.Equal(at, terminal.CompletedAt);
        Assert.Equal(Policy.MaxAttempts, terminal.CurrentAttemptNumber);
        Assert.All(terminal.Attempts, attempt => Assert.Equal(BackupStatus.Failed, attempt.Status));
        Assert.Equal(2, terminal.Facts.Count(fact => fact.Kind == "BackupRetryScheduled"));
        Assert.Equal(2, terminal.Facts.Count(fact => fact.Kind == "BackupRetryStarted"));
        Assert.Single(terminal.Facts, fact => fact.Kind == "BackupRetryExhausted");
        Assert.Single(terminal.Facts, fact => fact.Kind == "BackupRunFailed");
        Assert.Equal(BackupRetryOutcome.AttemptsExhausted, (await store.RetryRunAsync(world.TenantId, run.Id, at.AddHours(1), default)).Outcome);
        Assert.Empty(Mine(await store.StartDueRetriesAsync(at.AddDays(1), 500, default), run.Id));
        await AssertAttemptNumbersAsync(env, run.Id, Policy.MaxAttempts);
    }

    [Theory]
    [InlineData("DatabaseUnavailable", true)]
    [InlineData("OperationTimedOut", true)]
    [InlineData("StorageUnavailable", true)]
    [InlineData("ReplicaVerificationFailed", true)]
    [InlineData("PreCheckFailed", false)]
    [InlineData("SqlBackupFailed", false)]
    [InlineData("VerificationFailed", false)]
    [InlineData("HashFailed", false)]
    [InlineData("StorageWriteFailed", false)]
    [InlineData("InternalError", false)]
    [InlineData("DatabaseNotFound", false)]
    [InlineData("InvalidConfiguration", false)]
    [InlineData("UnsupportedCapability", false)]
    [InlineData("AuthenticationFailed", false)]
    [InlineData("PermissionDenied", false)]
    [InlineData("StagingInvalid", false)]
    public async Task ReportedFailureCodeDecidesBetweenAScheduledRetryAndATerminalFailure(string code, bool retryable)
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = await StartAsync(store, world);
        await FailAsync(store, await ClaimAsync(store, world, Now), code, FailedAt);

        var persisted = await env.LoadAsync(world, run.Id);
        Assert.Equal(code, Assert.Single(persisted.Attempts).TerminalReason);
        if (retryable)
        {
            Assert.Equal(BackupStatus.RetryPending, persisted.Status);
            Assert.Equal(BackupRetryState.Scheduled, persisted.RetryState);
            Assert.Equal(Due, persisted.NextAttemptAt);
            Assert.Null(persisted.CompletedAt);
            Assert.Single(Mine(await store.StartDueRetriesAsync(Due, 500, default), run.Id));
        }
        else
        {
            Assert.Equal(BackupStatus.Failed, persisted.Status);
            Assert.Equal(code, persisted.TerminalReason);
            Assert.Equal(BackupRetryState.NotRetryable, persisted.RetryState);
            Assert.Null(persisted.NextAttemptAt);
            Assert.Equal(FailedAt, persisted.CompletedAt);
            Assert.DoesNotContain(persisted.Facts, fact => fact.Kind is "BackupRetryScheduled" or "BackupRetryExhausted");
            Assert.Empty(Mine(await store.StartDueRetriesAsync(Due.AddDays(1), 500, default), run.Id));
            await AssertAttemptNumbersAsync(env, run.Id, 1);
        }
    }

    [Theory]
    [InlineData("AgentLost")]
    [InlineData("Cancelled")]
    [InlineData("NotAFailureCode")]
    [InlineData(null)]
    public async Task AgentCannotReportBackendOnlyOrUnknownFailureCodesAndTheAttemptKeepsRunning(string? code)
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = await StartAsync(store, world);
        var command = await StartAttemptAsync(store, await ClaimAsync(store, world, Now), Now);
        Assert.Equal(BackupMutationResult.Conflict, await store.CompleteAttemptAsync(command with { Now = FailedAt }, new Completion("Failed", code, null), default));

        var persisted = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.Running, persisted.Status);
        Assert.Equal(BackupStatus.Running, Assert.Single(persisted.Attempts).Status);
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(command with { Now = FailedAt }, new Completion("Failed", "DatabaseUnavailable", null), default));
        Assert.Equal(BackupStatus.RetryPending, (await env.LoadAsync(world, run.Id)).Status);
    }

    [Fact]
    public async Task NonRetryableFailureAllowsManualRetriesOnTheSameRunUntilTheSharedAttemptBudgetIsSpent()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = await FailedTerminallyAsync(store, world);

        var at = FailedAt;
        for (var attempt = 2; attempt <= Policy.MaxAttempts; attempt++)
        {
            at = at.AddMinutes(1);
            var retried = await store.RetryRunAsync(world.TenantId, run.Id, at, default);
            Assert.Equal(BackupRetryOutcome.Started, retried.Outcome);
            Assert.Equal(run.Id, retried.Run!.Id);
            var reopened = await env.LoadAsync(world, run.Id);
            Assert.Equal(BackupStatus.Pending, reopened.Status);
            Assert.Equal(BackupRetryState.None, reopened.RetryState);
            Assert.Null(reopened.CompletedAt);
            Assert.Null(reopened.TerminalReason);
            Assert.Equal(attempt, reopened.CurrentAttemptNumber);
            // The operator's retry is immediate: no backoff applies.
            Assert.Equal(at, reopened.CurrentAttempt.EligibleAt);
            var claim = await ClaimAsync(store, world, at);
            Assert.Equal(run.Id, claim.Run.Id);
            Assert.Equal(attempt, claim.Attempt.Number);
            await FailAsync(store, claim, "SqlBackupFailed", at);
            var failedAgain = await env.LoadAsync(world, run.Id);
            Assert.Equal(BackupStatus.Failed, failedAgain.Status);
            Assert.Equal(BackupRetryState.NotRetryable, failedAgain.RetryState);
        }

        Assert.Equal(BackupRetryOutcome.AttemptsExhausted, (await store.RetryRunAsync(world.TenantId, run.Id, at.AddMinutes(1), default)).Outcome);
        await AssertAttemptNumbersAsync(env, run.Id, Policy.MaxAttempts);
        Assert.Equal(BackupStatus.Failed, (await env.LoadAsync(world, run.Id)).Status);
    }

    [Theory]
    [InlineData("succeeded", BackupRetryOutcome.Succeeded)]
    [InlineData("cancelled-while-pending", BackupRetryOutcome.Cancelled)]
    [InlineData("cancelled-while-retry-pending", BackupRetryOutcome.Cancelled)]
    [InlineData("pending", BackupRetryOutcome.InProgress)]
    [InlineData("leased", BackupRetryOutcome.InProgress)]
    [InlineData("running", BackupRetryOutcome.InProgress)]
    [InlineData("running-cancellation-requested", BackupRetryOutcome.InProgress)]
    public async Task ManualRetryIsRefusedWithoutSideEffectsWhenNothingCanOrShouldBeRetried(string state, BackupRetryOutcome expected)
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = await StartAsync(store, world);
        switch (state)
        {
            case "succeeded":
                var running = await StartAttemptAsync(store, await ClaimAsync(store, world, Now), Now);
                Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(running, Success(FailedAt), default));
                break;
            case "cancelled-while-pending":
                await store.RequestCancellationAsync(world.TenantId, run.Id, Now, default);
                break;
            case "cancelled-while-retry-pending":
                await FailAsync(store, await ClaimAsync(store, world, Now), "DatabaseUnavailable", FailedAt);
                await store.RequestCancellationAsync(world.TenantId, run.Id, FailedAt, default);
                break;
            case "leased":
                await ClaimAsync(store, world, Now);
                break;
            case "running":
                await StartAttemptAsync(store, await ClaimAsync(store, world, Now), Now);
                break;
            case "running-cancellation-requested":
                await StartAttemptAsync(store, await ClaimAsync(store, world, Now), Now);
                await store.RequestCancellationAsync(world.TenantId, run.Id, FailedAt, default);
                break;
        }
        var before = await env.LoadAsync(world, run.Id);

        var result = await store.RetryRunAsync(world.TenantId, run.Id, Due, default);

        Assert.Equal(expected, result.Outcome);
        var after = await env.LoadAsync(world, run.Id);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Attempts.Count, after.Attempts.Count);
        Assert.Equal(before.Facts.Count, after.Facts.Count);
        Assert.Equal(before.NextAttemptAt, after.NextAttemptAt);
    }

    [Fact]
    public async Task ManualRetryOfAnotherTenantsOrAnUnknownRunIsNotFoundAndChangesNothing()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var other = await env.SeedAsync();
        var run = await FailedTerminallyAsync(store, world);

        var crossTenant = await store.RetryRunAsync(other.TenantId, run.Id, Due, default);
        Assert.Equal(BackupRetryOutcome.NotFound, crossTenant.Outcome);
        Assert.Null(crossTenant.Run);
        Assert.Equal(BackupRetryOutcome.NotFound, (await store.RetryRunAsync(world.TenantId, Guid.NewGuid(), Due, default)).Outcome);
        Assert.Equal(BackupStatus.Failed, (await env.LoadAsync(world, run.Id)).Status);
        await AssertAttemptNumbersAsync(env, run.Id, 1);
    }

    [Fact]
    public async Task ArtifactBelongsOnlyToTheSuccessfulAttemptOfARetriedRun()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = await StartAsync(store, world);
        var first = await ClaimAsync(store, world, Now);
        var firstCommand = await StartAttemptAsync(store, first, Now);
        // A failed attempt can never publish an artifact, however the agent words the result.
        var forged = new Completion("Failed", "DatabaseUnavailable", Success(FailedAt).Artifact);
        Assert.Equal(BackupMutationResult.Conflict, await store.CompleteAttemptAsync(firstCommand with { Now = FailedAt }, forged, default));
        Assert.Equal(BackupStatus.Running, (await env.LoadAsync(world, run.Id)).Status);
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(firstCommand with { Now = FailedAt }, new Completion("Failed", "DatabaseUnavailable", null), default));
        Assert.Single(Mine(await store.StartDueRetriesAsync(Due, 500, default), run.Id));
        var second = await ClaimAsync(store, world, Due);
        var secondCommand = await StartAttemptAsync(store, second, Due);
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(secondCommand, Success(Due), default));

        var persisted = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.Succeeded, persisted.Status);
        Assert.Equal(BackupRetryState.None, persisted.RetryState);
        Assert.Null(persisted.TerminalReason);
        Assert.Empty(persisted.Attempts.Single(attempt => attempt.Number == 1).Artifacts);
        var artifact = Assert.Single(persisted.Attempts.Single(attempt => attempt.Number == 2).Artifacts);
        Assert.Equal(second.Attempt.Id, artifact.AttemptId);
        Assert.Single(artifact.Replicas);
        Assert.Equal(second.Attempt.Id, Assert.Single(await env.ArtifactOwnersAsync(run.Id)));
        Assert.Single(persisted.Facts, fact => fact.Kind == "BackupRunSucceeded");
    }

    [Fact]
    public async Task ReplayedFailureCompletionIsIdempotentWhileRetryPendingAndStaleOnceTheRetryAttemptExists()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = await StartAsync(store, world);
        var claim = await ClaimAsync(store, world, Now);
        var command = await StartAttemptAsync(store, claim, Now);
        var failure = new Completion("Failed", "DatabaseUnavailable", null);
        var results = await RaceAsync(8, _ => env.Store().CompleteAttemptAsync(command with { Now = FailedAt }, failure, default));
        Assert.All(results, result => Assert.Equal(BackupMutationResult.Accepted, result));

        var scheduled = await env.LoadAsync(world, run.Id);
        Assert.Equal(Due, scheduled.NextAttemptAt);
        Assert.Single(scheduled.Facts, fact => fact.Kind == "BackupRetryScheduled");
        // A late retransmission must not push the schedule out, and a contradictory late result must not flip it.
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(command with { Now = FailedAt.AddMinutes(10) }, failure, default));
        Assert.Equal(BackupMutationResult.Conflict, await store.CompleteAttemptAsync(command with { Now = FailedAt.AddMinutes(10) }, new Completion("Failed", "PreCheckFailed", null), default));
        var unchanged = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.RetryPending, unchanged.Status);
        Assert.Equal(Due, unchanged.NextAttemptAt);
        Assert.Single(unchanged.Facts, fact => fact.Kind == "BackupRetryScheduled");

        Assert.Single(Mine(await store.StartDueRetriesAsync(Due, 500, default), run.Id));
        Assert.Equal(BackupMutationResult.Conflict, await store.CompleteAttemptAsync(command with { Now = Due }, failure, default));
        await AssertAttemptNumbersAsync(env, run.Id, 2);
    }

    [Fact]
    public async Task StaleAttemptCannotTouchTheRunWhileRetryIsPendingNorOnceTheRetryAttemptExistsEvenWithItsOwnToken()
    {
        await using var env = await Env.IsolatedAsync();
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = await StartAsync(store, world);
        var first = await ClaimAsync(store, world, Now);
        var old = await StartAttemptAsync(store, first, Now);
        var lostAt = Now + Lease;
        Assert.Equal(1, await store.ExpireStaleAsync(lostAt, default));
        var due = lostAt + Delay;

        // Phase 1: attempt 1 is AgentLost and the retry is merely scheduled.
        var lateAt = lostAt.AddSeconds(5);
        await AssertRejectedAsync(store, old with { Now = lateAt });
        var scheduled = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.RetryPending, scheduled.Status);
        Assert.Equal(due, scheduled.NextAttemptAt);
        Assert.Equal(BackupStatus.AgentLost, Assert.Single(scheduled.Attempts).Status);
        Assert.Empty(await env.ArtifactOwnersAsync(run.Id));

        // Phase 2: attempt 2 exists but is not claimed yet.
        Assert.Single(await store.StartDueRetriesAsync(due, 500, default));
        var pending = (await env.LoadAsync(world, run.Id)).CurrentAttempt;
        await AssertRejectedAsync(store, old with { Now = due });
        Assert.Equal(AttemptState.Of(pending), AttemptState.Of((await env.LoadAsync(world, run.Id)).CurrentAttempt));

        // Phase 3: attempt 2 is running with its own token.
        var second = await ClaimAsync(store, world, due);
        var current = await StartAttemptAsync(store, second, due);
        var before = AttemptState.Of((await env.LoadAsync(world, run.Id)).CurrentAttempt);
        await AssertRejectedAsync(store, old with { Now = due.AddSeconds(1) });
        // Mixing the two identities never works either: new attempt with the old token, old attempt with the new token.
        await AssertRejectedAsync(store, current with { LeaseToken = old.LeaseToken, Now = due.AddSeconds(1) });
        await AssertRejectedAsync(store, current with { AttemptId = old.AttemptId, Now = due.AddSeconds(1) });
        var during = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.Running, during.Status);
        Assert.Equal(before, AttemptState.Of(during.CurrentAttempt));
        Assert.Empty(await env.ArtifactOwnersAsync(run.Id));

        // The legitimate owner is unaffected and finishes the run.
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(current with { Now = due.AddSeconds(2) }, Success(due), default));
        await AssertRejectedAsync(store, old with { Now = due.AddSeconds(3) });
        var finished = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.Succeeded, finished.Status);
        Assert.Equal(2, finished.CurrentAttemptNumber);
        Assert.Equal(BackupStatus.AgentLost, finished.Attempts.Single(attempt => attempt.Number == 1).Status);
        Assert.Equal(second.Attempt.Id, Assert.Single(await env.ArtifactOwnersAsync(run.Id)));
    }

    [Fact]
    public async Task CallbacksWithWrongTokenAgentTenantExpiredOrTerminalLeaseAreRejectedWithoutSideEffects()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var other = await env.SeedAsync();
        var sameTenantAgent = await env.AddAgentAsync(world.TenantId);
        var run = await StartAsync(store, world);
        var command = await StartAttemptAsync(store, await ClaimAsync(store, world, Now), Now);
        var untouched = AttemptState.Of((await env.LoadAsync(world, run.Id)).CurrentAttempt);
        var at = Now.AddSeconds(10);

        await AssertRejectedAsync(store, command with { LeaseToken = Guid.NewGuid(), Now = at });
        await AssertRejectedAsync(store, command with { LeaseToken = Guid.Empty, Now = at });
        // Another agent of the SAME tenant and another tenant's agent both see no such attempt at all.
        await AssertRejectedAsync(store, command with { AgentId = sameTenantAgent, Now = at }, BackupMutationResult.NotFound);
        await AssertRejectedAsync(store, command with { TenantId = other.TenantId, Now = at }, BackupMutationResult.NotFound);
        await AssertRejectedAsync(store, command with { TenantId = other.TenantId, AgentId = other.AgentId, Now = at }, BackupMutationResult.NotFound);
        // An expired lease cannot be renewed back to life, and acting on it changes nothing until the stale detection runs.
        await AssertRejectedAsync(store, command with { Now = Now + Lease });
        await AssertRejectedAsync(store, command with { Now = Now + Lease + TimeSpan.FromHours(1) });
        Assert.Equal(untouched, AttemptState.Of((await env.LoadAsync(world, run.Id)).CurrentAttempt));
        Assert.Equal(BackupStatus.Running, (await env.LoadAsync(world, run.Id)).Status);

        // A live lease is still renewable, and once the attempt is terminal its lease is dead for every callback.
        var renewed = await store.RenewLeaseAsync(command with { Now = at }, Lease, default);
        Assert.Equal(at + Lease, renewed!.LeaseExpiresAt);
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(command with { Now = at }, Success(at), default));
        var terminal = AttemptState.Of((await env.LoadAsync(world, run.Id)).CurrentAttempt);
        var afterAt = at.AddSeconds(1);
        Assert.Equal(BackupMutationResult.Conflict, await store.StartAttemptAsync(command with { Now = afterAt }, default));
        Assert.Equal(BackupMutationResult.Conflict, await store.ReportProgressAsync(command with { Now = afterAt }, new BackupProgress("Uploading", 99), default));
        Assert.Null(await store.RenewLeaseAsync(command with { Now = afterAt }, Lease, default));
        Assert.Equal(BackupMutationResult.Conflict, await store.CompleteAttemptAsync(command with { Now = afterAt }, new Completion("Failed", "InternalError", null), default));
        Assert.Equal(terminal, AttemptState.Of((await env.LoadAsync(world, run.Id)).CurrentAttempt));
    }

    [Theory]
    [InlineData(BackupStatus.Succeeded)]
    [InlineData(BackupStatus.Failed)]
    [InlineData(BackupStatus.Cancelled)]
    [InlineData(BackupStatus.AgentLost)]
    public async Task DatabaseRejectsChangingATerminalAttemptsOutcome(BackupStatus terminalStatus)
    {
        var env = new Env(fixture);
        var terminal = await TerminalAsync(env, terminalStatus);
        var other = terminalStatus == BackupStatus.Failed ? BackupStatus.Succeeded : BackupStatus.Failed;
        var before = AttemptState.Of(Assert.Single(await env.AttemptsAsync(terminal.RunId)));
        await using var context = env.Context();

        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation,
            $"UPDATE backup_run_attempts SET \"Status\" = {(int)other} WHERE \"Id\" = {terminal.AttemptId}");
        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation,
            $"UPDATE backup_run_attempts SET \"TerminalReason\" = 'Edited' WHERE \"Id\" = {terminal.AttemptId}");
        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation,
            $"UPDATE backup_run_attempts SET \"CompletedAt\" = {Now.AddDays(1)} WHERE \"Id\" = {terminal.AttemptId}");

        Assert.Equal(before, AttemptState.Of(Assert.Single(await env.AttemptsAsync(terminal.RunId))));
    }

    [Theory]
    [InlineData(BackupStatus.Succeeded, BackupStatus.Failed, false)]
    [InlineData(BackupStatus.Succeeded, BackupStatus.Cancelled, false)]
    [InlineData(BackupStatus.Succeeded, BackupStatus.Pending, false)]
    [InlineData(BackupStatus.Cancelled, BackupStatus.Pending, false)]
    [InlineData(BackupStatus.Cancelled, BackupStatus.Failed, false)]
    [InlineData(BackupStatus.Failed, BackupStatus.Cancelled, false)]
    [InlineData(BackupStatus.Failed, BackupStatus.Succeeded, false)]
    [InlineData(BackupStatus.AgentLost, BackupStatus.Cancelled, false)]
    [InlineData(BackupStatus.Failed, BackupStatus.Pending, true)]
    [InlineData(BackupStatus.AgentLost, BackupStatus.Pending, true)]
    public async Task DatabaseOnlyLetsAFailedOrAgentLostRunBeReopenedAndNeverMovesSucceededOrCancelledRuns(BackupStatus from, BackupStatus to, bool allowed)
    {
        var env = new Env(fixture);
        var terminal = await TerminalAsync(env, from);
        await using var context = env.Context();
        // Reopening also clears the completion time, otherwise the terminal/completed check (not the guard) would be what rejects it.
        FormattableString move = to == BackupStatus.Pending
            ? (FormattableString)$"UPDATE backup_runs SET \"Status\" = 1, \"CompletedAt\" = NULL WHERE \"Id\" = {terminal.RunId}"
            : $"UPDATE backup_runs SET \"Status\" = {(int)to} WHERE \"Id\" = {terminal.RunId}";

        if (allowed) Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync(move));
        else await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation, move);

        Assert.Equal(allowed ? to : from, (await env.LoadAsync(terminal.World, terminal.RunId)).Status);
    }

    [Fact]
    public async Task DatabaseRejectsDuplicateAttemptNumbersBudgetOverflowAndInconsistentRetrySchedules()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var scheduled = (await ScheduleRetryAsync(store, world)).Run;
        var plain = await env.SeedAsync();
        var pending = await StartAsync(store, plain);
        Assert.Single(Mine(await store.StartDueRetriesAsync(Due, 500, default), scheduled.Id));
        var retryAttempt = (await env.LoadAsync(world, scheduled.Id)).CurrentAttempt;
        await using var context = env.Context();

        // (Run, Number) is unique: a second number 2 is impossible however the row is written.
        await AssertViolationAsync(context, PostgresErrorCodes.UniqueViolation,
            $"INSERT INTO backup_run_attempts (\"Id\", \"TenantId\", \"RunId\", \"AgentId\", \"Number\", \"Status\", \"CreatedAt\", \"EligibleAt\") SELECT {Guid.NewGuid()}, \"TenantId\", \"RunId\", \"AgentId\", \"Number\", 1, \"CreatedAt\", \"EligibleAt\" FROM backup_run_attempts WHERE \"Id\" = {retryAttempt.Id}",
            "IX_backup_run_attempts_TenantId_RunId_Number");
        await AssertAttemptNumbersAsync(env, scheduled.Id, 2);

        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation,
            $"UPDATE backup_runs SET \"CurrentAttemptNumber\" = \"RetryMaxAttempts\" + 1 WHERE \"Id\" = {scheduled.Id}", "ck_backup_runs_attempt_budget");
        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation,
            $"UPDATE backup_runs SET \"RetryMaxAttempts\" = 11 WHERE \"Id\" = {scheduled.Id}", "ck_backup_runs_retry_policy");
        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation,
            $"UPDATE backup_runs SET \"Status\" = 10 WHERE \"Id\" = {scheduled.Id}", "ck_backup_runs_status");
        // A run waits for a retry exactly while it is RetryPending: schedule and state are tied to the status.
        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation,
            $"UPDATE backup_runs SET \"NextAttemptAt\" = {Due} WHERE \"Id\" = {pending.Id}", "ck_backup_runs_retry_schedule");
        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation,
            $"UPDATE backup_runs SET \"RetryState\" = 1 WHERE \"Id\" = {pending.Id}", "ck_backup_runs_retry_state");
        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation,
            $"UPDATE backup_jobs SET \"OverlapPolicy\" = 'Sometimes' WHERE \"Id\" = {world.JobId}", "ck_backup_job_policy");
        Assert.Equal(2, (await env.LoadAsync(world, scheduled.Id)).CurrentAttemptNumber);
        Assert.Equal(BackupStatus.Pending, (await env.LoadAsync(plain, pending.Id)).Status);
    }

    [Fact]
    public async Task RunKeepsTheRetryPolicyItWasCreatedWithWhenTheJobIsEditedLater()
    {
        var env = new Env(fixture);
        var store = env.Store();
        var world = await env.SeedAsync();
        var run = await StartAsync(store, world, "before-edit");
        Assert.Equal(Policy, (await env.LoadAsync(world, run.Id)).RetryPolicy);
        await using (var context = env.Context())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE backup_jobs SET \"RetryMaxAttempts\" = 1, \"RetryInitialDelay\" = interval '5 seconds' WHERE \"Id\" = {world.JobId}");
        }
        var job = await store.GetJobAsync(world.TenantId, world.JobId, default);
        Assert.Equal(1, job!.RetryPolicy.MaxAttempts);

        // The execution in flight retries according to its snapshot (3 attempts, 30s) ...
        await FailAsync(store, await ClaimAsync(store, world, Now), "OperationTimedOut", FailedAt);
        var scheduled = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.RetryPending, scheduled.Status);
        Assert.Equal(Due, scheduled.NextAttemptAt);
        // ... while a run created after the edit snapshots the new policy and cannot retry at all.
        var next = await StartAsync(store, world, "after-edit", FailedAt);
        Assert.Equal(1, (await env.LoadAsync(world, next.Id)).RetryPolicy.MaxAttempts);
        await FailAsync(store, await ClaimAsync(store, world, FailedAt), "OperationTimedOut", FailedAt.AddSeconds(1));
        var exhausted = await env.LoadAsync(world, next.Id);
        Assert.Equal(BackupStatus.Failed, exhausted.Status);
        Assert.Equal(BackupRetryState.Exhausted, exhausted.RetryState);
    }

    [Fact]
    public async Task DueRetriesStartOldestDueFirstAndHonourTheBatchLimit()
    {
        await using var env = await Env.IsolatedAsync();
        var store = env.Store();
        var runs = new List<Guid>();
        // Failures at +30s, +10s, +20s: due order is the second, the third, then the first.
        foreach (var failedAfter in new[] { 30, 10, 20 })
            runs.Add((await ScheduleRetryAsync(store, await env.SeedAsync(), failedAt: Now.AddSeconds(failedAfter))).Run.Id);
        var allDue = Now.AddMinutes(10);

        Assert.Equal(new[] { runs[1], runs[2] }, (await store.StartDueRetriesAsync(allDue, 2, default)).Select(item => item.RunId));
        Assert.Equal(new[] { runs[0] }, (await store.StartDueRetriesAsync(allDue, 2, default)).Select(item => item.RunId));
        Assert.Empty(await store.StartDueRetriesAsync(allDue, 2, default));
    }

    [Fact]
    public async Task RetryLifecycleUnderTheDefaultSkipOverlapPolicyNeverBlocksItsOwnRun()
    {
        var env = new Env(fixture);
        var store = env.Store();
        // Skip is the production default: the partial unique index (one active run per job) is live during every transition below.
        var world = await env.SeedAsync(overlap: BackupOverlapPolicy.Skip);
        var run = await StartAsync(store, world);
        await FailAsync(store, await ClaimAsync(store, world, Now), "DatabaseUnavailable", FailedAt);
        Assert.Equal(BackupStatus.RetryPending, (await env.LoadAsync(world, run.Id)).Status);
        Assert.Single(Mine(await store.StartDueRetriesAsync(Due, 500, default), run.Id));
        var second = await ClaimAsync(store, world, Due);
        await FailAsync(store, second, "PreCheckFailed", Due);
        Assert.Equal(BackupStatus.Failed, (await env.LoadAsync(world, run.Id)).Status);

        var reopened = await store.RetryRunAsync(world.TenantId, run.Id, Due.AddMinutes(1), default);
        Assert.Equal(BackupRetryOutcome.Started, reopened.Outcome);
        var third = await ClaimAsync(store, world, Due.AddMinutes(1));
        var command = await StartAttemptAsync(store, third, Due.AddMinutes(1));
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(command, Success(Due.AddMinutes(1)), default));
        var finished = await env.LoadAsync(world, run.Id);
        Assert.Equal(BackupStatus.Succeeded, finished.Status);
        Assert.Equal(3, finished.CurrentAttemptNumber);
        await AssertAttemptNumbersAsync(env, run.Id, 3);
    }

    // ----- helpers -----

    private sealed record World(TenantId TenantId, AgentId AgentId, Guid JobId);
    private sealed record Terminal(World World, Guid RunId, Guid AttemptId);
    private sealed record Scheduled(BackupRun Run, DateTimeOffset DueAt);
    private sealed record AttemptState(BackupStatus Status, string? Reason, Guid? Token, DateTimeOffset? LeaseExpiresAt, string? Stage, double? Percent, DateTimeOffset? CompletedAt)
    {
        public static AttemptState Of(BackupRunAttempt attempt) =>
            new(attempt.Status, attempt.TerminalReason, attempt.LeaseToken, attempt.LeaseExpiresAt, attempt.ProgressStage, attempt.ProgressPercent, attempt.CompletedAt);
    }

    /// <summary>One database plus the data-access helpers of this suite; <see cref="IsolatedAsync"/> owns a private database for tests that scan globally.</summary>
    private sealed class Env(PostgreSqlFixture database, bool owned = false) : IAsyncDisposable
    {
        public static async Task<Env> IsolatedAsync()
        {
            var isolated = new PostgreSqlFixture();
            await isolated.InitializeAsync();
            return new Env(isolated, true);
        }
        public ValueTask DisposeAsync() => owned ? new ValueTask(database.DisposeAsync()) : default;
        public PostgreSqlBackupRunStore Store(Func<double>? jitter = null) => StoreFor(database.ConnectionString, jitter);
        public static PostgreSqlBackupRunStore StoreFor(string connectionString, Func<double>? jitter = null) =>
            new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options, AdmitAllBackupAdmission.Instance, Limits, jitter ?? (() => 0));

        public async Task<World> SeedAsync(BackupRetryPolicy? policy = null, BackupOverlapPolicy overlap = BackupOverlapPolicy.Queue)
        {
            var tenantId = TenantId.New();
            await using (var context = database.CreatePlatformContext())
            {
                context.Tenants.Add(new Tenant(tenantId, "Retry tenant"));
                await context.SaveChangesAsync();
            }
            var agentId = await AddAgentAsync(tenantId);
            var job = (await Store().CreateJobAsync(new BackupJob(Guid.NewGuid(), tenantId, "Database", agentId, Configuration(), Now, policy ?? Policy, overlap), default))!;
            return new World(tenantId, agentId, job.Id);
        }
        public async Task<AgentId> AddAgentAsync(TenantId tenantId)
        {
            var agentId = AgentId.New();
            await using var context = database.CreatePlatformContext();
            var agent = new BackupPlatform.Domain.Agents.Agent(agentId, tenantId, new AgentSoftware(AgentPlatform.Linux, new Version(1, 0), new AgentProtocolVersion(1, 0)));
            agent.RecordEnrollment();
            context.Agents.Add(agent);
            context.AgentCapabilities.Add(new BackupPlatform.Agents.Capabilities.AgentCapabilityRecord(agentId, tenantId,
                BackupPlatform.Contracts.Backups.BackupTaskProtocol.MultiDestinationCapability, BackupPlatform.Domain.Capabilities.CapabilitySupport.Supported, null, "fixture", Now));
            await context.SaveChangesAsync();
            return agentId;
        }
        public async Task<BackupRun> LoadAsync(World world, Guid runId) => (await Store().GetRunAsync(world.TenantId, runId, default))!;
        public async Task<List<BackupRunAttempt>> AttemptsAsync(Guid runId)
        {
            await using var context = database.CreatePlatformContext();
            return await context.BackupRunAttempts.AsNoTracking().Where(attempt => attempt.RunId == runId).OrderBy(attempt => attempt.Number).ToListAsync();
        }
        public PlatformDbContext Context() => database.CreatePlatformContext();
        public async Task<List<Guid>> ArtifactOwnersAsync(Guid runId)
        {
            var attemptIds = (await AttemptsAsync(runId)).Select(attempt => attempt.Id).ToList();
            await using var context = database.CreatePlatformContext();
            return await context.BackupArtifacts.AsNoTracking().Where(artifact => attemptIds.Contains(artifact.AttemptId)).Select(artifact => artifact.AttemptId).ToListAsync();
        }
    }

    private static async Task<BackupRun> StartAsync(IBackupRunStore store, World world, string key = "run", DateTimeOffset? now = null)
    {
        var result = await store.StartRunAsync(world.TenantId, world.JobId, key, now ?? Now, default);
        Assert.Equal(BackupStartOutcome.Started, result.Outcome);
        return result.Run!;
    }

    private static async Task<BackupClaim> ClaimAsync(IBackupRunStore store, World world, DateTimeOffset now)
    {
        var claim = await store.ClaimNextAsync(world.TenantId, world.AgentId, now, Lease, default);
        Assert.NotNull(claim);
        return claim;
    }

    private static async Task<BackupLeaseCommand> StartAttemptAsync(IBackupRunStore store, BackupClaim claim, DateTimeOffset now)
    {
        var command = Command(claim, now);
        Assert.Equal(BackupMutationResult.Accepted, await store.StartAttemptAsync(command, default));
        return command;
    }

    private static async Task FailAsync(IBackupRunStore store, BackupClaim claim, string code, DateTimeOffset at)
    {
        var command = await StartAttemptAsync(store, claim, at);
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(command, new Completion("Failed", code, null), default));
    }

    /// <summary>Starts a run, runs attempt 1 and fails it retryably at <paramref name="failedAt"/> so the run waits for its backoff.</summary>
    private static async Task<Scheduled> ScheduleRetryAsync(IBackupRunStore store, World world, string key = "scheduled", DateTimeOffset? failedAt = null)
    {
        var at = failedAt ?? FailedAt;
        var run = await StartAsync(store, world, key);
        await FailAsync(store, await ClaimAsync(store, world, Now), "DatabaseUnavailable", at);
        var persisted = await store.GetRunAsync(world.TenantId, run.Id, default);
        Assert.Equal(BackupStatus.RetryPending, persisted!.Status);
        return new Scheduled(run, at + Delay);
    }

    private static async Task<BackupRun> FailedTerminallyAsync(IBackupRunStore store, World world)
    {
        var run = await StartAsync(store, world);
        await FailAsync(store, await ClaimAsync(store, world, Now), "PreCheckFailed", FailedAt);
        Assert.Equal(BackupStatus.Failed, (await store.GetRunAsync(world.TenantId, run.Id, default))!.Status);
        return run;
    }

    private static async Task<Terminal> TerminalAsync(Env env, BackupStatus status)
    {
        var store = env.Store();
        var world = await env.SeedAsync(status == BackupStatus.AgentLost ? Policy with { MaxAttempts = 1 } : Policy);
        var run = await StartAsync(store, world);
        switch (status)
        {
            case BackupStatus.Succeeded:
                var command = await StartAttemptAsync(store, await ClaimAsync(store, world, Now), Now);
                Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(command, Success(Now), default));
                break;
            case BackupStatus.Failed:
                await FailAsync(store, await ClaimAsync(store, world, Now), "PreCheckFailed", Now);
                break;
            case BackupStatus.Cancelled:
                await store.RequestCancellationAsync(world.TenantId, run.Id, Now, default);
                break;
            case BackupStatus.AgentLost:
                await ClaimAsync(store, world, Now);
                // The next poll after the lease ran out flips the attempt; with a single allowed attempt the run ends AgentLost.
                Assert.Null(await store.ClaimNextAsync(world.TenantId, world.AgentId, Now + Lease, Lease, default));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }
        var persisted = await env.LoadAsync(world, run.Id);
        Assert.Equal(status, persisted.Status);
        return new Terminal(world, run.Id, persisted.CurrentAttempt.Id);
    }

    /// <summary>Every callback an attempt can make; each must be refused with the same result and none may have an effect.</summary>
    private static async Task AssertRejectedAsync(IBackupRunStore store, BackupLeaseCommand command, BackupMutationResult expected = BackupMutationResult.Conflict)
    {
        Assert.Equal(expected, await store.StartAttemptAsync(command with { SoftwareVersion = "1.0", ProtocolVersion = "1.0", Platform = "Linux" }, default));
        Assert.Equal(expected, await store.ReportProgressAsync(command, new BackupProgress("Uploading", 99), default));
        Assert.Null(await store.RenewLeaseAsync(command, Lease, default));
        Assert.Equal(expected, await store.CompleteAttemptAsync(command, Success(command.Now), default));
        Assert.Equal(expected, await store.CompleteAttemptAsync(command, new Completion("Failed", "InternalError", null), default));
    }

    private static async Task AssertAttemptNumbersAsync(Env env, Guid runId, int count) =>
        Assert.Equal(Enumerable.Range(1, count), (await env.AttemptsAsync(runId)).Select(attempt => attempt.Number));


    private static async Task AssertViolationAsync(PlatformDbContext context, string sqlState, FormattableString sql, string? constraint = null)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(sql));
        Assert.Equal(sqlState, error.SqlState);
        if (constraint is not null) Assert.Equal(constraint, error.ConstraintName);
    }

    private static IEnumerable<BackupRetryStarted> Mine(IEnumerable<BackupRetryStarted> started, Guid runId) => started.Where(item => item.RunId == runId);

    /// <summary>Releases all operations at the same instant so they genuinely contend instead of running one after another.</summary>
    private static async Task<T[]> RaceAsync<T>(int count, Func<int, Task<T>> operation)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count).Select(async index =>
        {
            await gate.Task;
            return await operation(index);
        }).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static async Task<object?> Box<T>(Task<T> task) => await task;

    private static BackupLeaseCommand Command(BackupClaim claim, DateTimeOffset now) => new(claim.Run.TenantId, claim.Run.AgentId,
        claim.Attempt.Id, claim.Attempt.LeaseToken!.Value, now) { SoftwareVersion = "1.0", ProtocolVersion = "1.0", Platform = "Linux" };
    private static Completion Success(DateTimeOffset createdAt) => new("Succeeded", null, new BackupArtifactResult(100, SourceHash, ArtifactHash,
        "sha256", createdAt, "{\"DatabaseName\":\"Database\"}", "{\"CopyOnly\":true}",
        [new BackupReplicaResult("local", "default", "tenant/job/run/attempt/backup.bak", 100, ArtifactHash, "sha256", "Verified", "ReadBack", createdAt, createdAt)]));
    private static BackupConfiguration Configuration() => BackupStoreHarness.Configuration();
}
