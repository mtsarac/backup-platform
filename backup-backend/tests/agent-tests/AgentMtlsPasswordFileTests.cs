using BackupPlatform.Agent.Host;
using BackupPlatform.Agent.Transport;

namespace BackupPlatform.AgentTests;

[CollectionDefinition("Agent mTLS environment", DisableParallelization = true)]
public sealed class AgentMtlsEnvironmentCollection;

[Collection("Agent mTLS environment")]
public sealed class AgentMtlsPasswordFileTests : IDisposable
{
    private const string FileVariable = "AGENT_MTLS_CERTIFICATE_PASSWORD_FILE";
    private const string PasswordVariable = "AGENT_MTLS_CERTIFICATE_PASSWORD";
    private readonly string _directory = Directory.CreateTempSubdirectory("mtls-password").FullName;

    public AgentMtlsPasswordFileTests() => Reset();

    public void Dispose()
    {
        Reset();
        Directory.Delete(_directory, recursive: true);
    }

    private static void Reset()
    {
        Environment.SetEnvironmentVariable(FileVariable, null);
        Environment.SetEnvironmentVariable(PasswordVariable, null);
    }

    [Fact]
    public void ThePasswordFileWinsOverTheEnvironmentAndIsTrimmed()
    {
        var path = Path.Combine(_directory, "pfx.pass");
        File.WriteAllText(path, "from-the-file-123\n");
        Environment.SetEnvironmentVariable(PasswordVariable, "from-the-environment");
        Environment.SetEnvironmentVariable(FileVariable, path);
        Assert.Equal("from-the-file-123", AgentMtlsTransport.ResolvePassword());
    }

    [Fact]
    public void WithoutAFileTheEnvironmentIsUsedAndNothingIsNull()
    {
        Assert.Null(AgentMtlsTransport.ResolvePassword());
        Environment.SetEnvironmentVariable(PasswordVariable, "from-the-environment");
        Assert.Equal("from-the-environment", AgentMtlsTransport.ResolvePassword());
    }

    [Fact]
    public void ARelativePasswordFilePathIsRefused()
    {
        Environment.SetEnvironmentVariable(FileVariable, "pfx.pass");
        Assert.Throws<InvalidOperationException>(AgentMtlsTransport.ResolvePassword);
    }

    [Fact]
    public void EnrollmentCreatesAnOwnerOnlyRandomPasswordFileOnceAndReusesIt()
    {
        var path = Path.Combine(_directory, "pfx.pass");
        Environment.SetEnvironmentVariable(FileVariable, path);
        var first = AgentMtlsPasswordFile.ResolveOrCreate();
        Assert.True(first.Length >= 32);
        Assert.Equal(first, AgentMtlsPasswordFile.ResolveOrCreate());
        Assert.Equal(first, AgentMtlsTransport.ResolvePassword());
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void EnrollmentWithNeitherSourceFailsInsteadOfUsingAnEmptyPassword()
    {
        Assert.Throws<InvalidOperationException>(AgentMtlsPasswordFile.ResolveOrCreate);
    }
}
