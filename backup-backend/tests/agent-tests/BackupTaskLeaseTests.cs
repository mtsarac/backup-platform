using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Tasks;

namespace BackupPlatform.AgentTests;

public sealed class BackupTaskLeaseTests
{
    [Theory]
    [InlineData(300, 10_000)]
    [InlineData(21, 7_000)]
    [InlineData(3, 1_000)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void RenewalDelayIsAThirdOfTheRemainingLeaseBetweenOneMillisecondAndTheInterval(int remainingSeconds, int expectedMilliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), BackupTaskLease.RenewalDelay(TimeSpan.FromSeconds(remainingSeconds)));
    }

    [Theory]
    [InlineData(AgentBackendFailureKind.Transient, true)]
    [InlineData(AgentBackendFailureKind.RateLimited, true)]
    [InlineData(AgentBackendFailureKind.Unauthorized, false)]
    public void OnlyTransientAndRateLimitedFailuresAreRetried(AgentBackendFailureKind kind, bool retryable)
    {
        Assert.Equal(retryable, BackupTaskLease.IsRetryable(new AgentBackendFailure(kind, "x")));
    }
}
