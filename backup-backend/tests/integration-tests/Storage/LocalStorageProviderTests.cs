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
}
