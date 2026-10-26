using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using static BackupPlatform.UnitTests.BackupRunTestSupport;

namespace BackupPlatform.UnitTests;

public sealed class BackupRunRetryTests
{
    public static TheoryData<string> RetryableCodes => new()
    {
        BackupFailureCodes.DatabaseUnavailable,
        BackupFailureCodes.OperationTimedOut,
        BackupFailureCodes.StorageUnavailable,
        BackupFailureCodes.ReplicaVerificationFailed
    };

    public static TheoryData<string> NonRetryableCodes => new()
    {
        BackupFailureCodes.PreCheckFailed,
        BackupFailureCodes.SqlBackupFailed,
        BackupFailureCodes.VerificationFailed,
        BackupFailureCodes.HashFailed,
        BackupFailureCodes.StorageWriteFailed,
        BackupFailureCodes.InternalError,
        BackupFailureCodes.DatabaseNotFound,
        BackupFailureCodes.InvalidConfiguration,
        BackupFailureCodes.UnsupportedCapability,
        BackupFailureCodes.AuthenticationFailed,
        BackupFailureCodes.PermissionDenied,
        BackupFailureCodes.StagingInvalid
    };

    private static int Count(BackupRun run, string kind) => run.Facts.Count(fact => fact.Kind == kind);
    private static BackupCompletion Failed(string code) => new(BackupStatus.Failed, code, null);

