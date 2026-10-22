using System.Security.Principal;

namespace BackupPlatform.Agent.Platform.Windows.Tests;

public sealed class WindowsNetworkSessionTests
{
    [WindowsFact]
    public async Task Outbound_session_flows_across_await_and_restores_the_callers_identity_on_failure()
    {
        using var original = WindowsIdentity.GetCurrent();
        var failure = new IOException("Operation failed inside the outbound session.");
        var session = new WindowsNetworkSession();
        var caught = await Assert.ThrowsAsync<IOException>(() => session.RunAsync<int>(Environment.UserName,
            "unused-outbound-test-password", Environment.UserDomainName, async () =>
            {
                await Task.Yield();
                using var impersonated = WindowsIdentity.GetCurrent();
                Assert.NotEqual(TokenImpersonationLevel.None, impersonated.ImpersonationLevel);
                throw failure;
            }));
        Assert.Same(failure, caught);
        using var restored = WindowsIdentity.GetCurrent();
        Assert.Equal(original.User, restored.User);
        Assert.Equal(original.ImpersonationLevel, restored.ImpersonationLevel);
    }
}
