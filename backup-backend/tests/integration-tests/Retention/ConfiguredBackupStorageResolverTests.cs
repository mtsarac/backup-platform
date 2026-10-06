using BackupPlatform.Api.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// The resolver is the backend's whole reach into storage: only what the operator configured resolves, and nothing a persisted replica says
/// (provider id, target, key) can widen that. (Lives with the API tests because the resolver belongs to the API host.)
/// </summary>
public sealed class ConfiguredBackupStorageResolverTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "backup-resolver"));
    private static readonly TenantId TenantA = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly TenantId TenantB = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly TenantId Unconfigured = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private static ConfiguredBackupStorageResolver Resolver(bool local = true, bool central = true) => new(new BackupStorageOptions
    {
        Local = local ? new BackupLocalStorageSettings { RootDirectory = Path.Combine(Root, "local") } : null,
        Central = central
            ? new BackupCentralStorageSettings
            {
                RootDirectory = Path.Combine(Root, "central"),
                TenantScopes = new Dictionary<string, string> { [TenantA.Value.ToString()] = "tenant-a", [TenantB.Value.ToString("N")] = "tenant-b" }
            }
            : null
    });

    [Fact]
    public void ConfiguredLocalStorageResolvesForEveryTenantUnderItsOwnNameOrDefault()
    {
        var resolver = Resolver();

        var byName = resolver.Resolve(TenantA, "local", "local");
        var byDefault = resolver.Resolve(Unconfigured, "local", "default");

        Assert.NotNull(byName);
        Assert.Same(byName, byDefault);
    }

    [Fact]
    public void CentralStorageResolvesOnlyForTenantsWithAConfiguredScopeAndNeverSharesAProviderBetweenTenants()
    {
        var resolver = Resolver();

        var a = resolver.Resolve(TenantA, "central", "central");
        var b = resolver.Resolve(TenantB, "central", "default");

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
        Assert.Null(resolver.Resolve(Unconfigured, "central", "central"));
    }

    [Theory]
    [InlineData("s3", "s3")]
    [InlineData("local", "../../etc")]
    [InlineData("local", "/var/backups")]
    [InlineData("local", "central")]       // a target never redirects a provider to another
    [InlineData("local", "Local")]         // exact, case-sensitive comparison
    [InlineData("local", "")]
    [InlineData("LOCAL", "local")]
    [InlineData("", "")]
    [InlineData("central", "local")]
    public void AnythingElseThePersistedRowSaysResolvesToNothing(string provider, string target)
    {
        var resolver = Resolver();

        Assert.Null(resolver.Resolve(TenantA, provider, target));
    }

    [Fact]
    public void AStorageTheOperatorDidNotConfigureNeverResolves()
    {
        var noLocal = Resolver(local: false);
        var noCentral = Resolver(central: false);
        var nothing = new ConfiguredBackupStorageResolver(new BackupStorageOptions());

        Assert.Null(noLocal.Resolve(TenantA, "local", "default"));
        Assert.NotNull(noLocal.Resolve(TenantA, "central", "central"));
        Assert.Null(noCentral.Resolve(TenantA, "central", "central"));
        Assert.NotNull(noCentral.Resolve(TenantA, "local", "local"));
        Assert.Null(nothing.Resolve(TenantA, "local", "local"));
        Assert.Empty(nothing.ManagedStorages);
    }

    [Fact]
    public void ManagedStoragesListExactlyTheConfiguredStoragesWithTheirTenantScope()
    {
        var resolver = Resolver();

        var storages = resolver.ManagedStorages;

        Assert.Equal(3, storages.Count);
        var local = Assert.Single(storages, storage => storage.ProviderId.Value == "local");
        Assert.Null(local.TenantScope);
        Assert.Same(local.Provider, resolver.Resolve(TenantA, "local", "local"));
        var central = storages.Where(storage => storage.ProviderId.Value == "central").ToList();
        Assert.Equal(new[] { TenantA, TenantB }.Select(tenant => tenant.Value).Order(), central.Select(storage => storage.TenantScope!.Value).Order());
        foreach (var storage in central) Assert.Same(storage.Provider, resolver.Resolve(storage.TenantScope!, "central", "central"));
        Assert.Equal(storages.Count, storages.Select(storage => storage.Name).Distinct().Count());
        Assert.DoesNotContain(storages, storage => storage.Name.Contains(Root, StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidOptionsFailBeforeAnyProviderIsBuilt()
    {
        var invalid = new BackupStorageOptions { Local = new BackupLocalStorageSettings { RootDirectory = "relative" } };

        Assert.Throws<InvalidOperationException>(() => new ConfiguredBackupStorageResolver(invalid));
    }
}
