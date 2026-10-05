using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BackupPlatform.IntegrationTests.BackupStoreHarness;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// A job's overlap policy decides what a manual start does while another run of the same job is queued, retrying or executing:
/// Skip refuses it (no run is created), Queue enqueues it. The decision is made under the agent lock and backed by a partial
/// unique index, so parallel callers cannot both win.
/// </summary>
public sealed class BackupOverlapTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly BackupStoreHarness _kit = new(fixture);

    [Theory]
    [InlineData("Pending")]
    [InlineData("Leased")]
    [InlineData("Running")]
    [InlineData("RetryPending")]
    public async Task SkipRefusesASecondManualStartWhileTheFirstRunIsActive(string state)
    {
        var agent = await _kit.AddAgentAsync();
        var store = _kit.Store(Limits(agent: 5));
        var job = await SkipJobAsync(agent, store);
        var first = await _kit.StartRunAsync(store, job, "first", T0);
        await DriveAsync(store, agent, state);
        Assert.Equal(Enum.Parse<BackupStatus>(state), (await store.GetRunAsync(agent.TenantId, first.Id, default))!.Status);

        var second = await store.StartRunAsync(agent.TenantId, job.Id, "second", T0.AddSeconds(5), default);

        Assert.Equal(BackupStartOutcome.SkippedOverlap, second.Outcome);
        Assert.Equal(first.Id, second.ActiveRunId);
        Assert.Null(second.Run);
        Assert.Equal(1, await _kit.CountRunsAsync(job.Id));
        Assert.Equal(BackupOverlapPolicy.Skip, first.OverlapPolicy);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("FailedNotRetryable")]
    [InlineData("FailedRetriesExhausted")]
    [InlineData("CancelledWhilePending")]
    [InlineData("CancelledWhileRetryPending")]
    [InlineData("AgentLostRetriesExhausted")]
    public async Task SkipAllowsANewRunOnceTheActiveOneIsTerminal(string ending)
    {
        var agent = await _kit.AddAgentAsync();
        var store = _kit.Store(Limits(agent: 5));
        var noRetries = new BackupRetryPolicy { MaxAttempts = 1 };
        var job = await SkipJobAsync(agent, store, ending is "FailedRetriesExhausted" or "AgentLostRetriesExhausted" ? noRetries : null);
        var first = await _kit.StartRunAsync(store, job, "first", T0);
        switch (ending)
        {
            case "CancelledWhilePending":
                await store.RequestCancellationAsync(agent.TenantId, first.Id, T0.AddSeconds(1), default);
                break;
            case "CancelledWhileRetryPending":
                await DriveAsync(store, agent, "RetryPending");
                await store.RequestCancellationAsync(agent.TenantId, first.Id, T0.AddSeconds(3), default);
                break;
            case "AgentLostRetriesExhausted":
                await DriveAsync(store, agent, "Running");
                await store.ExpireStaleAsync(T0 + Lease, default);
                break;
            default:
                var claim = (await ClaimAsync(store, agent, T0))!;
                await StartAttemptAsync(store, claim, T0.AddSeconds(1));
                await CompleteAsync(store, claim, ending switch
                {
                    "Succeeded" => Success(T0.AddSeconds(2)),
                    "FailedNotRetryable" => Failed("PreCheckFailed"),
                    _ => Failed("OperationTimedOut")
                }, T0.AddSeconds(2));
                break;
        }
        Assert.True((await store.GetRunAsync(agent.TenantId, first.Id, default))!.IsTerminal, "the first run must be terminal for this scenario");

        var second = await store.StartRunAsync(agent.TenantId, job.Id, "second", T0 + Lease + TimeSpan.FromSeconds(1), default);

        Assert.Equal(BackupStartOutcome.Started, second.Outcome);
        Assert.NotEqual(first.Id, second.Run!.Id);
        Assert.Equal(BackupStatus.Pending, second.Run.Status);
        var third = await store.StartRunAsync(agent.TenantId, job.Id, "third", T0 + Lease + TimeSpan.FromSeconds(2), default);
        Assert.Equal((BackupStartOutcome.SkippedOverlap, second.Run.Id), (third.Outcome, third.ActiveRunId));
        Assert.Equal(2, await _kit.CountRunsAsync(job.Id));
    }

    [Fact]
    public async Task SkipOnlyConsidersRunsOfTheSameJob()
    {
        var agent = await _kit.AddAgentAsync();
        var store = _kit.Store(Limits(agent: 5));
        var busy = await SkipJobAsync(agent, store);
        var other = await SkipJobAsync(agent, store);
        await _kit.StartRunAsync(store, busy, "key", T0);

        // Same tenant, same agent, same key: a different job is unaffected. (The key is scoped to the job, too.)
        var started = await store.StartRunAsync(agent.TenantId, other.Id, "key", T0, default);

        Assert.Equal(BackupStartOutcome.Started, started.Outcome);
        Assert.Equal(other.Id, started.Run!.JobId);
    }

    [Fact]
    public async Task QueueEnqueuesBehindTheActiveRun()
    {
        var agent = await _kit.AddAgentAsync();
        var store = _kit.Store(Limits(agent: 1));
        var job = await _kit.AddJobAsync(agent, BackupOverlapPolicy.Queue);
        var first = await _kit.StartRunAsync(store, job, "first", T0);
        var claim = (await ClaimAsync(store, agent, T0))!;
        await StartAttemptAsync(store, claim, T0.AddSeconds(1));

        var second = await _kit.StartRunAsync(store, job, "second", T0.AddSeconds(2));
        var third = await _kit.StartRunAsync(store, job, "third", T0.AddSeconds(3));

        Assert.Equal(BackupOverlapPolicy.Queue, second.OverlapPolicy);
        Assert.Equal(3, await _kit.CountRunsAsync(job.Id));
        var runs = (await store.ListRunsAsync(agent.TenantId, job.Id, 10, default)).ToDictionary(run => run.Id);
        Assert.Equal(BackupStatus.Running, runs[first.Id].Status);
        Assert.Equal(new[] { BackupStatus.Pending, BackupStatus.Pending }, new[] { runs[second.Id].Status, runs[third.Id].Status });
        Assert.Null(await ClaimAsync(store, agent, T0.AddSeconds(4))); // queued behind the running one: the agent runs one backup at a time
    }

    [Fact]
    public async Task ConcurrentManualStartsOnASkipJobCreateExactlyOneRun()
    {
        var agent = await _kit.AddAgentAsync();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var job = await SkipJobAsync(agent, _kit.Store());

            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
                Task.Run(() => _kit.Store().StartRunAsync(agent.TenantId, job.Id, $"key-{iteration}-{i}", T0, default))));

            var winner = Assert.Single(results, result => result.Outcome == BackupStartOutcome.Started);
            var losers = results.Where(result => result.Outcome != BackupStartOutcome.Started).ToList();
            Assert.Equal(11, losers.Count);
            Assert.All(losers, loser =>
            {
                Assert.Equal(BackupStartOutcome.SkippedOverlap, loser.Outcome);
                Assert.Equal(winner.Run!.Id, loser.ActiveRunId);
                Assert.Null(loser.Run);
            });
            Assert.Equal(1, await _kit.CountRunsAsync(job.Id));
            Assert.Equal(1, await _kit.CountAttemptsAsync(winner.Run!.Id));
        }
    }

    [Fact]
    public async Task ConcurrentStartsWithTheSameKeyCreateOneRunAndReplayTheRest()
    {
        var agent = await _kit.AddAgentAsync();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var job = await SkipJobAsync(agent, _kit.Store());

            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
                Task.Run(() => _kit.Store().StartRunAsync(agent.TenantId, job.Id, "double-click", T0, default))));

            var winner = Assert.Single(results, result => result.Outcome == BackupStartOutcome.Started);
            Assert.All(results.Where(result => result.Outcome != BackupStartOutcome.Started), replay =>
            {
                // Replayed, not SkippedOverlap: the key identifies the request, and its run exists although the job is busy.
                Assert.Equal(BackupStartOutcome.Replayed, replay.Outcome);
                Assert.Equal(winner.Run!.Id, replay.Run!.Id);
            });
            Assert.Equal(1, await _kit.CountRunsAsync(job.Id));
        }
    }

    [Fact]
    public async Task ReplayReturnsTheOriginalRunWhetherTheJobIsBusyOrTheRunIsTerminalAndNeverAddsAnAttempt()
    {
        var agent = await _kit.AddAgentAsync();
        var store = _kit.Store(Limits(agent: 5));
        var job = await SkipJobAsync(agent, store);
        var first = await _kit.StartRunAsync(store, job, "a", T0);

        var whileActive = await store.StartRunAsync(agent.TenantId, job.Id, "a", T0.AddSeconds(1), default);
        Assert.Equal((BackupStartOutcome.Replayed, first.Id), (whileActive.Outcome, whileActive.Run!.Id));

        var claim = (await ClaimAsync(store, agent, T0))!;
        await CompleteAsync(store, claim, Failed("PreCheckFailed"), T0.AddSeconds(2));
        var afterFailure = await store.StartRunAsync(agent.TenantId, job.Id, "a", T0.AddSeconds(3), default);
        Assert.Equal((BackupStartOutcome.Replayed, first.Id), (afterFailure.Outcome, afterFailure.Run!.Id));
        Assert.Equal(BackupStatus.Failed, afterFailure.Run.Status); // replaying never reopens a finished run

        var second = await _kit.StartRunAsync(store, job, "b", T0.AddSeconds(4)); // the job is free again...
        var whileBusy = await store.StartRunAsync(agent.TenantId, job.Id, "a", T0.AddSeconds(5), default);
        Assert.Equal((BackupStartOutcome.Replayed, first.Id), (whileBusy.Outcome, whileBusy.Run!.Id)); // ...and busy again: still a replay
        Assert.Equal(BackupStartOutcome.SkippedOverlap, (await store.StartRunAsync(agent.TenantId, job.Id, "c", T0.AddSeconds(6), default)).Outcome);

        Assert.Equal(1, await _kit.CountAttemptsAsync(first.Id));
        Assert.Equal(1, await _kit.CountAttemptsAsync(second.Id));
        Assert.Equal(2, await _kit.CountRunsAsync(job.Id));
    }

    [Fact]
    public async Task IdempotencyKeyIsIndependentOfRetries()
    {
        var agent = await _kit.AddAgentAsync();
        var store = _kit.Store(Limits(agent: 5));
        var job = await SkipJobAsync(agent, store, new BackupRetryPolicy { MaxAttempts = 4 });
        var run = await _kit.StartRunAsync(store, job, "the-key", T0);

        // Attempt 1 times out; the scheduled retry (attempt 2) is created by the worker.
        var first = (await ClaimAsync(store, agent, T0))!;
        await CompleteAsync(store, first, Failed("OperationTimedOut"), T0.AddSeconds(5));
        Assert.Contains(await store.StartDueRetriesAsync(T0.AddMinutes(1), 500, default), started => started.RunId == run.Id);
        // Attempt 2 fails for good; an operator retries (attempt 3), it times out, and the operator hits "retry now" (attempt 4).
        var second = (await ClaimAsync(store, agent, T0.AddMinutes(1)))!;
        await CompleteAsync(store, second, Failed("PreCheckFailed"), T0.AddMinutes(2));
        Assert.Equal(BackupRetryOutcome.Started, (await store.RetryRunAsync(agent.TenantId, run.Id, T0.AddMinutes(3), default)).Outcome);
        var third = (await ClaimAsync(store, agent, T0.AddMinutes(3)))!;
        await CompleteAsync(store, third, Failed("OperationTimedOut"), T0.AddMinutes(4));
        Assert.Equal(BackupRetryOutcome.Started, (await store.RetryRunAsync(agent.TenantId, run.Id, T0.AddMinutes(4), default)).Outcome);

        var replay = await store.StartRunAsync(agent.TenantId, job.Id, "the-key", T0.AddMinutes(5), default);

        Assert.Equal(BackupStartOutcome.Replayed, replay.Outcome);
        Assert.Equal(run.Id, replay.Run!.Id);
        Assert.Equal(new[] { 1, 2, 3, 4 }, replay.Run.Attempts.Select(attempt => attempt.Number).Order().ToArray());
        Assert.Equal(4, replay.Run.CurrentAttemptNumber);
        Assert.Equal(BackupStatus.Pending, replay.Run.Status);
        Assert.Equal(1, await _kit.CountRunsAsync(job.Id)); // not one run per attempt
        Assert.Equal(BackupStartOutcome.SkippedOverlap, (await store.StartRunAsync(agent.TenantId, job.Id, "another-key", T0.AddMinutes(5), default)).Outcome);
    }

    [Fact]
    public async Task SkipRefusesManualRetryOfAFailedRunWhileAnotherRunIsActiveAndTheDatabaseBacksItUp()
    {
        var agent = await _kit.AddAgentAsync();
        var store = _kit.Store(Limits(agent: 5));
        var job = await SkipJobAsync(agent, store);
        var failed = await _kit.StartRunAsync(store, job, "first", T0);
        await CompleteAsync(store, (await ClaimAsync(store, agent, T0))!, Failed("PreCheckFailed"), T0.AddSeconds(1));
        var active = await _kit.StartRunAsync(store, job, "second", T0.AddSeconds(2)); // allowed: the first run is over

        var refused = await store.RetryRunAsync(agent.TenantId, failed.Id, T0.AddSeconds(3), default);

        Assert.Equal(BackupRetryOutcome.OverlapActive, refused.Outcome);
        var untouched = (await store.GetRunAsync(agent.TenantId, failed.Id, default))!; // the result's own Run is not authoritative: read it back
        Assert.Equal(BackupStatus.Failed, untouched.Status);
        Assert.Single(untouched.Attempts);

        // Even a statement that bypasses the application cannot reopen the run: the partial unique index allows one active Skip run per job.
        await using var context = fixture.CreatePlatformContext();
        var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE backup_runs SET \"Status\" = 1, \"CompletedAt\" = NULL, \"TerminalReason\" = NULL, \"RetryState\" = 0 WHERE \"Id\" = {failed.Id}"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("backup_runs", error.TableName);
        Assert.Equal(BackupStatus.Failed, (await store.GetRunAsync(agent.TenantId, failed.Id, default))!.Status);

        // Once the other run is over, the same retry is fine.
        await store.RequestCancellationAsync(agent.TenantId, active.Id, T0.AddSeconds(4), default);
        var allowed = await store.RetryRunAsync(agent.TenantId, failed.Id, T0.AddSeconds(5), default);
        Assert.Equal(BackupRetryOutcome.Started, allowed.Outcome);
        var reopened = (await store.GetRunAsync(agent.TenantId, failed.Id, default))!;
        Assert.Equal((BackupStatus.Pending, 2), (reopened.Status, reopened.Attempts.Count));
    }

    [Fact]
    public async Task QueueAllowsManualRetryOfAFailedRunWhileAnotherRunIsActive()
    {
        var agent = await _kit.AddAgentAsync();
        var store = _kit.Store(Limits(agent: 5));
        var job = await _kit.AddJobAsync(agent, BackupOverlapPolicy.Queue);
        var failed = await _kit.StartRunAsync(store, job, "first", T0);
        await CompleteAsync(store, (await ClaimAsync(store, agent, T0))!, Failed("PreCheckFailed"), T0.AddSeconds(1));
        var active = await _kit.StartRunAsync(store, job, "second", T0.AddSeconds(2));

        var result = await store.RetryRunAsync(agent.TenantId, failed.Id, T0.AddSeconds(3), default);

        Assert.Equal(BackupRetryOutcome.Started, result.Outcome);
        var runs = (await store.ListRunsAsync(agent.TenantId, job.Id, 10, default)).ToDictionary(run => run.Id);
        Assert.Equal(new[] { BackupStatus.Pending, BackupStatus.Pending }, new[] { runs[failed.Id].Status, runs[active.Id].Status });
        Assert.Equal(2, runs[failed.Id].Attempts.Count);
    }

    [Fact]
    public async Task RetryNowOfARetryPendingRunIsNotAnOverlapWithItself()
    {
        var agent = await _kit.AddAgentAsync();
        var store = _kit.Store(Limits(agent: 5));
        var job = await SkipJobAsync(agent, store);
        var run = await _kit.StartRunAsync(store, job, "first", T0);
        await DriveAsync(store, agent, "RetryPending");

        var retry = await store.RetryRunAsync(agent.TenantId, run.Id, T0.AddSeconds(10), default);

        Assert.Equal(BackupRetryOutcome.Started, retry.Outcome);
        var persisted = (await store.GetRunAsync(agent.TenantId, run.Id, default))!;
        Assert.Equal((BackupStatus.Pending, 2), (persisted.Status, persisted.Attempts.Count));
        var blocked = await store.StartRunAsync(agent.TenantId, job.Id, "second", T0.AddSeconds(11), default);
        Assert.Equal((BackupStartOutcome.SkippedOverlap, run.Id), (blocked.Outcome, blocked.ActiveRunId));
    }

    /// <summary>A job created with the constructor's overlap default, which must be Skip, whatever the harness would pick.</summary>
    private static async Task<BackupJob> SkipJobAsync(StoreAgent agent, PostgreSqlBackupRunStore store, BackupRetryPolicy? retry = null)
    {
        var created = await store.CreateJobAsync(new BackupJob(Guid.NewGuid(), agent.TenantId, "Database", agent.AgentId, Configuration(), T0, retry), default);
        var persisted = (await store.GetJobAsync(agent.TenantId, created!.Id, default))!;
        Assert.Equal(BackupOverlapPolicy.Skip, persisted.OverlapPolicy);
        return persisted;
    }

    /// <summary>Moves the agent's only queued attempt to the requested state (Pending does nothing); a retryable failure ends in RetryPending.</summary>
    private static async Task DriveAsync(PostgreSqlBackupRunStore store, StoreAgent agent, string state)
    {
        if (state == "Pending") return;
        var claim = (await ClaimAsync(store, agent, T0))!;
        if (state == "Leased") return;
        await StartAttemptAsync(store, claim, T0.AddSeconds(1));
        if (state == "Running") return;
        await CompleteAsync(store, claim, Failed("OperationTimedOut"), T0.AddSeconds(2));
    }
}
