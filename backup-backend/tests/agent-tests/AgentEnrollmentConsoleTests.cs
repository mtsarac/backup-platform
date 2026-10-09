using BackupPlatform.Agent.Host;
using BackupPlatform.AgentTests.Support;

namespace BackupPlatform.AgentTests;

[CollectionDefinition("Agent enrollment console", DisableParallelization = true)]
public sealed class AgentEnrollmentConsoleCollection { }

[Collection("Agent enrollment console")]
public sealed class AgentEnrollmentConsoleTests
{
    [Fact]
    public async Task Command_errors_return_failure_without_writing_token_or_credential_to_console()
    {
        var stdout = Console.Out;
        var stderr = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var code = await AgentEnrollmentCommand.RunAsync(["enroll", "--server", "http://backend.test/?secret=" + TestIdentity.Secret,
                "--token", TestIdentity.RotatedSecret, "--non-interactive"], CancellationToken.None);
            Assert.Equal(1, code);
            Assert.NotEmpty(error.ToString());
            Assert.DoesNotContain(TestIdentity.Secret, error.ToString() + output, StringComparison.Ordinal);
            Assert.DoesNotContain(TestIdentity.RotatedSecret, error.ToString() + output, StringComparison.Ordinal);
            Assert.Contains("HTTPS", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
    }
}
