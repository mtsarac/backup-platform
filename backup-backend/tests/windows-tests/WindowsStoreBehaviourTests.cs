using System.Security.Cryptography;
using System.Text;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Platform.Windows.Identity;

namespace BackupPlatform.Agent.Platform.Windows.Tests;

/// <summary>DPAPI round trip, the process boundary, rotation, corruption and atomic replacement.</summary>
public sealed class WindowsStoreBehaviourTests : IDisposable
{
    private readonly TestDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private string StoreDirectory => _scratch.Combine("store");

    private WindowsAgentCredentialStore NewStore() => new(StoreDirectory);

    [WindowsFact]
    public async Task AnIdentityRoundTripsIncludingAPendingCredential()
    {
        var identity = TestIdentity.Create(generation: 3, withPending: true);

        await NewStore().SaveAsync(identity, CancellationToken.None);
        var loaded = await NewStore().LoadAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(identity.AgentId, loaded.AgentId);
        Assert.Equal(identity.InstallationId, loaded.InstallationId);
        Assert.Equal(identity.ServerUri, loaded.ServerUri);
        Assert.Equal(identity.Current, loaded.Current);
        Assert.Equal(identity.Current.Secret, loaded.Current.Secret);
        Assert.Equal(identity.Pending, loaded.Pending);
        Assert.Equal(identity.Pending!.Secret, loaded.Pending!.Secret);
    }

    [WindowsFact]
    public async Task NothingStoredYieldsNullWhetherTheDirectoryIsMissingOrEmpty()
    {
        Assert.Null(await NewStore().LoadAsync(CancellationToken.None));

        Directory.CreateDirectory(StoreDirectory);
        Assert.Null(await NewStore().LoadAsync(CancellationToken.None));
    }

