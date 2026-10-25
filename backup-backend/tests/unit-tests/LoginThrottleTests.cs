using BackupPlatform.Api.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BackupPlatform.UnitTests;

public sealed class LoginThrottleTests
{
    [Fact]
    public void Locks_after_max_failures_until_the_window_passes_and_a_success_clears()
    {
        var clock = new FakeTimeProvider();
        var throttle = new LoginThrottle(clock);
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) throttle.Failed("Owner");
        Assert.Null(throttle.RetryAfter("owner"));
        throttle.Failed("OWNER");
        Assert.NotNull(throttle.RetryAfter("owner"));
        Assert.Null(throttle.RetryAfter("other"));
        clock.Advance(LoginThrottle.Window);
        Assert.Null(throttle.RetryAfter("owner"));
        for (var i = 0; i < LoginThrottle.MaxFailures; i++) throttle.Failed("owner");
        throttle.Succeeded("owner");
        Assert.Null(throttle.RetryAfter("owner"));
    }
}
