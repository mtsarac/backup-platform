using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using static BackupPlatform.UnitTests.BackupRunTestSupport;

namespace BackupPlatform.UnitTests;

public sealed class BackupOrchestratorMappingTests
{
    public static TheoryData<BackupRetryPolicyRequest?> RequestsThatKeepEveryDefault => new()
    {
        null,
        new BackupRetryPolicyRequest(),
    };

    [Theory]
    [MemberData(nameof(RequestsThatKeepEveryDefault))]
    public void MissingOrEmptyRetryPolicyRequestMeansThePlatformDefault(BackupRetryPolicyRequest? request) =>
        Assert.Equal(BackupRetryPolicy.Default, BackupOrchestrator.ToDomain(request));

    [Fact]
    public void PartialRequestOverridesOnlyTheSuppliedMembers()
    {
        var policy = BackupOrchestrator.ToDomain(new BackupRetryPolicyRequest(MaxAttempts: 7));
        Assert.Equal(BackupRetryPolicy.Default with { MaxAttempts = 7 }, policy);

        policy = BackupOrchestrator.ToDomain(new BackupRetryPolicyRequest(Backoff: "fixed", Jitter: 0));
        Assert.Equal(BackupRetryPolicy.Default with { Backoff = BackupBackoffStrategy.Fixed, Jitter = 0 }, policy);
    }

