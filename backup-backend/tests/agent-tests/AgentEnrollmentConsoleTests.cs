using BackupPlatform.Agent.Host;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.AgentTests;

[CollectionDefinition("Agent enrollment console", DisableParallelization = true)]
public sealed class AgentEnrollmentConsoleCollection { }

[Collection("Agent enrollment console")]
public sealed class AgentEnrollmentConsoleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Environment_token_supports_unattended_enrollment_without_replacing_an_existing_identity(bool explicitToken)
    {
        const string variable = "BACKUP_AGENT_ENROLLMENT_TOKEN";
        var previousToken = Environment.GetEnvironmentVariable(variable);
        var stderr = Console.Error;
        using var error = new StringWriter();
        var directory = Path.Combine(Path.GetTempPath(), $"agent-env-enroll-{Guid.NewGuid():N}");
        var identity = TestIdentity.Create();
        var token = AgentSecretReference.Format(Guid.NewGuid(), TestIdentity.Secret);
        try
        {
            IAgentCredentialStore store;
            using (AgentEnrollmentLease.Acquire(directory))
            {
                store = AgentCredentialStoreFactory.Create(directory);
                await store.SaveAsync(identity, CancellationToken.None);
            }
            Console.SetError(error);
            Environment.SetEnvironmentVariable(variable, explicitToken ? "invalid-environment-token" : token);
            string[] args = ["enroll", "--server", "https://backup.example.test/", "--store-directory", directory, "--non-interactive", "--console"];
            if (explicitToken) { args = [.. args, "--token", token]; }

            var code = await AgentEnrollmentCommand.RunAsync(args, CancellationToken.None);

            Assert.Equal(1, code);
            Assert.Contains("already enrolled", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(token, error.ToString(), StringComparison.Ordinal);
            Assert.Null(Environment.GetEnvironmentVariable(variable));
            Assert.Equal(identity, await store.LoadAsync(CancellationToken.None));
        }
        finally
        {
            Console.SetError(stderr);
            Environment.SetEnvironmentVariable(variable, previousToken);
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

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