    // ---- scheduling -------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RetryableCodes))]
    public void RetryableFailureWithBudgetSchedulesARetryAndLeavesTheRunNonTerminal(string code)
    {
        var policy = Policy(3, jitter: 0.5);
        var run = NewRun(policy);
        var failedAt = Now.AddMinutes(1);

        Fail(run, code, failedAt, jitter: 0.5);

        Assert.Equal(BackupStatus.RetryPending, run.Status);
        Assert.Equal(BackupRetryState.Scheduled, run.RetryState);
        Assert.Equal(failedAt + policy.DelayAfter(1, 0.5), run.NextAttemptAt);
        Assert.Equal(failedAt.AddSeconds(7.5), run.NextAttemptAt);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.TerminalReason);
        Assert.False(run.IsTerminal);
        Assert.True(run.IsActive);
        Assert.Equal(BackupStatus.Failed, run.CurrentAttempt.Status);
        Assert.Equal(code, run.CurrentAttempt.TerminalReason);
        var scheduled = Assert.Single(run.Facts, fact => fact.Kind == "BackupRetryScheduled");
        Assert.Equal(run.CurrentAttempt.Id, scheduled.AttemptId);
        Assert.Equal(failedAt, scheduled.At);
        Assert.Equal(0, Count(run, "BackupRunFailed"));
        Assert.Equal(0, Count(run, "BackupRetryExhausted"));
    }

    [Fact]
    public void ScheduledDelayFollowsTheBackoffAcrossSuccessiveFailures()
    {
        var run = NewRun(Policy(4));
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        Assert.Equal(Now.AddSeconds(10), run.NextAttemptAt);

        var second = run.NextAttemptAt!.Value;
        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), second));
        Fail(run, BackupFailureCodes.DatabaseUnavailable, second.AddSeconds(3));
        Assert.Equal(second.AddSeconds(3).AddSeconds(20), run.NextAttemptAt);

        var third = run.NextAttemptAt!.Value;
        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), third));
        Fail(run, BackupFailureCodes.DatabaseUnavailable, third);
        Assert.Equal(third.AddSeconds(40), run.NextAttemptAt);
    }

    [Theory]
    [MemberData(nameof(NonRetryableCodes))]
    public void NonRetryableFailureEndsTheRunImmediatelyEvenWithBudgetLeft(string code)
    {
        var run = NewRun(Policy(5));
        var failedAt = Now.AddSeconds(30);

        Fail(run, code, failedAt);

        Assert.Equal(BackupStatus.Failed, run.Status);
        Assert.Equal(BackupRetryState.NotRetryable, run.RetryState);
        Assert.Equal(code, run.TerminalReason);
        Assert.Equal(failedAt, run.CompletedAt);
        Assert.Null(run.NextAttemptAt);
        Assert.True(run.IsTerminal);
        Assert.Equal(1, Count(run, "BackupRunFailed"));
        Assert.Equal(0, Count(run, "BackupRetryScheduled"));
        Assert.Equal(0, Count(run, "BackupRetryExhausted"));
        Assert.False(run.StartScheduledRetry(Guid.NewGuid(), failedAt.AddDays(1)));
        Assert.Single(run.Attempts);
    }

    [Fact]
    public void RetryableFailureOnTheLastAllowedAttemptExhaustsTheBudget()
    {
        var run = NewRun(Policy(2));
        Fail(run, BackupFailureCodes.StorageUnavailable, Now);
        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), run.NextAttemptAt!.Value));
        var lastFailure = Now.AddMinutes(5);

        Fail(run, BackupFailureCodes.StorageUnavailable, lastFailure);

        Assert.Equal(BackupStatus.Failed, run.Status);
        Assert.Equal(BackupRetryState.Exhausted, run.RetryState);
        Assert.Equal(BackupFailureCodes.StorageUnavailable, run.TerminalReason);
        Assert.Equal(lastFailure, run.CompletedAt);
        Assert.Null(run.NextAttemptAt);
        Assert.Equal(1, Count(run, "BackupRetryScheduled"));
        Assert.Equal(1, Count(run, "BackupRetryExhausted"));
        Assert.Equal(1, Count(run, "BackupRunFailed"));
        Assert.False(run.StartScheduledRetry(Guid.NewGuid(), lastFailure.AddDays(1)));
        Assert.Equal(2, run.Attempts.Count);
    }

    [Fact]
    public void SingleAttemptPolicyNeverSchedulesARetry()
    {
        var run = NewRun(Policy(1));
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        Assert.Equal(BackupStatus.Failed, run.Status);
        Assert.Equal(BackupRetryState.Exhausted, run.RetryState);
        Assert.Equal(0, Count(run, "BackupRetryScheduled"));
        Assert.Equal(1, Count(run, "BackupRetryExhausted"));
    }

    [Fact]
    public void AgentLostFollowsTheSameRetryPolicyAsAFailure()
    {
        var run = NewRun(Policy(2));
        Begin(run, Now);
        var expiredAt = Now.Add(Lease);

        Assert.True(run.Expire(expiredAt));

        Assert.Equal(BackupStatus.RetryPending, run.Status);
        Assert.Equal(BackupRetryState.Scheduled, run.RetryState);
        Assert.Equal(BackupStatus.AgentLost, run.CurrentAttempt.Status);
        Assert.Equal(BackupFailureCodes.AgentLost, run.CurrentAttempt.TerminalReason);
        Assert.Equal(expiredAt.AddSeconds(10), run.NextAttemptAt);
        Assert.Null(run.CompletedAt);
        Assert.Equal(1, Count(run, "BackupRetryScheduled"));

        // The second loss uses up the budget: the run ends AgentLost, not Failed.
        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), run.NextAttemptAt!.Value));
        var secondStart = run.CurrentAttempt.CreatedAt;
        Begin(run, secondStart);
        Assert.True(run.Expire(secondStart.Add(Lease)));

        Assert.Equal(BackupStatus.AgentLost, run.Status);
        Assert.Equal(BackupRetryState.Exhausted, run.RetryState);
        Assert.Equal(BackupFailureCodes.AgentLost, run.TerminalReason);
        Assert.Equal(secondStart.Add(Lease), run.CompletedAt);
        Assert.Equal(1, Count(run, "BackupRetryExhausted"));
    }

    [Fact]
    public void AgentLostWithASingleAttemptPolicyIsTerminalAgentLost()
    {
        var run = NewRun(Policy(1));
        Begin(run, Now);
        Assert.True(run.Expire(Now.Add(Lease)));
        Assert.Equal(BackupStatus.AgentLost, run.Status);
        Assert.Equal(BackupRetryState.Exhausted, run.RetryState);
        Assert.Equal(BackupFailureCodes.AgentLost, run.TerminalReason);
        Assert.True(run.IsTerminal);
    }

    [Fact]
    public void ExpiryRequiresAnExpiredLeaseOnALiveAttempt()
    {
        var run = NewRun();
        Assert.False(run.Expire(Now.AddDays(1)), "a queued attempt holds no lease");
        var token = Begin(run, Now);
        Assert.False(run.Expire(Now.Add(Lease).AddTicks(-1)));
        Assert.Equal(BackupStatus.Running, run.Status);
        Assert.True(run.CurrentAttempt.HasLease(token, Now.Add(Lease).AddTicks(-1)));
        Assert.True(run.Expire(Now.Add(Lease)));
    }

    [Fact]
    public void SuccessIsNeverRetried()
    {
        var run = NewRun();
        var token = Begin(run, Now);
        Succeed(run, token, Now.AddSeconds(5));

        Assert.Equal(BackupStatus.Succeeded, run.Status);
        Assert.Equal(BackupRetryState.None, run.RetryState);
        Assert.Null(run.NextAttemptAt);
        Assert.Equal(0, Count(run, "BackupRetryScheduled"));
        Assert.False(run.StartScheduledRetry(Guid.NewGuid(), Now.AddDays(1)));
        Assert.Equal(BackupManualRetryResult.Succeeded, run.RequestManualRetry(Guid.NewGuid(), Now.AddDays(1)));
        Assert.False(run.Expire(Now.AddDays(1)));
        Assert.Single(run.Attempts);
    }

    [Fact]
    public void SuccessOnARetryAttemptSucceedsTheWholeRunAndKeepsTheFailureInHistory()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.OperationTimedOut, Now);
        var due = run.NextAttemptAt!.Value;
        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), due));
        var token = Begin(run, due);
        Succeed(run, token, due.AddSeconds(8));

        Assert.Equal(BackupStatus.Succeeded, run.Status);
        Assert.Equal(BackupRetryState.None, run.RetryState);
        Assert.Equal(due.AddSeconds(8), run.CompletedAt);
        Assert.Equal(BackupStatus.Failed, run.Attempts[0].Status);
        Assert.Equal(BackupStatus.Succeeded, run.Attempts[1].Status);
        Assert.Single(run.Attempts[1].Artifacts);
        Assert.Empty(run.Attempts[0].Artifacts);
    }

    // ---- starting the scheduled retry ----------------------------------------------------------------------------------------------

    [Fact]
    public void ScheduledRetryIsNotStartedBeforeItIsDueAndStartsExactlyOnce()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        var due = run.NextAttemptAt!.Value;
        var firstAttemptId = run.CurrentAttempt.Id;

        Assert.False(run.StartScheduledRetry(Guid.NewGuid(), due.AddTicks(-10)));
        Assert.Equal(BackupStatus.RetryPending, run.Status);
        Assert.Single(run.Attempts);
        Assert.Equal(0, Count(run, "BackupRetryStarted"));

        var secondId = Guid.NewGuid();
        Assert.True(run.StartScheduledRetry(secondId, due));

        Assert.Equal(BackupStatus.Pending, run.Status);
        Assert.Equal(2, run.CurrentAttemptNumber);
        Assert.Equal(secondId, run.CurrentAttempt.Id);
        Assert.Equal(2, run.CurrentAttempt.Number);
        Assert.Equal(run.Id, run.CurrentAttempt.RunId);
        Assert.Equal(BackupRetryState.None, run.RetryState);
        Assert.Null(run.NextAttemptAt);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.TerminalReason);
        var started = Assert.Single(run.Facts, fact => fact.Kind == "BackupRetryStarted");
        Assert.Equal(secondId, started.AttemptId);

        // A second worker (or a repeated scan) finds nothing to do.
        Assert.False(run.StartScheduledRetry(Guid.NewGuid(), due.AddHours(1)));
        Assert.Equal(2, run.Attempts.Count);
        Assert.Equal(firstAttemptId, run.Attempts[0].Id);
        Assert.Equal(1, Count(run, "BackupRetryStarted"));
    }

    [Fact]
    public void StartingARetryNeverTouchesThePreviousAttempt()
    {
        var run = NewRun();
        var token = Fail(run, BackupFailureCodes.StorageUnavailable, Now.AddSeconds(3));
        var first = run.Attempts[0];
        var snapshot = (first.Id, first.Number, first.Status, first.TerminalReason, first.CompletedAt, first.LeaseToken, first.CreatedAt, first.EligibleAt, first.StartedAt);

        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), run.NextAttemptAt!.Value));

        Assert.Same(first, run.Attempts[0]);
        Assert.Equal(snapshot, (first.Id, first.Number, first.Status, first.TerminalReason, first.CompletedAt, first.LeaseToken, first.CreatedAt, first.EligibleAt, first.StartedAt));
        Assert.True(first.IsTerminal);
        Assert.Equal(token, first.LeaseToken);
        Assert.Null(run.CurrentAttempt.LeaseToken);
        Assert.Equal(BackupStatus.Pending, run.CurrentAttempt.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void RetryAttemptIsEligibleFromItsScheduledDueTimeWhetherStartedOnTimeOrLate(int minutesLate)
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        var due = run.NextAttemptAt!.Value;
        var startedAt = due.AddMinutes(minutesLate);

        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), startedAt));

        Assert.Equal(due, run.CurrentAttempt.EligibleAt);
        Assert.Equal(startedAt, run.CurrentAttempt.CreatedAt);
    }

    [Fact]
    public void ARetryAttemptIsAFullyFunctionalAttemptOnTheSameRun()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        var due = run.NextAttemptAt!.Value;
        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), due));

        var token = Begin(run, due.AddSeconds(1));

        Assert.Equal(BackupStatus.Running, run.Status);
        Assert.Equal(2, run.CurrentAttempt.Number);
        Assert.Equal(token, run.CurrentAttempt.LeaseToken);
        Assert.Equal(Now, run.StartedAt);
        Assert.Equal(2, Count(run, "BackupRunStarted"));
    }

    [Fact]
    public void RetryStartedFactsAppearInLifecycleOrder()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        run.StartScheduledRetry(Guid.NewGuid(), run.NextAttemptAt!.Value);

        Assert.Equal(
            ["BackupRunCreated", "AttemptQueued", "AttemptLeased", "BackupRunStarted", "BackupRetryScheduled", "BackupRetryStarted", "BackupAutomaticRetryStarted"],
            run.Facts.Select(fact => fact.Kind));
    }

    // ---- cancellation outranks retry -----------------------------------------------------------------------------------------------

    [Fact]
    public void CancellingAPendingRunCancelsItImmediately()
    {
        var run = NewRun();
        run.RequestCancellation(Now.AddSeconds(2));

        Assert.Equal(BackupStatus.Cancelled, run.Status);
        Assert.Equal(BackupStatus.Cancelled, run.CurrentAttempt.Status);
        Assert.Equal(Now.AddSeconds(2), run.CompletedAt);
        Assert.Equal(Now.AddSeconds(2), run.CancellationRequestedAt);
        Assert.Equal(BackupRetryState.None, run.RetryState);
        Assert.Equal(BackupFailureCodes.Cancelled, run.TerminalReason);
        Assert.Equal(1, Count(run, "BackupCancellationRequested"));
        Assert.Equal(1, Count(run, "BackupRunCancelled"));
    }

    [Fact]
    public void CancellingWhileRetryPendingWithdrawsTheRetryImmediately()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        var due = run.NextAttemptAt!.Value;
        var cancelledAt = Now.AddSeconds(1);

        run.RequestCancellation(cancelledAt);

        Assert.Equal(BackupStatus.Cancelled, run.Status);
        Assert.Equal(BackupRetryState.None, run.RetryState);
        Assert.Null(run.NextAttemptAt);
        Assert.Equal(cancelledAt, run.CompletedAt);
        Assert.Equal(BackupFailureCodes.Cancelled, run.TerminalReason);
        Assert.True(run.IsTerminal);
        Assert.Equal(1, Count(run, "BackupCancellationRequested"));
        Assert.Equal(1, Count(run, "BackupRunCancelled"));
        // The failed attempt keeps its true outcome; only the run's decision changed.
        Assert.Equal(BackupStatus.Failed, run.CurrentAttempt.Status);
        Assert.Equal(BackupFailureCodes.DatabaseUnavailable, run.CurrentAttempt.TerminalReason);

        Assert.False(run.StartScheduledRetry(Guid.NewGuid(), due.AddHours(1)));
        Assert.Single(run.Attempts);
        Assert.Equal(BackupStatus.Cancelled, run.Status);
        Assert.Equal(BackupManualRetryResult.Cancelled, run.RequestManualRetry(Guid.NewGuid(), due.AddHours(1)));
        Assert.Single(run.Attempts);
    }

    [Theory]
    [MemberData(nameof(RetryableCodes))]
    [MemberData(nameof(NonRetryableCodes))]
    public void FailureAfterCancellationWasRequestedEndsTheRunCancelledNeverRetried(string code)
    {
        var run = NewRun(Policy(5));
        var token = Begin(run, Now);
        run.RequestCancellation(Now.AddSeconds(1));
        Assert.Equal(BackupStatus.Running, run.Status);
        Assert.Equal(Now.AddSeconds(1), run.CancellationRequestedAt);
        Assert.Null(run.CompletedAt);

        run.Complete(token, Now.AddSeconds(2), Failed(code));

        Assert.Equal(BackupStatus.Cancelled, run.Status);
        Assert.Equal(BackupRetryState.None, run.RetryState);
        Assert.Null(run.NextAttemptAt);
        Assert.Equal(BackupFailureCodes.Cancelled, run.TerminalReason);
        Assert.Equal(Now.AddSeconds(2), run.CompletedAt);
        Assert.Equal(BackupStatus.Failed, run.CurrentAttempt.Status);
        Assert.Equal(code, run.CurrentAttempt.TerminalReason);
        Assert.Equal(0, Count(run, "BackupRetryScheduled"));
        Assert.Equal(0, Count(run, "BackupRetryExhausted"));
        Assert.Equal(0, Count(run, "BackupRunFailed"));
        Assert.False(run.StartScheduledRetry(Guid.NewGuid(), Now.AddDays(1)));
        Assert.Single(run.Attempts);
    }

    [Fact]
    public void LeaseExpiryAfterCancellationWasRequestedEndsTheRunCancelledNotAgentLostNorRetried()
    {
        var run = NewRun(Policy(5));
        Begin(run, Now);
        run.RequestCancellation(Now.AddSeconds(1));

        Assert.True(run.Expire(Now.Add(Lease)));

        Assert.Equal(BackupStatus.Cancelled, run.Status);
        Assert.Equal(BackupRetryState.None, run.RetryState);
        Assert.Null(run.NextAttemptAt);
        Assert.Equal(BackupFailureCodes.Cancelled, run.TerminalReason);
        Assert.Equal(BackupStatus.AgentLost, run.CurrentAttempt.Status);
        Assert.Equal(0, Count(run, "BackupRetryScheduled"));
        Assert.False(run.StartScheduledRetry(Guid.NewGuid(), Now.AddDays(1)));
        Assert.Single(run.Attempts);
    }

    [Fact]
    public void LeaseExpiryWithRetainedCheckpointAfterCancellationWasRequestedEndsTheRunCancelledNotPaused()
    {
        var run = NewRun(Policy(5));
        Begin(run, Now);
        run.RequestCancellation(Now.AddSeconds(1));

        Assert.True(run.Expire(Now.Add(Lease), retainTransfer: true));

        Assert.Equal(BackupStatus.Cancelled, run.Status);
        Assert.Equal(BackupStatus.AgentLost, run.CurrentAttempt.Status);
        Assert.True(run.IsTerminal);
    }

    [Fact]
    public void ResumeAndRequestPauseAreRejectedOnACancellingRun()
    {
        var run = NewRun();
        Begin(run, Now);
        run.RequestCancellation(Now.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() => run.RequestPause());
        Assert.Throws<InvalidOperationException>(() => run.Resume(Now.AddSeconds(2)));
        Assert.Equal(BackupStatus.Running, run.Status);
    }

    [Fact]
    public void CancellingALeasedRunIsCooperativeAndTheAgentCanAcknowledgeIt()
    {
        var run = NewRun();
        var token = Guid.NewGuid();
        run.Claim(token, Now, Lease);
        run.RequestCancellation(Now.AddSeconds(1));
        Assert.Equal(BackupStatus.Leased, run.Status);
        Assert.False(run.IsTerminal);

        run.Complete(token, Now.AddSeconds(2), new BackupCompletion(BackupStatus.Cancelled, "Cancelled", null));

        Assert.Equal(BackupStatus.Cancelled, run.Status);
        Assert.Equal(BackupStatus.Cancelled, run.CurrentAttempt.Status);
        Assert.Equal(BackupRetryState.None, run.RetryState);
        Assert.Equal(1, Count(run, "BackupRunCancelled"));
    }

    [Fact]
    public void AnAgentReportedCancellationWithoutARequestStillEndsTheRunCancelled()
    {
        var run = NewRun();
        var token = Begin(run, Now);
        run.Complete(token, Now.AddSeconds(1), new BackupCompletion(BackupStatus.Cancelled, null, null));
        Assert.Equal(BackupStatus.Cancelled, run.Status);
        Assert.Equal(BackupRetryState.None, run.RetryState);
        Assert.False(run.StartScheduledRetry(Guid.NewGuid(), Now.AddDays(1)));
    }

    [Fact]
    public void CancellationRequestedWhileRunningDoesNotOverrideASuccessThatWonTheRace()
    {
        var run = NewRun();
        var token = Begin(run, Now);
        run.RequestCancellation(Now.AddSeconds(1));
        Succeed(run, token, Now.AddSeconds(2));
        Assert.Equal(BackupStatus.Succeeded, run.Status);
        Assert.Equal(BackupRetryState.None, run.RetryState);
    }

    [Fact]
    public void RepeatedCancellationIsIdempotent()
    {
        var run = NewRun();
        Begin(run, Now);
        run.RequestCancellation(Now.AddSeconds(1));
        run.RequestCancellation(Now.AddSeconds(9));
        Assert.Equal(Now.AddSeconds(1), run.CancellationRequestedAt);
        Assert.Equal(1, Count(run, "BackupCancellationRequested"));
    }

    [Fact]
    public void CancellingATerminalRunChangesNothing()
    {
        var failed = NewRun();
        Fail(failed, BackupFailureCodes.PermissionDenied, Now);
        var succeeded = NewRun();
        Succeed(succeeded, Begin(succeeded, Now), Now.AddSeconds(1));
        var exhausted = NewRun(Policy(1));
        Fail(exhausted, BackupFailureCodes.DatabaseUnavailable, Now);

        foreach (var run in new[] { failed, succeeded, exhausted })
        {
            var status = run.Status;
            var facts = run.Facts.Count;
            var completedAt = run.CompletedAt;
            run.RequestCancellation(Now.AddHours(1));
            Assert.Equal(status, run.Status);
            Assert.Equal(facts, run.Facts.Count);
            Assert.Equal(completedAt, run.CompletedAt);
            Assert.Null(run.CancellationRequestedAt);
        }
    }

    [Fact]
    public void ATerminalRunCannotStartAScheduledRetry()
    {
        var failed = NewRun();
        Fail(failed, BackupFailureCodes.HashFailed, Now);
        var exhausted = NewRun(Policy(1));
        Fail(exhausted, BackupFailureCodes.OperationTimedOut, Now);
        var succeeded = NewRun();
        Succeed(succeeded, Begin(succeeded, Now), Now);
        var cancelled = NewRun();
        cancelled.RequestCancellation(Now);
        var lost = NewRun(Policy(1));
        Begin(lost, Now);
        lost.Expire(Now.Add(Lease));

        foreach (var run in new[] { failed, exhausted, succeeded, cancelled, lost })
        {
            Assert.True(run.IsTerminal);
            var status = run.Status;
            Assert.False(run.StartScheduledRetry(Guid.NewGuid(), Now.AddDays(30)));
            Assert.Equal(status, run.Status);
            Assert.Single(run.Attempts);
        }
    }

    [Fact]
    public void ARunThatIsNotYetRetryPendingCannotStartAScheduledRetry()
    {
        var run = NewRun();
        Assert.False(run.StartScheduledRetry(Guid.NewGuid(), Now.AddDays(1)));
        Begin(run, Now);
        Assert.False(run.StartScheduledRetry(Guid.NewGuid(), Now.AddDays(1)));
        Assert.Single(run.Attempts);
        Assert.Equal(BackupStatus.Running, run.Status);
    }

    // ---- attempt numbering and budget ----------------------------------------------------------------------------------------------

    [Fact]
    public void AttemptNumbersAreMonotonicAcrossRetriesAndNeverExceedMaxAttempts()
    {
        var run = NewRun(Policy(4, BackupBackoffStrategy.Fixed));
        var clock = Now;
        for (var expected = 1; expected <= 4; expected++)
        {
            Assert.Equal(expected, run.CurrentAttemptNumber);
            Assert.Equal(expected, run.CurrentAttempt.Number);
            Fail(run, BackupFailureCodes.DatabaseUnavailable, clock);
            if (expected == 4) break;
            clock = run.NextAttemptAt!.Value;
            Assert.True(run.StartScheduledRetry(Guid.NewGuid(), clock));
        }

        Assert.Equal(BackupStatus.Failed, run.Status);
        Assert.Equal(BackupRetryState.Exhausted, run.RetryState);
        Assert.Equal([1, 2, 3, 4], run.Attempts.Select(a => a.Number));
        Assert.Equal(4, run.Attempts.Select(a => a.Id).Distinct().Count());
        Assert.All(run.Attempts, a => Assert.True(a.IsTerminal));
        Assert.All(run.Attempts, a => Assert.Equal(run.Id, a.RunId));
        Assert.False(run.StartScheduledRetry(Guid.NewGuid(), clock.AddDays(1)));
        Assert.Equal(BackupManualRetryResult.AttemptsExhausted, run.RequestManualRetry(Guid.NewGuid(), clock.AddDays(1)));
        Assert.Equal(4, run.Attempts.Count);
        Assert.Equal(4, run.CurrentAttemptNumber);
    }

    [Fact]
    public void TheFirstAttemptCanOnlyBeQueuedOnce()
    {
        var run = NewRun();
        Assert.Throws<InvalidOperationException>(() => run.QueueFirstAttempt(Guid.NewGuid(), Now));
        Assert.Single(run.Attempts);

        Fail(run, BackupFailureCodes.PermissionDenied, Now);
        Assert.Throws<InvalidOperationException>(() => run.QueueFirstAttempt(Guid.NewGuid(), Now));
        Assert.Single(run.Attempts);
    }

    [Fact]
    public void AnAttemptIdentifierCannotBeReusedByARetry()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        var firstId = run.Attempts[0].Id;

        Assert.Throws<ArgumentException>(() => run.StartScheduledRetry(firstId, run.NextAttemptAt!.Value));

        Assert.Equal(BackupStatus.RetryPending, run.Status);
        Assert.Equal(BackupRetryState.Scheduled, run.RetryState);
        Assert.NotNull(run.NextAttemptAt);
        Assert.Single(run.Attempts);
        Assert.Throws<ArgumentException>(() => run.RequestManualRetry(firstId, Now));
        Assert.Single(run.Attempts);
        Assert.Equal(BackupStatus.RetryPending, run.Status);
    }

    [Fact]
    public void AnEmptyAttemptIdentifierIsRejected()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        Assert.Throws<ArgumentException>(() => run.StartScheduledRetry(Guid.Empty, run.NextAttemptAt!.Value));
        Assert.Single(run.Attempts);
    }

    // ---- immutability and fences ---------------------------------------------------------------------------------------------------

    [Fact]
    public void RepeatingTheSameTerminalCompletionIsANoOpThatDoesNotRescheduleOrDuplicateFacts()
    {
        var policy = Policy(3, jitter: 1);
        var run = NewRun(policy);
        var token = Fail(run, BackupFailureCodes.DatabaseUnavailable, Now, jitter: 0);
        var due = run.NextAttemptAt;
        var facts = run.Facts.Count;

        run.Complete(token, Now.AddSeconds(30), Failed(BackupFailureCodes.DatabaseUnavailable), jitter: 1);

        Assert.Equal(due, run.NextAttemptAt);
        Assert.Equal(facts, run.Facts.Count);
        Assert.Equal(Now, run.CurrentAttempt.CompletedAt);
        Assert.Equal(BackupStatus.RetryPending, run.Status);
    }

    [Fact]
    public void ATerminalAttemptRejectsADifferentOutcomeOrAnotherToken()
    {
        var run = NewRun();
        var token = Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        var facts = run.Facts.Count;

        Assert.Throws<InvalidOperationException>(() => run.Complete(token, Now, Failed(BackupFailureCodes.OperationTimedOut)));
        Assert.Throws<InvalidOperationException>(() => run.Complete(token, Now, new BackupCompletion(BackupStatus.Cancelled, "Cancelled", null)));
        Assert.Throws<InvalidOperationException>(() => run.Complete(Guid.NewGuid(), Now, Failed(BackupFailureCodes.DatabaseUnavailable)));

        Assert.Equal(BackupStatus.Failed, run.CurrentAttempt.Status);
        Assert.Equal(BackupFailureCodes.DatabaseUnavailable, run.CurrentAttempt.TerminalReason);
        Assert.Equal(BackupStatus.RetryPending, run.Status);
        Assert.Equal(facts, run.Facts.Count);
    }

    [Fact]
    public void ATerminalAttemptCannotBeExpiredRenewedOrProgressed()
    {
        var run = NewRun();
        var token = Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        var facts = run.Facts.Count;

        Assert.False(run.Expire(Now.AddDays(1)));
        Assert.Throws<InvalidOperationException>(() => run.Renew(token, Now.AddSeconds(1), Lease));
        Assert.Throws<InvalidOperationException>(() => run.Progress(token, Now.AddSeconds(1), "Uploading", 10));
        Assert.Equal(facts, run.Facts.Count);
        Assert.Equal(BackupStatus.RetryPending, run.Status);
        Assert.Equal(BackupStatus.Failed, run.CurrentAttempt.Status);
    }

    [Fact]
    public void ALostAttemptCannotBeRevivedByALateCompletionFromTheOldAgent()
    {
        var run = NewRun();
        var token = Begin(run, Now);
        Assert.True(run.Expire(Now.Add(Lease)));

        Assert.Throws<InvalidOperationException>(() => run.Complete(token, Now.Add(Lease).AddSeconds(1), Failed(BackupFailureCodes.DatabaseUnavailable)));
        var artifactless = new BackupCompletion(BackupStatus.Cancelled, "Cancelled", null);
        Assert.Throws<InvalidOperationException>(() => run.Complete(token, Now.Add(Lease).AddSeconds(1), artifactless));

        Assert.Equal(BackupStatus.AgentLost, run.CurrentAttempt.Status);
        Assert.Equal(BackupStatus.RetryPending, run.Status);
    }

    [Fact]
    public void AnOldAttemptsTokenCannotDriveTheNewAttempt()
    {
        var run = NewRun();
        var oldToken = Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), run.NextAttemptAt!.Value));
        var later = run.CurrentAttempt.CreatedAt;

        Assert.Throws<InvalidOperationException>(() => run.Start(oldToken, later, "1.0", "1.0", "Linux"));
        Assert.Throws<InvalidOperationException>(() => run.Complete(oldToken, later, Failed(BackupFailureCodes.DatabaseUnavailable)));
        Assert.Throws<InvalidOperationException>(() => run.Renew(oldToken, later, Lease));
        Assert.Throws<InvalidOperationException>(() => run.Progress(oldToken, later, "Uploading", 1));

        Assert.Equal(BackupStatus.Pending, run.Status);
        Assert.Equal(BackupStatus.Pending, run.CurrentAttempt.Status);
        Assert.Equal(BackupStatus.Failed, run.Attempts[0].Status);

        var newToken = Begin(run, later);
        Assert.NotEqual(oldToken, newToken);
        Assert.Throws<InvalidOperationException>(() => run.Complete(oldToken, later, Failed(BackupFailureCodes.DatabaseUnavailable)));
        Assert.Equal(BackupStatus.Running, run.Status);
    }

    [Fact]
    public void LeaseFencesRejectWrongTokensExpiredLeasesAndOutOfOrderTransitions()
    {
        var run = NewRun();
        Assert.Throws<InvalidOperationException>(() => run.Start(Guid.NewGuid(), Now, "1.0", "1.0", "Linux"));
        var token = Guid.NewGuid();
        run.Claim(token, Now, Lease);
        Assert.Throws<InvalidOperationException>(() => run.Claim(Guid.NewGuid(), Now, Lease));
        Assert.Throws<InvalidOperationException>(() => run.Start(Guid.NewGuid(), Now, "1.0", "1.0", "Linux"));
        Assert.Throws<InvalidOperationException>(() => run.Start(Guid.Empty, Now, "1.0", "1.0", "Linux"));
        run.Start(token, Now, "1.0", "1.0", "Linux");

        Assert.Throws<InvalidOperationException>(() => run.Renew(Guid.NewGuid(), Now, Lease));
        Assert.Throws<InvalidOperationException>(() => run.Progress(Guid.NewGuid(), Now, "Uploading", 1));
        Assert.Throws<InvalidOperationException>(() => run.Complete(Guid.NewGuid(), Now, Failed(BackupFailureCodes.DatabaseUnavailable)));
        Assert.Throws<InvalidOperationException>(() => run.Complete(Guid.Empty, Now, Failed(BackupFailureCodes.DatabaseUnavailable)));
        var expiry = Now.Add(Lease);
        Assert.Throws<InvalidOperationException>(() => run.Renew(token, expiry, Lease));
        Assert.Throws<InvalidOperationException>(() => run.Complete(token, expiry, Failed(BackupFailureCodes.DatabaseUnavailable)));
        Assert.Equal(BackupStatus.Running, run.Status);
        Assert.DoesNotContain(run.Facts, fact => fact.Kind is "BackupRetryScheduled" or "BackupRunFailed");
    }

    [Fact]
    public void ARunWaitingForItsRetryCannotBeClaimed()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        Assert.Throws<InvalidOperationException>(() => run.Claim(Guid.NewGuid(), run.NextAttemptAt!.Value.AddHours(1), Lease));
        Assert.Equal(BackupStatus.RetryPending, run.Status);
        Assert.Single(run.Attempts);
    }

    // ---- manual retry --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ManualRetryOfASucceededRunIsRefused()
    {
        var run = NewRun();
        Succeed(run, Begin(run, Now), Now.AddSeconds(1));
        Assert.Equal(BackupManualRetryResult.Succeeded, run.RequestManualRetry(Guid.NewGuid(), Now.AddMinutes(1)));
        Assert.Single(run.Attempts);
        Assert.Equal(BackupStatus.Succeeded, run.Status);
    }

    [Fact]
    public void ManualRetryOfACancelledRunIsRefused()
    {
        var run = NewRun();
        run.RequestCancellation(Now);
        Assert.Equal(BackupManualRetryResult.Cancelled, run.RequestManualRetry(Guid.NewGuid(), Now.AddMinutes(1)));
        Assert.Single(run.Attempts);
        Assert.Equal(BackupStatus.Cancelled, run.Status);
    }

    [Fact]
    public void ManualRetryOfARunCancelledAfterAFailureIsRefused()
    {
        var run = NewRun();
        var token = Begin(run, Now);
        run.RequestCancellation(Now.AddSeconds(1));
        run.Complete(token, Now.AddSeconds(2), Failed(BackupFailureCodes.DatabaseUnavailable));
        Assert.Equal(BackupManualRetryResult.Cancelled, run.RequestManualRetry(Guid.NewGuid(), Now.AddMinutes(1)));
        Assert.Single(run.Attempts);
    }

    [Fact]
    public void ManualRetryWhileAnAttemptIsQueuedLeasedOrRunningIsRefusedAndAddsNothing()
    {
        var run = NewRun();
        Assert.Equal(BackupStatus.Pending, run.Status);
        Assert.Equal(BackupManualRetryResult.InProgress, run.RequestManualRetry(Guid.NewGuid(), Now));

        var token = Guid.NewGuid();
        run.Claim(token, Now, Lease);
        Assert.Equal(BackupStatus.Leased, run.Status);
        Assert.Equal(BackupManualRetryResult.InProgress, run.RequestManualRetry(Guid.NewGuid(), Now));

        run.Start(token, Now, "1.0", "1.0", "Linux");
        Assert.Equal(BackupStatus.Running, run.Status);
        Assert.Equal(BackupManualRetryResult.InProgress, run.RequestManualRetry(Guid.NewGuid(), Now));

        Assert.Single(run.Attempts);
        Assert.Equal(0, Count(run, "BackupRetryStarted"));
    }

    [Fact]
    public void ManualRetryOfARetryPendingRunMeansRetryNowAndCannotBeDuplicatedByTheScheduler()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        var due = run.NextAttemptAt!.Value;
        var firstId = run.CurrentAttempt.Id;
        var requestedAt = Now.AddSeconds(2);
        Assert.True(requestedAt < due);
        var secondId = Guid.NewGuid();

        Assert.Equal(BackupManualRetryResult.Started, run.RequestManualRetry(secondId, requestedAt));

        Assert.Equal(BackupStatus.Pending, run.Status);
        Assert.Equal(2, run.CurrentAttemptNumber);
        Assert.Equal(secondId, run.CurrentAttempt.Id);
        Assert.Equal(requestedAt, run.CurrentAttempt.EligibleAt);
        Assert.Equal(BackupRetryState.None, run.RetryState);
        Assert.Null(run.NextAttemptAt);
        Assert.Null(run.CompletedAt);
        Assert.Equal(1, Count(run, "BackupRetryStarted"));

        // The scheduled retry that was waiting is gone: the scheduler adds nothing, and neither does a second click.
        Assert.False(run.StartScheduledRetry(Guid.NewGuid(), due.AddMinutes(1)));
        Assert.Equal(BackupManualRetryResult.InProgress, run.RequestManualRetry(Guid.NewGuid(), due.AddMinutes(1)));
        Assert.Equal(2, run.Attempts.Count);
        Assert.Equal(firstId, run.Attempts[0].Id);
        Assert.Equal(BackupStatus.Failed, run.Attempts[0].Status);
    }

    [Fact]
    public void ManualRetryAfterTheSchedulerAlreadyStartedTheRetryIsRefusedAsInProgress()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), run.NextAttemptAt!.Value));
        Assert.Equal(BackupManualRetryResult.InProgress, run.RequestManualRetry(Guid.NewGuid(), run.CurrentAttempt.CreatedAt));
        Assert.Equal(2, run.Attempts.Count);
    }

    [Theory]
    [MemberData(nameof(NonRetryableCodes))]
    public void ManualRetryReopensAFailedRunThatHasBudgetLeft(string code)
    {
        var run = NewRun(Policy(3));
        Fail(run, code, Now);
        Assert.Equal(BackupStatus.Failed, run.Status);
        var retryAt = Now.AddHours(2);
        var secondId = Guid.NewGuid();

        Assert.Equal(BackupManualRetryResult.Started, run.RequestManualRetry(secondId, retryAt));

        Assert.Equal(BackupStatus.Pending, run.Status);
        Assert.False(run.IsTerminal);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.TerminalReason);
        Assert.Equal(BackupRetryState.None, run.RetryState);
        Assert.Equal(2, run.CurrentAttemptNumber);
        Assert.Equal(secondId, run.CurrentAttempt.Id);
        Assert.Equal(retryAt, run.CurrentAttempt.EligibleAt);
        Assert.Equal(1, Count(run, "BackupRetryStarted"));
        // History: the failed attempt is untouched.
        Assert.Equal(BackupStatus.Failed, run.Attempts[0].Status);
        Assert.Equal(code, run.Attempts[0].TerminalReason);
        Assert.Equal(Now, run.Attempts[0].CompletedAt);
        // The reopened run executes like any other.
        var token = Begin(run, retryAt);
        Succeed(run, token, retryAt.AddSeconds(1));
        Assert.Equal(BackupStatus.Succeeded, run.Status);
    }

    [Fact]
    public void ManualRetryIsRefusedOnceTheBudgetIsSpentWhetherFailedOrAgentLost()
    {
        var nonRetryableSingle = NewRun(Policy(1));
        Fail(nonRetryableSingle, BackupFailureCodes.SqlBackupFailed, Now);
        Assert.Equal(BackupManualRetryResult.AttemptsExhausted, nonRetryableSingle.RequestManualRetry(Guid.NewGuid(), Now.AddMinutes(1)));

        var exhausted = NewRun(Policy(1));
        Fail(exhausted, BackupFailureCodes.DatabaseUnavailable, Now);
        Assert.Equal(BackupManualRetryResult.AttemptsExhausted, exhausted.RequestManualRetry(Guid.NewGuid(), Now.AddMinutes(1)));

        var lost = NewRun(Policy(1));
        Begin(lost, Now);
        lost.Expire(Now.Add(Lease));
        Assert.Equal(BackupStatus.AgentLost, lost.Status);
        Assert.Equal(BackupManualRetryResult.AttemptsExhausted, lost.RequestManualRetry(Guid.NewGuid(), Now.AddHours(1)));

        foreach (var run in new[] { nonRetryableSingle, exhausted, lost })
        {
            Assert.Single(run.Attempts);
            Assert.True(run.IsTerminal);
            Assert.Equal(0, Count(run, "BackupRetryStarted"));
        }
    }

    [Fact]
    public void ManualRetriesAreBoundedByTheSamePolicyBudgetAsAutomaticOnes()
    {
        var run = NewRun(Policy(2));
        Fail(run, BackupFailureCodes.InternalError, Now);
        Assert.Equal(BackupManualRetryResult.Started, run.RequestManualRetry(Guid.NewGuid(), Now.AddMinutes(1)));
        Fail(run, BackupFailureCodes.InternalError, Now.AddMinutes(2));

        Assert.Equal(BackupStatus.Failed, run.Status);
        Assert.Equal(BackupManualRetryResult.AttemptsExhausted, run.RequestManualRetry(Guid.NewGuid(), Now.AddMinutes(3)));
        Assert.Equal(2, run.Attempts.Count);
    }

    [Fact]
    public void ManualRetryOfALostAgentRunWithBudgetLeftReplacesTheScheduledRetry()
    {
        var run = NewRun(Policy(3));
        Begin(run, Now);
        run.Expire(Now.Add(Lease));
        Assert.Equal(BackupStatus.RetryPending, run.Status);

        Assert.Equal(BackupManualRetryResult.Started, run.RequestManualRetry(Guid.NewGuid(), Now.Add(Lease).AddSeconds(1)));
        Assert.Equal(2, run.Attempts.Count);
        Assert.Equal(BackupStatus.AgentLost, run.Attempts[0].Status);
        Assert.Equal(BackupStatus.Pending, run.Status);
    }

    [Fact]
    public void AReopenedRunCanBeCancelledLikeAnyPendingRun()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.HashFailed, Now);
        Assert.Equal(BackupManualRetryResult.Started, run.RequestManualRetry(Guid.NewGuid(), Now.AddMinutes(1)));
        run.RequestCancellation(Now.AddMinutes(2));
        Assert.Equal(BackupStatus.Cancelled, run.Status);
        Assert.Equal(BackupStatus.Cancelled, run.CurrentAttempt.Status);
        Assert.Equal(BackupStatus.Failed, run.Attempts[0].Status);
        Assert.Equal(2, run.Attempts.Count);
    }

    // ---- policy snapshot -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void RunSnapshotsTheJobsRetryAndOverlapPolicyAtCreation()
    {
        var policy = Policy(2, BackupBackoffStrategy.Fixed, jitter: 0.1);
        var job = new BackupJob(Guid.NewGuid(), TenantId.New(), "Nightly", AgentId.New(), Configuration(), Now, policy, BackupOverlapPolicy.Queue);
        var run = new BackupRun(Guid.NewGuid(), job.TenantId, job.Id, job.AgentId, job.Configuration, "key", Now, job.RetryPolicy, job.OverlapPolicy);
        run.QueueFirstAttempt(Guid.NewGuid(), Now);

        Assert.Equal(policy, run.RetryPolicy);
        Assert.Equal(BackupOverlapPolicy.Queue, run.OverlapPolicy);

        // A run created while the job used another policy keeps its own: behaviour follows the snapshot, not the job.
        var singleShot = new BackupRun(Guid.NewGuid(), job.TenantId, job.Id, job.AgentId, job.Configuration, "key2", Now, Policy(1));
        singleShot.QueueFirstAttempt(Guid.NewGuid(), Now);
        Fail(singleShot, BackupFailureCodes.DatabaseUnavailable, Now);
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        Assert.Equal(BackupStatus.Failed, singleShot.Status);
        Assert.Equal(BackupStatus.RetryPending, run.Status);
        Assert.Equal(Now.AddSeconds(10), run.NextAttemptAt);
    }

    [Fact]
    public void RunAndJobDefaultToThePlatformPolicyAndSkipOverlap()
    {
        var job = new BackupJob(Guid.NewGuid(), TenantId.New(), "Nightly", AgentId.New(), Configuration(), Now);
        Assert.Equal(BackupRetryPolicy.Default, job.RetryPolicy);
        Assert.Equal(BackupOverlapPolicy.Skip, job.OverlapPolicy);
        var run = new BackupRun(Guid.NewGuid(), job.TenantId, job.Id, job.AgentId, job.Configuration, "key", Now);
        Assert.Equal(BackupRetryPolicy.Default, run.RetryPolicy);
        Assert.Equal(BackupOverlapPolicy.Skip, run.OverlapPolicy);
    }

    [Fact]
    public void RunAndJobRejectAnInvalidPolicyOrOverlapAtConstruction()
    {
        var invalid = BackupRetryPolicy.Default with { MaxAttempts = 0 };
        Assert.Throws<ArgumentException>(() => new BackupRun(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), AgentId.New(), Configuration(), "key", Now, invalid));
        Assert.Throws<ArgumentException>(() => new BackupRun(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), AgentId.New(), Configuration(), "key", Now, null, (BackupOverlapPolicy)0));
        Assert.Throws<ArgumentException>(() => new BackupJob(Guid.NewGuid(), TenantId.New(), "Nightly", AgentId.New(), Configuration(), Now, invalid));
        Assert.Throws<ArgumentException>(() => new BackupJob(Guid.NewGuid(), TenantId.New(), "Nightly", AgentId.New(), Configuration(), Now, null, (BackupOverlapPolicy)9));
    }
}
