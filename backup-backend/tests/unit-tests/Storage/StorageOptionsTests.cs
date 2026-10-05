using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions.FileSystem;
using BackupPlatform.StorageProviders.Central;
using BackupPlatform.StorageProviders.Local;

namespace BackupPlatform.UnitTests.Storage;

public sealed class StorageOptionsTests
{
    private static string AbsoluteRoot => Path.Combine(Path.GetTempPath(), "bp_test_options_unused");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rel-zz/sub")]
    [InlineData("./rel-zz")]
    [InlineData("../rel-zz")]
    [InlineData("rel-zz")]
    public void RelativeOrEmptyRootsAreRejected(string root)
    {
        var local = Assert.Throws<ArgumentException>(() => new LocalStorageOptions(root));
        var central = Assert.Throws<ArgumentException>(() => new CentralStorageOptions(root, "tenant"));
        var shared = Assert.Throws<ArgumentException>(() => FileSystemStorageRoot.Normalize(root));

        Assert.All([local, central, shared], exception => Assert.DoesNotContain("rel-zz", exception.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void NullRootIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalStorageOptions(null!));
        Assert.Throws<ArgumentNullException>(() => new CentralStorageOptions(null!, "tenant"));
    }

    [Fact]
    public void RootWithNulCharacterIsRejected() =>
        Assert.Throws<ArgumentException>(() => new LocalStorageOptions(AbsoluteRoot + "\0x"));

    [Fact]
    public void AbsoluteRootIsNormalised()
    {
        var options = new LocalStorageOptions(AbsoluteRoot + Path.DirectorySeparatorChar + "a" + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar);

        Assert.Equal(AbsoluteRoot, options.RootDirectory);
    }

    [Fact]
    public void FilesystemRootKeepsItsSeparator()
    {
        var root = Path.GetPathRoot(AbsoluteRoot)!;

        Assert.Equal(root, FileSystemStorageRoot.Normalize(root));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("/")]
    [InlineData("a\\b")]
    [InlineData("tenant one")]
    [InlineData("C:")]
    [InlineData("CON")]
    [InlineData("tenant.")]
    [InlineData("tenänt")]
    [InlineData("Tenant-A")]
    public void InvalidTenantScopesAreRejected(string scope) =>
        Assert.Throws<ArgumentException>(() => new CentralStorageOptions(AbsoluteRoot, scope));

    [Fact]
    public void NullTenantScopeIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new CentralStorageOptions(AbsoluteRoot, null!));

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("0b1f6c2e-8d0a-4f7e-9a53-2f4c1d9d7a10")]
    [InlineData("acme.corp_1")]
    public void ValidTenantScopesAreAccepted(string scope)
    {
        var options = new CentralStorageOptions(AbsoluteRoot, scope);

        Assert.Equal(scope, options.TenantScope);
        Assert.Equal(AbsoluteRoot, options.RootDirectory);
    }

    [Fact]
    public void ProvidersRejectMissingOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalStorageProvider(null!));
        Assert.Throws<ArgumentNullException>(() => new CentralStorageProvider(null!));
    }

    [Fact]
    public void ProvidersDoNotTouchTheFileSystemWhenConstructed()
    {
        var root = Path.Combine(Path.GetTempPath(), "bp_test_" + Guid.NewGuid().ToString("N"));

        _ = new LocalStorageProvider(new LocalStorageOptions(root));
        _ = new CentralStorageProvider(new CentralStorageOptions(root, "tenant"));

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void LocalProviderIdentityAndCapabilities()
    {
        var provider = new LocalStorageProvider(new LocalStorageOptions(AbsoluteRoot));

        AssertFileSystemIdentity(provider, StorageProviderId.Local);
    }

    [Fact]
    public void CentralProviderIdentityAndCapabilities()
    {
        var provider = new CentralStorageProvider(new CentralStorageOptions(AbsoluteRoot, "tenant"));

        AssertFileSystemIdentity(provider, StorageProviderId.Central);
    }

    private static void AssertFileSystemIdentity(IStorageProvider provider, StorageProviderId expectedId)
    {
        Assert.Equal(expectedId, provider.Id);
        Assert.Equal(StorageTopology.AgentDirect, provider.Topology);
        Assert.Equal(2, provider.Capabilities.Count);
        Assert.True(provider.Capabilities.Supports(StorageCapabilityIds.Delete));
        Assert.True(provider.Capabilities.Supports(StorageCapabilityIds.AtomicPublish));
        Assert.False(provider.Capabilities.Supports(StorageCapabilityIds.ServerSideChecksum));
        Assert.False(provider.Capabilities.Supports(StorageCapabilityIds.Resume));
        Assert.False(provider.Capabilities.Supports(StorageCapabilityIds.Multipart));
        Assert.False(provider.Capabilities.Supports(StorageCapabilityIds.ImmutableStorage));
        Assert.False(provider.Capabilities.Supports(StorageCapabilityIds.CapacityQuery));
    }
}
