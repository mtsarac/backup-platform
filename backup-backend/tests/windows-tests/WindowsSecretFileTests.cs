using BackupPlatform.Agent.Platform.Windows;

namespace BackupPlatform.Agent.Platform.Windows.Tests;

public sealed class WindowsSecretFileTests : IDisposable
{
    private readonly TestDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [WindowsFact]
    public void TheSecretIsStoredAsADpapiBlobAndRoundTrips()
    {
        var path = _scratch.Combine("pfx.pass");
        WindowsSecretFile.Write(path, "correct horse battery staple");
        Assert.DoesNotContain("correct horse", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.Equal("correct horse battery staple", WindowsSecretFile.Read(path));
    }

    [WindowsFact]
    public void RewritingReplacesTheSecret()
    {
        var path = _scratch.Combine("pfx.pass");
        WindowsSecretFile.Write(path, "first-secret-value");
        WindowsSecretFile.Write(path, "second-secret-value");
        Assert.Equal("second-secret-value", WindowsSecretFile.Read(path));
    }
}
