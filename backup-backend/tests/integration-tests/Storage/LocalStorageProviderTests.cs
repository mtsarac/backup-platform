using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Local;

namespace BackupPlatform.IntegrationTests.Storage;

public sealed class LocalStorageProviderTests : FileSystemProviderContractTests
{
    protected override string ObjectsDirectory => Root;

    protected override IStorageProvider CreateProvider(string root) => new LocalStorageProvider(new LocalStorageOptions(root));

    [Fact]
    public void LocalProviderIsTheAgentDirectLocalProvider()
    {
        var provider = CreateProvider(Root);

        Assert.Equal(StorageProviderId.Local, provider.Id);
        Assert.Equal(StorageTopology.AgentDirect, provider.Topology);
    }

    [Fact]
    public async Task KeysAreStoredDirectlyBelowTheRoot()
    {
        var provider = CreateProvider(Root);

        await provider.PutAsync(new StorageObjectKey("a/b.bak"), new PatternStream(10), new StoragePutOptions(StorageOverwritePolicy.Fail), null, CancellationToken.None);

        Assert.Equal([Path.Combine(Root, "a", "b.bak")], AllFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedVerifiedPrefixResumesExactlyAndTamperingNeverPublishes(bool corruptPrefix)
    {
        var provider = CreateProvider(Root);
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17);
        var checksum = ContentChecksum.Sha256(System.Security.Cryptography.SHA256.HashData(bytes));
        var key = new StorageObjectKey("retained/backup.bak");
        StorageTransferCheckpoint? saved = null;
        var options = new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, bytes.Length)
        {
            RecordCheckpointAsync = (checkpoint, _) =>
            {
                saved = checkpoint;
                if (checkpoint.VerifiedByteOffset > 0) throw new StorageException(StorageFailureKind.NetworkUnavailable, "The mocked connection dropped.");
                return Task.CompletedTask;
            }
        };
        await Assert.ThrowsAsync<StorageException>(() => provider.PutAsync(key, new MemoryStream(bytes), options, null, CancellationToken.None));
        Assert.NotNull(saved);
        Assert.Equal(1024 * 1024, saved.VerifiedByteOffset);
        var partial = Path.Combine(Root, "retained", saved.ProviderState!);
        Assert.Equal(saved.VerifiedByteOffset, new FileInfo(partial).Length);
        Assert.False(await provider.ExistsAsync(key, CancellationToken.None));
        if (corruptPrefix)
        {
            await using var tamper = new FileStream(partial, FileMode.Open, FileAccess.Write);
            tamper.WriteByte((byte)(bytes[0] ^ 255));
        }
        options = options with { Checkpoint = saved, RecordCheckpointAsync = (checkpoint, _) => { saved = checkpoint; return Task.CompletedTask; } };
        if (corruptPrefix)
        {
            var error = await Assert.ThrowsAsync<StorageException>(() => provider.PutAsync(key, new MemoryStream(bytes), options, null, CancellationToken.None));
            Assert.Equal(StorageFailureKind.IntegrityMismatch, error.Kind);
            Assert.False(await provider.ExistsAsync(key, CancellationToken.None));
        }
        else
        {
            await provider.PutAsync(key, new MemoryStream(bytes), options, null, CancellationToken.None);
            await using var read = await provider.OpenReadAsync(key, CancellationToken.None);
            using var actual = new MemoryStream();
            await read.CopyToAsync(actual);
            Assert.Equal(bytes, actual.ToArray());
            Assert.Equal(bytes.Length, saved!.VerifiedByteOffset);
            Assert.False(File.Exists(partial));
        }
    }
}