    [Fact]
    public void FullRequestMapsEveryMemberAndSecondsBecomeTimeSpans()
    {
        var policy = BackupOrchestrator.ToDomain(new BackupRetryPolicyRequest(4, 2.5, 90, "Exponential", 0.5));
        Assert.Equal(4, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2.5), policy.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(90), policy.MaxDelay);
        Assert.Equal(BackupBackoffStrategy.Exponential, policy.Backoff);
        Assert.Equal(0.5, policy.Jitter);
    }

    [Theory]
    [InlineData("fixed", BackupBackoffStrategy.Fixed)]
    [InlineData("FIXED", BackupBackoffStrategy.Fixed)]
    [InlineData("Exponential", BackupBackoffStrategy.Exponential)]
    [InlineData("exponential", BackupBackoffStrategy.Exponential)]
    public void BackoffNamesAreCaseInsensitive(string name, BackupBackoffStrategy expected) =>
        Assert.Equal(expected, BackupOrchestrator.ToDomain(new BackupRetryPolicyRequest(Backoff: name)).Backoff);

    [Theory]
    [InlineData("Linear")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("Fixed,Exponential")]
    public void UnknownOrNumericBackoffIsRejected(string backoff) =>
        Assert.Throws<ArgumentException>(() => BackupOrchestrator.ToDomain(new BackupRetryPolicyRequest(Backoff: backoff)));

    [Theory]
    [InlineData(-1)]
    [InlineData(0.5)]
    [InlineData(86_401)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InitialDelayOutsideTheAllowedRangeIsRejected(double seconds) =>
        Assert.Throws<ArgumentException>(() => BackupOrchestrator.ToDomain(new BackupRetryPolicyRequest(InitialDelaySeconds: seconds)));

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(86_401)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MaxDelayOutsideTheAllowedRangeOrBelowTheInitialDelayIsRejected(double seconds) =>
        Assert.Throws<ArgumentException>(() => BackupOrchestrator.ToDomain(new BackupRetryPolicyRequest(InitialDelaySeconds: 10, MaxDelaySeconds: seconds)));

    [Fact]
    public void PartialRequestIsValidatedAgainstTheDefaultsItInherits() =>
        // Default MaxDelay is 10 minutes, so a 20 minute initial delay alone leaves MaxDelay below InitialDelay.
        Assert.Throws<ArgumentException>(() => BackupOrchestrator.ToDomain(new BackupRetryPolicyRequest(InitialDelaySeconds: 1_200)));

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-3)]
    public void MaxAttemptsOutsideOneToTenIsRejected(int attempts) =>
        Assert.Throws<ArgumentException>(() => BackupOrchestrator.ToDomain(new BackupRetryPolicyRequest(MaxAttempts: attempts)));

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void JitterOutsideTheUnitIntervalIsRejected(double jitter) =>
        Assert.Throws<ArgumentException>(() => BackupOrchestrator.ToDomain(new BackupRetryPolicyRequest(Jitter: jitter)));

    [Fact]
    public void MissingOverlapPolicyMeansSkip() => Assert.Equal(BackupOverlapPolicy.Skip, BackupOrchestrator.ToDomain((string?)null));

    [Theory]
    [InlineData("Skip", BackupOverlapPolicy.Skip)]
    [InlineData("skip", BackupOverlapPolicy.Skip)]
    [InlineData("QUEUE", BackupOverlapPolicy.Queue)]
    [InlineData("Queue", BackupOverlapPolicy.Queue)]
    public void OverlapPolicyNamesAreCaseInsensitive(string name, BackupOverlapPolicy expected) =>
        Assert.Equal(expected, BackupOrchestrator.ToDomain(name));

    [Theory]
    [InlineData("")]
    [InlineData("Replace")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("Skip,Queue")]
    public void UnknownOrNumericOverlapPolicyIsRejected(string overlap) =>
        Assert.Throws<ArgumentException>(() => BackupOrchestrator.ToDomain(overlap));

    [Fact]
    public void ViewOfAFreshRunHasNoFailureAndCountsTheFirstAttempt()
    {
        var run = NewRun(Policy(maxAttempts: 4), BackupOverlapPolicy.Queue);
        var view = BackupOrchestrator.ToView(run);

        Assert.Equal("Pending", view.Status);
        Assert.Equal(1, view.CurrentAttemptNumber);
        var retry = view.Retry;
        Assert.Equal(4, retry.MaxAttempts);
        Assert.Equal(1, retry.AttemptsUsed);
        Assert.Equal("None", retry.State);
        Assert.Null(retry.NextAttemptAt);
        Assert.Null(retry.LastFailureCode);
        Assert.Null(retry.LastFailureReason);
        Assert.Null(retry.LastFailureRetryable);
        Assert.Equal("Queue", retry.OverlapPolicy);
        Assert.Equal(new BackupRetryPolicyView(4, 10, 600, "Exponential", 0), retry.Policy);
        Assert.Null(Assert.Single(view.Attempts).Retryable);
    }

    [Fact]
    public void ViewOfARetryableFailureShowsAScheduledRetryWithoutTerminatingTheRun()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now.AddMinutes(1));
        var view = BackupOrchestrator.ToView(run);

        Assert.Equal("RetryPending", view.Status);
        Assert.Null(view.CompletedAt);
        Assert.Null(view.ReasonCode);
        Assert.Equal("Scheduled", view.Retry.State);
        Assert.Equal(Now.AddMinutes(1).AddSeconds(10), view.Retry.NextAttemptAt);
        Assert.Equal(1, view.Retry.AttemptsUsed);
        Assert.Equal(BackupFailureCodes.DatabaseUnavailable, view.Retry.LastFailureCode);
        Assert.NotEmpty(view.Retry.LastFailureReason!);
        Assert.True(view.Retry.LastFailureRetryable);
        var attempt = Assert.Single(view.Attempts);
        Assert.Equal("Failed", attempt.Status);
        Assert.True(attempt.Retryable);
    }

    [Fact]
    public void ViewOfANonRetryableFailureIsTerminalAndMarkedNotRetryable()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.PermissionDenied, Now);
        var view = BackupOrchestrator.ToView(run);

        Assert.Equal("Failed", view.Status);
        Assert.Equal(BackupFailureCodes.PermissionDenied, view.ReasonCode);
        Assert.Equal("NotRetryable", view.Retry.State);
        Assert.Null(view.Retry.NextAttemptAt);
        Assert.Equal(BackupFailureCodes.PermissionDenied, view.Retry.LastFailureCode);
        Assert.False(view.Retry.LastFailureRetryable);
        Assert.False(Assert.Single(view.Attempts).Retryable);
    }

    [Fact]
    public void ViewOfAnExhaustedRunReportsMaxAttemptsAsTotalAttemptsUsed()
    {
        var run = NewRun(Policy(maxAttempts: 2));
        Fail(run, BackupFailureCodes.OperationTimedOut, Now);
        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), run.NextAttemptAt!.Value));
        Fail(run, BackupFailureCodes.OperationTimedOut, Now.AddMinutes(5));
        var view = BackupOrchestrator.ToView(run);

        Assert.Equal("Failed", view.Status);
        Assert.Equal(2, view.Retry.MaxAttempts);
        Assert.Equal(2, view.Retry.AttemptsUsed);
        Assert.Equal("Exhausted", view.Retry.State);
        // The last failure is still classified retryable: it is the budget, not the cause, that ended the run.
        Assert.True(view.Retry.LastFailureRetryable);
        Assert.Equal([1, 2], view.Attempts.Select(a => a.Number));
        Assert.All(view.Attempts, a => Assert.True(a.Retryable));
    }

    [Fact]
    public void ViewKeepsTheLastFailureWhileTheNextAttemptIsPending()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.StorageUnavailable, Now);
        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), run.NextAttemptAt!.Value));
        var view = BackupOrchestrator.ToView(run);

        Assert.Equal("Pending", view.Status);
        Assert.Equal(2, view.Retry.AttemptsUsed);
        Assert.Equal("None", view.Retry.State);
        Assert.Null(view.Retry.NextAttemptAt);
        Assert.Equal(BackupFailureCodes.StorageUnavailable, view.Retry.LastFailureCode);
        Assert.True(view.Retry.LastFailureRetryable);
        Assert.True(view.Attempts[0].Retryable);
        Assert.Null(view.Attempts[1].Retryable);
    }

    [Fact]
    public void ViewOfALostAgentReportsAgentLostAsARetryableFailure()
    {
        var run = NewRun();
        Begin(run, Now);
        Assert.True(run.Expire(Now.AddMinutes(3)));
        var view = BackupOrchestrator.ToView(run);

        Assert.Equal("RetryPending", view.Status);
        Assert.Equal(BackupFailureCodes.AgentLost, view.Retry.LastFailureCode);
        Assert.True(view.Retry.LastFailureRetryable);
        Assert.Equal("AgentLost", Assert.Single(view.Attempts).Status);
    }

    [Fact]
    public void ViewOfASuccessAfterARetryKeepsFailureHistoryButNoRetryIsPending()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        Assert.True(run.StartScheduledRetry(Guid.NewGuid(), run.NextAttemptAt!.Value));
        var token = Begin(run, run.CurrentAttempt.CreatedAt);
        Succeed(run, token, run.CurrentAttempt.CreatedAt.AddSeconds(5));
        var view = BackupOrchestrator.ToView(run);

        Assert.Equal("Succeeded", view.Status);
        Assert.Equal("None", view.Retry.State);
        Assert.Null(view.Retry.NextAttemptAt);
        Assert.Equal(2, view.Retry.AttemptsUsed);
        Assert.Equal(BackupFailureCodes.DatabaseUnavailable, view.Retry.LastFailureCode);
        Assert.Null(view.Attempts[1].Retryable);
        Assert.Single(view.Attempts[1].Artifacts);
    }

    [Fact]
    public void ViewOfARunCancelledWhileRetryPendingShowsNoScheduledRetry()
    {
        var run = NewRun();
        Fail(run, BackupFailureCodes.DatabaseUnavailable, Now);
        run.RequestCancellation(Now.AddSeconds(1));
        var view = BackupOrchestrator.ToView(run);

        Assert.Equal("Cancelled", view.Status);
        Assert.Equal(BackupFailureCodes.Cancelled, view.ReasonCode);
        Assert.Equal("None", view.Retry.State);
        Assert.Null(view.Retry.NextAttemptAt);
        Assert.Equal(Now.AddSeconds(1), view.CancellationRequestedAt);
    }

    [Fact]
    public void PolicyViewRoundTripsThroughTheRequestMapping()
    {
        var original = new BackupRetryPolicy
        {
            MaxAttempts = 6,
            InitialDelay = TimeSpan.FromSeconds(3),
            MaxDelay = TimeSpan.FromMinutes(7),
            Backoff = BackupBackoffStrategy.Fixed,
            Jitter = 0.4
        };
        var view = BackupOrchestrator.ToView(original);
        var back = BackupOrchestrator.ToDomain(new BackupRetryPolicyRequest(view.MaxAttempts, view.InitialDelaySeconds, view.MaxDelaySeconds, view.Backoff, view.Jitter));
        Assert.Equal(original, back);
    }
}