    [WindowsFact]
    public async Task TheBlobOnDiskDoesNotContainTheSecretInAnyCommonEncoding()
    {
        var identity = TestIdentity.Create();
        await NewStore().SaveAsync(identity, CancellationToken.None);

        var blob = await File.ReadAllBytesAsync(Path.Combine(StoreDirectory, "agent-identity.bin"));

        foreach (var encoding in new Encoding[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.Latin1 })
        {
            Assert.DoesNotContain(identity.Current.Secret, encoding.GetString(blob), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(identity.AgentId.ToString(), Encoding.UTF8.GetString(blob), StringComparison.OrdinalIgnoreCase);
    }

    [WindowsFact]
    public async Task TheBlobIsBoundToTheStoresEntropyAndNotDecryptableWithoutIt()
    {
        var identity = TestIdentity.Create();
        await NewStore().SaveAsync(identity, CancellationToken.None);
        var blob = await File.ReadAllBytesAsync(Path.Combine(StoreDirectory, "agent-identity.bin"));

        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(blob, Encoding.UTF8.GetBytes("another application"), DataProtectionScope.CurrentUser));
        Assert.NotEmpty(ProtectedData.Unprotect(blob, WindowsAgentCredentialStore.Entropy, DataProtectionScope.CurrentUser));
    }

    [WindowsFact]
    public async Task AnotherProcessOfTheSameAccountReadsWhatWasWrittenAndTheOtherWayRound()
    {
        var agentId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var secret = TestIdentity.NewSecret();

        // Written by a process that has ended: nothing but the file and the account's DPAPI keys connect it to the reader.
        var saved = Probe.Run("save", StoreDirectory, agentId.ToString(), installationId.ToString(), credentialId.ToString(), secret, "1");
        Assert.True(saved.ExitCode == 0, saved.Output);

        var viaTest = await NewStore().LoadAsync(CancellationToken.None);
        Assert.NotNull(viaTest);
        Assert.Equal(agentId, viaTest.AgentId);
        Assert.Equal(credentialId, viaTest.Current.CredentialId);
        Assert.Equal(secret, viaTest.Current.Secret);

        var loadedByProbe = Probe.Run("load", StoreDirectory);
        Assert.Equal($"OK {agentId} {credentialId} 1 {TestIdentity.Sha256(secret)} none", loadedByProbe.Output);

        // And the reverse: written here, read by a fresh process.
        var other = TestIdentity.Create(generation: 2);
        await NewStore().SaveAsync(other, CancellationToken.None);
        Assert.Equal(
            $"OK {other.AgentId} {other.Current.CredentialId} 2 {TestIdentity.Sha256(other.Current.Secret)} none",
            Probe.Run("load", StoreDirectory).Output);
    }

    [WindowsFact]
    public async Task RotationPersistsCurrentAndPendingAcrossProcessesAndPromotionLeavesNoPending()
    {
        var agentId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        var currentSecret = TestIdentity.NewSecret();
        var pendingId = Guid.NewGuid();
        var pendingSecret = TestIdentity.NewSecret();

        var saved = Probe.Run("save", StoreDirectory, agentId.ToString(), installationId.ToString(), currentId.ToString(), currentSecret, "1", pendingId.ToString(), pendingSecret);
        Assert.True(saved.ExitCode == 0, saved.Output);
        Assert.Equal($"OK {agentId} {currentId} 1 {TestIdentity.Sha256(currentSecret)} 2", Probe.Run("load", StoreDirectory).Output);

        var store = NewStore();
        var withPending = await store.LoadAsync(CancellationToken.None);
        Assert.NotNull(withPending?.Pending);
        Assert.Equal(pendingId, withPending.Pending.CredentialId);

        await store.SaveAsync(withPending.WithPendingPromoted(), CancellationToken.None);

        Assert.Equal($"OK {agentId} {pendingId} 2 {TestIdentity.Sha256(pendingSecret)} none", Probe.Run("load", StoreDirectory).Output);
        Assert.Equal(["agent-identity.bin"], TestDirectory.Names(StoreDirectory));
    }

    [WindowsFact]
    public async Task DiscardingAPendingCredentialPersists()
    {
        var store = NewStore();
        await store.SaveAsync(TestIdentity.Create(withPending: true), CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);
        await store.SaveAsync(loaded!.WithoutPending(), CancellationToken.None);

        Assert.Null((await NewStore().LoadAsync(CancellationToken.None))!.Pending);
    }

    [WindowsFact]
    public async Task ACorruptTruncatedOrEmptyBlobFailsClosedWithoutLeakingAnything()
    {
        var identity = TestIdentity.Create();
        await NewStore().SaveAsync(identity, CancellationToken.None);
        var file = Path.Combine(StoreDirectory, "agent-identity.bin");
        var blob = await File.ReadAllBytesAsync(file);

        var damaged = new List<byte[]>
        {
            Array.Empty<byte>(),
            blob[..(blob.Length / 2)],
            blob.Select(b => (byte)(b ^ 0xFF)).ToArray(),
            Encoding.UTF8.GetBytes(identity.Current.Secret)
        };
        var flipped = (byte[])blob.Clone();
        flipped[^10] ^= 0x01;
        damaged.Add(flipped);

        foreach (var content in damaged)
        {
            await File.WriteAllBytesAsync(file, content);
            var exception = await Assert.ThrowsAsync<AgentCredentialStoreException>(() => NewStore().LoadAsync(CancellationToken.None));
            Assert.DoesNotContain(identity.Current.Secret, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(identity.AgentId.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(StoreDirectory, exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        // A good identity written afterwards replaces the damage.
        await NewStore().SaveAsync(identity, CancellationToken.None);
        Assert.Equal(identity.Current, (await NewStore().LoadAsync(CancellationToken.None))!.Current);
    }

    [WindowsFact]
    public async Task DeleteIsIdempotentAndAfterItNothingIsStored()
    {
        var store = NewStore();
        await store.DeleteAsync(CancellationToken.None);

        await store.SaveAsync(TestIdentity.Create(), CancellationToken.None);
        await store.DeleteAsync(CancellationToken.None);
        await store.DeleteAsync(CancellationToken.None);

        Assert.Null(await store.LoadAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(StoreDirectory));
    }

    [WindowsFact]
    public async Task ConcurrentWritersFromTwoStoreInstancesLeaveOneCompleteIdentityAndNoTemporaryFiles()
    {
        var agentId = Guid.NewGuid();
        var written = Enumerable.Range(1, 24).Select(generation => TestIdentity.Create(generation, agentId: agentId)).ToArray();
        var stores = new[] { NewStore(), NewStore() };

        await Task.WhenAll(written.Select((identity, index) => Task.Run(() => stores[index % 2].SaveAsync(identity, CancellationToken.None))));

        Assert.Equal(["agent-identity.bin"], TestDirectory.Names(StoreDirectory));
        var loaded = await NewStore().LoadAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Contains(written, identity => identity.Current == loaded.Current && identity.InstallationId == loaded.InstallationId);
    }

    [WindowsFact]
    public async Task AReaderInAnotherProcessNeverSeesATornOrWrongIdentityWhileWritesReplaceTheFile()
    {
        var agentId = Guid.NewGuid();
        var known = Enumerable.Range(1, 40).Select(generation => TestIdentity.Create(generation, agentId: agentId)).ToArray();
        var expectedSecrets = known.ToDictionary(identity => TestIdentity.Sha256(identity.Current.Secret), identity => identity.Current.Generation);
        await NewStore().SaveAsync(known[0], CancellationToken.None);

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            var store = NewStore();
            for (var round = 0; round < 6 && !stop.IsCancellationRequested; round++)
            {
                foreach (var identity in known)
                {
                    await store.SaveAsync(identity, CancellationToken.None);
                }
            }
        });

        var observations = new List<string>();
        var exitCodes = new List<int>();
        while (!writer.IsCompleted && observations.Count < 60)
        {
            var result = Probe.Run("load", StoreDirectory);
            exitCodes.Add(result.ExitCode);
            observations.Add(result.Output);
        }

        await stop.CancelAsync();
        await writer;

        Assert.NotEmpty(observations);
        foreach (var output in observations)
        {
            var parts = output.Split(' ');
            Assert.True(parts is ["OK", var agent, _, _, var hash, "none"] && agent == agentId.ToString() && expectedSecrets.ContainsKey(hash), $"Unexpected observation: {output}");
        }

        Assert.All(exitCodes, code => Assert.Equal(0, code));
        Assert.Equal(["agent-identity.bin"], TestDirectory.Names(StoreDirectory));
    }

    [WindowsFact]
    public async Task AnInterruptedWriteIsInvisibleToReadersAndTheNextSaveStillWorks()
    {
        var identity = TestIdentity.Create();
        await NewStore().SaveAsync(identity, CancellationToken.None);
        // What a crash between "temp file written" and "swapped in" leaves behind.
        var leftover = Path.Combine(StoreDirectory, $".agent-identity.bin.{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(leftover, [1, 2, 3]);

        Assert.Equal(identity.Current, (await NewStore().LoadAsync(CancellationToken.None))!.Current);

        var next = TestIdentity.Create(generation: 2, agentId: identity.AgentId);
        await NewStore().SaveAsync(next, CancellationToken.None);
        Assert.Equal(next.Current, (await NewStore().LoadAsync(CancellationToken.None))!.Current);
    }

    [WindowsFact]
    public void ARelativeDirectoryIsRejectedByTheConstructor()
    {
        Assert.Throws<ArgumentException>(() => new WindowsAgentCredentialStore(@"relative\store"));
        Assert.Throws<ArgumentException>(() => new WindowsAgentCredentialStore(@"C:store"));
        Assert.Throws<ArgumentException>(() => new WindowsAgentCredentialStore(" "));
    }
}
