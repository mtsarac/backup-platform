using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Central;

namespace BackupPlatform.IntegrationTests.Storage;

public sealed class CentralStorageProviderTests : FileSystemProviderContractTests
{
    private const string Scope = "tenant-a";

    protected override string ObjectsDirectory => Path.Combine(Root, "tenants", Scope);

    protected override IStorageProvider CreateProvider(string root) => new CentralStorageProvider(new CentralStorageOptions(root, Scope));

    private static readonly StoragePutOptions FailIfExists = new(StorageOverwritePolicy.Fail);

    [Fact]
    public void CentralProviderIsTheAgentDirectCentralProvider()
    {
        var provider = CreateProvider(Root);

        Assert.Equal(StorageProviderId.Central, provider.Id);
        Assert.Equal(StorageTopology.AgentDirect, provider.Topology);
    }

    [Fact]
    public async Task ObjectsLiveUnderTenantsAndTheTenantScope()
    {
        var provider = CreateProvider(Root);

        var stored = await provider.PutAsync(new StorageObjectKey("mssql/sales.bak"), new PatternStream(10), FailIfExists, null, CancellationToken.None);

        Assert.Equal([Path.Combine(Root, "tenants", "tenant-a", "mssql", "sales.bak")], AllFiles());
        Assert.Equal("central:mssql/sales.bak", stored.Location.ToString());
    }

    [Fact]
    public async Task ProvidersForDifferentTenantsOnTheSameRootCannotSeeEachOther()
    {
        var tenantA = new CentralStorageProvider(new CentralStorageOptions(Root, "tenant-a"));
        var tenantB = new CentralStorageProvider(new CentralStorageOptions(Root, "tenant-b"));
        var key = new StorageObjectKey("mssql/shared-name.bak");
        var storedA = await tenantA.PutAsync(key, new PatternStream(1000), FailIfExists, null, CancellationToken.None);

        Assert.False(await tenantB.ExistsAsync(key, CancellationToken.None));
        Assert.Null(await tenantB.GetMetadataAsync(key, CancellationToken.None));
        Assert.False(await tenantB.DeleteAsync(key, CancellationToken.None));
        var verifyB = await tenantB.VerifyAsync(key, new StorageVerificationExpectation(1000, storedA.Checksum!), CancellationToken.None);
        Assert.Equal(StorageVerificationStatus.Missing, verifyB.Status);

        var storedB = await tenantB.PutAsync(key, new PatternStream(2000), FailIfExists, null, CancellationToken.None);
        Assert.Equal(2000, storedB.SizeBytes);
        Assert.Equal(1000, (await tenantA.GetMetadataAsync(key, CancellationToken.None))!.SizeBytes);
        Assert.Equal(2000, (await tenantB.GetMetadataAsync(key, CancellationToken.None))!.SizeBytes);

        Assert.True(await tenantA.DeleteAsync(key, CancellationToken.None));
        Assert.True(await tenantB.ExistsAsync(key, CancellationToken.None));
        Assert.Equal([Path.Combine(Root, "tenants", "tenant-b", "mssql", "shared-name.bak")], AllFiles());
    }

    [Fact]
    public async Task AKeyCannotNameAnotherTenantsDirectory()
    {
        var tenantA = new CentralStorageProvider(new CentralStorageOptions(Root, "tenant-a"));
        var tenantB = new CentralStorageProvider(new CentralStorageOptions(Root, "tenant-b"));
        await tenantB.PutAsync(new StorageObjectKey("secret.bak"), new PatternStream(10), FailIfExists, null, CancellationToken.None);

        // Even a key that spells out the sibling tenant's layout stays inside tenant-a's own area.
        var lookalike = new StorageObjectKey("tenants/tenant-b/secret.bak");

        Assert.False(await tenantA.ExistsAsync(lookalike, CancellationToken.None));
        Assert.False(await tenantA.DeleteAsync(lookalike, CancellationToken.None));
        Assert.True(await tenantB.ExistsAsync(new StorageObjectKey("secret.bak"), CancellationToken.None));
    }

    [Fact]
    public async Task EnumerateReturnsKeysWithoutTheTenantsPathAndNeverListsAnotherTenant()
    {
        var tenantA = CreateProvider(Root);
        var tenantB = new CentralStorageProvider(new CentralStorageOptions(Root, "tenant-b"));
        await tenantA.PutAsync(new StorageObjectKey("mssql/a.bak"), new PatternStream(10), FailIfExists, null, CancellationToken.None);
        await tenantB.PutAsync(new StorageObjectKey("mssql/b.bak"), new PatternStream(10), FailIfExists, null, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(Root, "unrelated"));
        await File.WriteAllBytesAsync(Path.Combine(Root, "unrelated", "x.bak"), [1]);

        var keys = new List<string>();
        await foreach (var entry in tenantA.EnumerateAsync(null, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        Assert.Equal(["mssql/a.bak"], keys);
    }

    [SymlinkFact]
    public async Task ATenantDirectoryThatIsASymlinkIsRefused()
    {
        var outside = NewOutsideDirectory();
        Directory.CreateDirectory(Path.Combine(Root, "tenants"));
        Directory.CreateSymbolicLink(ObjectsDirectory, outside);
        var provider = CreateProvider(Root);

        await ExpectFailure(StorageFailureKind.AccessDenied, () => provider.PutAsync(new StorageObjectKey("x.bak"), new PatternStream(10), FailIfExists, null, CancellationToken.None), outside);

        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [SymlinkFact]
    public async Task ATenantsDirectoryThatIsASymlinkIsRefused()
    {
        var outside = NewOutsideDirectory();
        Directory.CreateSymbolicLink(Path.Combine(Root, "tenants"), outside);
        var provider = CreateProvider(Root);

        await ExpectFailure(StorageFailureKind.AccessDenied, () => provider.ExistsAsync(new StorageObjectKey("x.bak"), CancellationToken.None), outside);

        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }
}
