using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using static BackupPlatform.UnitTests.BackupRunTestSupport;

namespace BackupPlatform.UnitTests;

public sealed class BackupRunNetworkResumeTests
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(10);

    private static BackupRun Paused(TransferPauseReason reason, int maxAttempts = 3, DateTimeOffset? at = null)
    {
        var run = NewRun(Policy(maxAttempts));
        var pausedAt = at ?? Now.AddSeconds(30);
        run.Pause(Begin(run, Now), pausedAt, reason);
        return run;
    }

    [Fact]
    public void A_network_pause_resumes_the_same_attempt_once_the_backoff_has_elapsed()
    {
        var run = Paused(TransferPauseReason.PausedByNetworkError);
        var pausedAt = run.CurrentAttempt.ProgressAt!.Value;
        var attempt = run.CurrentAttempt.Id;

        Assert.False(run.ResumeAfterNetworkPause(pausedAt + FirstDelay - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(BackupStatus.Paused, run.Status);

        Assert.True(run.ResumeAfterNetworkPause(pausedAt + FirstDelay));
        Assert.Equal(BackupStatus.Pending, run.Status);
        Assert.Equal(BackupStatus.Pending, run.CurrentAttempt.Status);
        Assert.Equal(attempt, run.CurrentAttempt.Id);
        Assert.Null(run.CurrentAttempt.PauseReason);
        Assert.Contains(run.Facts, fact => fact.Kind == "BackupNetworkResumeStarted" && fact.AttemptId == attempt);
        Assert.False(run.ResumeAfterNetworkPause(pausedAt + FirstDelay), "a resumed run is no longer paused");
    }

    [Fact]
    public void Each_further_network_pause_waits_longer_and_the_number_of_automatic_resumes_is_bounded()
    {
        var run = Paused(TransferPauseReason.PausedByNetworkError);
        var now = run.CurrentAttempt.ProgressAt!.Value;
        for (var resumed = 1; resumed <= BackupRun.MaxNetworkResumes; resumed++)
        {
            var delay = Policy().DelayAfter(resumed, 0);
            Assert.False(run.ResumeAfterNetworkPause(now + delay - TimeSpan.FromMilliseconds(1)));
            now += delay;
            Assert.True(run.ResumeAfterNetworkPause(now));
            var token = Guid.NewGuid();
            run.Claim(token, now, Lease);
            run.Start(token, now, "1.0", "1.0", "Linux");
            now += TimeSpan.FromSeconds(1);
            run.Pause(token, now, TransferPauseReason.PausedByNetworkError);
        }

        Assert.False(run.ResumeAfterNetworkPause(now + TimeSpan.FromDays(1)));
        Assert.Equal(BackupStatus.Paused, run.Status);
        run.Resume(now); // an operator still can
        Assert.Equal(BackupStatus.Pending, run.Status);
    }

    [Theory]
    [InlineData(TransferPauseReason.Operator)]
    [InlineData(TransferPauseReason.TimeRule)]
    public void Operator_and_time_rule_pauses_never_resume_by_themselves(TransferPauseReason reason)
    {
        var run = Paused(reason);

        Assert.False(run.ResumeAfterNetworkPause(Now.AddDays(30)));
        Assert.Equal(BackupStatus.Paused, run.Status);
    }

    [Fact]
    public void A_retry_policy_of_one_attempt_disables_automatic_resume()
    {
        var run = Paused(TransferPauseReason.PausedByNetworkError, maxAttempts: 1);

        Assert.False(run.ResumeAfterNetworkPause(Now.AddDays(1)));
    }

    [Fact]
    public void A_lease_that_expired_with_a_retained_transfer_is_a_network_pause_and_resumes()
    {
        var run = NewRun();
        Begin(run, Now);
        var expiredAt = Now + Lease + TimeSpan.FromSeconds(5);
        Assert.True(run.Expire(expiredAt, retainTransfer: true));
        Assert.Equal(TransferPauseReason.PausedByNetworkError, run.CurrentAttempt.PauseReason);

        Assert.True(run.ResumeAfterNetworkPause(expiredAt + FirstDelay));
        Assert.Equal(BackupStatus.Pending, run.Status);
    }

    [Fact]
    public void A_cancelled_run_is_never_resumed()
    {
        var run = Paused(TransferPauseReason.PausedByNetworkError);
        run.RequestCancellation(Now.AddMinutes(1));

        Assert.Equal(BackupStatus.Cancelled, run.Status);
        Assert.False(run.ResumeAfterNetworkPause(Now.AddDays(1)));
        Assert.Equal(BackupStatus.Cancelled, run.Status);
    }

    [Fact]
    public void A_resumed_scheduled_run_past_its_claim_deadline_is_not_dropped_as_unclaimed()
    {
        var occurrence = Now;
        var run = new BackupRun(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), AgentId.New(), Configuration(), "scheduled", Now, Policy(),
            BackupOverlapPolicy.Skip, BackupTriggerTypes.Scheduled, occurrence, occurrence + TimeSpan.FromMinutes(5));
        run.QueueFirstAttempt(Guid.NewGuid(), Now);
        run.Pause(Begin(run, Now), Now.AddSeconds(30), TransferPauseReason.PausedByNetworkError);
        var afterGrace = Now + TimeSpan.FromMinutes(10);

        Assert.True(run.ResumeAfterNetworkPause(afterGrace));
        Assert.False(run.ExpireUnclaimed(afterGrace));
        Assert.Equal(BackupStatus.Pending, run.Status);
    }
}
