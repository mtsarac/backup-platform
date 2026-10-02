using System.Runtime.Versioning;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Platform.Linux.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.UnitTests;

public sealed class AgentLocalIdentityTests
{
    [Fact]
    public void SerializerRoundTripsCurrentAndPendingCredentials()
    {
        var identity = Identity().WithPending(Credential(2));
        var restored = AgentLocalIdentitySerializer.Deserialize(AgentLocalIdentitySerializer.Serialize(identity));
        Assert.Equal(identity.AgentId, restored.AgentId);
        Assert.Equal(identity.ServerUri, restored.ServerUri);
        Assert.Equal(identity.InstallationId, restored.InstallationId);
        Assert.Equal(identity.Current, restored.Current);
        Assert.Equal(identity.Pending, restored.Pending);
        Assert.Equal(identity.Current.Secret, restored.Current.Secret);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("not json {{{")]
    [InlineData("{\"Version\":99}")]
    public void MalformedDocumentsFailWithoutEchoingTheirContent(string text)
    {
        var exception = Assert.Throws<AgentCredentialStoreException>(() =>
            AgentLocalIdentitySerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("not json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsRedactSecretsAndHeadersUseTheAgentScheme()
    {
        var credential = Credential(1);
        Assert.DoesNotContain(credential.Secret, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(credential.Secret, Identity().ToString(), StringComparison.Ordinal);
        Assert.Equal(AgentAuthorizationScheme.FormatHeaderValue(credential.CredentialId, credential.Secret), credential.ToAuthorizationHeaderValue());
    }

    [Fact]
    public void IdentityRequiresHttpsAndASequentialPendingGeneration()
    {
        var credential = Credential(1);
        Assert.Throws<ArgumentException>(() => new AgentLocalIdentity(Guid.NewGuid(), new Uri("http://localhost"), Guid.NewGuid(), credential));
        Assert.Throws<ArgumentException>(() => Identity().WithPending(Credential(3)));
        var promoted = Identity().WithPending(Credential(2)).WithPendingPromoted();
        Assert.Equal(2, promoted.Current.Generation);
        Assert.Null(promoted.Pending);
        Assert.Throws<InvalidOperationException>(() => Identity().WithPendingPromoted());
    }

    internal static AgentLocalIdentity Identity() =>
        new(Guid.NewGuid(), new Uri("https://backup.example.test"), Guid.NewGuid(), Credential(1));

    internal static AgentStoredCredential Credential(int generation) =>
        new(Guid.NewGuid(), AgentSecretFor(generation), generation);

    private static string AgentSecretFor(int generation) =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

[UnsupportedOSPlatform("windows")]
public sealed class LinuxAgentCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"agent-store-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string StoreDirectory => Path.Combine(_root, "identity");

    [LinuxFact]
    public async Task RoundTripsAndCreatesOwnerOnlyDirectoryAndFile()
    {
        var store = new LinuxAgentCredentialStore(StoreDirectory);
        Assert.Null(await store.LoadAsync(CancellationToken.None));
        var identity = AgentLocalIdentityTests.Identity().WithPending(AgentLocalIdentityTests.Credential(2));
        await store.SaveAsync(identity, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(identity.Current, loaded!.Current);
        Assert.Equal(identity.Pending, loaded.Pending);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(StoreDirectory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(StoreDirectory, "agent-identity.json")));
    }

    [LinuxFact]
    public async Task ReplacementIsAtomicLeavesNoTemporaryFilesAndSurvivesConcurrentWriters()
    {
        var store = new LinuxAgentCredentialStore(StoreDirectory);
        var identities = Enumerable.Range(0, 24).Select(_ => AgentLocalIdentityTests.Identity()).ToArray();
        await Task.WhenAll(identities.Select(identity => Task.Run(() => store.SaveAsync(identity, CancellationToken.None))));
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Contains(identities, identity => identity.Current == loaded!.Current);
        Assert.Equal(["agent-identity.json"], Directory.EnumerateFileSystemEntries(StoreDirectory).Select(entry => Path.GetFileName(entry)!).ToArray());
    }

    [LinuxFact]
    public async Task ASecondInstanceSeesTheLatestCompleteIdentity()
    {
        var first = new LinuxAgentCredentialStore(StoreDirectory);
        await first.SaveAsync(AgentLocalIdentityTests.Identity(), CancellationToken.None);
        var replacement = AgentLocalIdentityTests.Identity();
        await first.SaveAsync(replacement, CancellationToken.None);
        var restarted = await new LinuxAgentCredentialStore(StoreDirectory).LoadAsync(CancellationToken.None);
        Assert.Equal(replacement.Current, restarted!.Current);
    }

    [LinuxFact]
    public async Task LooseFileOrDirectoryPermissionsAreRejectedRatherThanTrusted()
    {
        var store = new LinuxAgentCredentialStore(StoreDirectory);
        await store.SaveAsync(AgentLocalIdentityTests.Identity(), CancellationToken.None);
        var file = Path.Combine(StoreDirectory, "agent-identity.json");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.LoadAsync(CancellationToken.None));
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(StoreDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.SaveAsync(AgentLocalIdentityTests.Identity(), CancellationToken.None));
    }

    [LinuxFact]
    public async Task SymbolicLinkedFileOrDirectoryIsRefusedAndASaveReplacesTheLinkNotItsTarget()
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "attacker-target");
        await File.WriteAllTextAsync(target, "attacker data");
        var store = new LinuxAgentCredentialStore(StoreDirectory);
        await store.SaveAsync(AgentLocalIdentityTests.Identity(), CancellationToken.None);
        var file = Path.Combine(StoreDirectory, "agent-identity.json");
        File.Delete(file);
        File.CreateSymbolicLink(file, target);
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.LoadAsync(CancellationToken.None));
        await store.SaveAsync(AgentLocalIdentityTests.Identity(), CancellationToken.None);
        Assert.Equal("attacker data", await File.ReadAllTextAsync(target));
        Assert.Null(new FileInfo(file).LinkTarget);

        var linkedDirectory = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(linkedDirectory, StoreDirectory);
        var linked = new LinuxAgentCredentialStore(linkedDirectory);
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => linked.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => linked.SaveAsync(AgentLocalIdentityTests.Identity(), CancellationToken.None));
    }

    [LinuxFact]
    public async Task CorruptFileFailsClosedAndDeleteIsIdempotent()
    {
        var store = new LinuxAgentCredentialStore(StoreDirectory);
        await store.SaveAsync(AgentLocalIdentityTests.Identity(), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(StoreDirectory, "agent-identity.json"), "{ truncated");
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.LoadAsync(CancellationToken.None));
        await store.DeleteAsync(CancellationToken.None);
        await store.DeleteAsync(CancellationToken.None);
        Assert.Null(await store.LoadAsync(CancellationToken.None));
    }

    [LinuxFact]
    public void RelativeDirectoriesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new LinuxAgentCredentialStore("relative/path"));
    }
}

/// <summary>Runs only on Linux, where owner-only mode bits and symlinks have their POSIX meaning.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Requires Linux.";
        }
    }
}
