using BackupPlatform.Domain.Storage;
using BackupPlatform.Storage;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.UnitTests.Storage.Connections;

public sealed class StorageProviderCatalogTests
{
    private static StorageProviderDescriptor Descriptor(
        string providerType = "fake", string displayName = "Fake", StorageProviderAvailability availability = StorageProviderAvailability.Available,
        IEnumerable<string>? capabilities = null, IEnumerable<StorageOwnership>? ownerships = null, bool tenantCreatable = true, bool platformCreatable = true,
        int? max = null, IEnumerable<StorageFieldDefinition>? config = null, IEnumerable<StorageSecretFieldDefinition>? secrets = null,
        StorageTestMode testMode = StorageTestMode.BackendRoundTrip) =>
        new(providerType, displayName, availability, StorageTopology.AgentDirect, null, capabilities ?? [], ownerships ?? [StorageOwnership.TenantManaged],
            tenantCreatable, platformCreatable, max, config ?? [], secrets ?? [], testMode);

    private static StorageFieldDefinition Field(string name) => new(name, name, StorageFieldKind.Text, false);

    private static StorageSecretFieldDefinition Secret(string name) => new(name, name, false);

    // ---- built-ins ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("local")]
    [InlineData("central")]
    public void LocalAndCentralAreAvailableWithoutConnectionConfigurationOrSecrets(string providerType)
    {
        var descriptor = StorageProviderCatalog.BuiltIn().Find(providerType)!;

        Assert.Equal(StorageProviderAvailability.Available, descriptor.Availability);
        Assert.True(descriptor.IsAvailable);
        Assert.Empty(descriptor.SecretFields);
        // Site and media are optional policy facts; only Local requires a volume.
        Assert.Equal(providerType == "local" ? ["volume", "site", "mediaClass"] : ["site", "mediaClass"], descriptor.ConfigFields.Select(field => field.Name));
    }

    [Fact]
    public void LocalIsTenantOnlyAgentSideAndCentralIsPlatformOnlyBackendTested()
    {
        var local = BuiltInStorageProviders.Local;
        var central = BuiltInStorageProviders.Central;

        Assert.True(local.TenantCreatable);
        Assert.False(local.PlatformCreatable);
        Assert.Equal([StorageOwnership.TenantManaged], local.SupportedOwnerships);
        Assert.Equal(StorageTestMode.AgentSide, local.TestMode);
        Assert.False(central.TenantCreatable);
        Assert.True(central.PlatformCreatable);
        Assert.Equal([StorageOwnership.PlatformManaged], central.SupportedOwnerships);
        Assert.Equal(StorageTestMode.BackendRoundTrip, central.TestMode);
    }

    [Fact]
    public void GoogleDriveIsAvailableTenantCreatableWithAgentSideTest()
    {
        var googleDrive = BuiltInStorageProviders.GoogleDrive;

        Assert.Equal(StorageProviderAvailability.Available, googleDrive.Availability);
        Assert.True(googleDrive.IsAvailable);
        Assert.True(googleDrive.TenantCreatable);
        Assert.False(googleDrive.PlatformCreatable);
        Assert.Equal([StorageOwnership.TenantManaged], googleDrive.SupportedOwnerships);
        Assert.Equal(StorageTestMode.AgentSide, googleDrive.TestMode);
        Assert.Equal(StorageTopology.AgentDirect, googleDrive.Topology);
        Assert.Contains(googleDrive.ConfigFields, f => f.Name == "accountEmail");
        Assert.Contains(googleDrive.SecretFields, f => f.Name == "refreshToken");
    }

    [Fact]
    public void ConstructorForcesAPlannedProviderToBeUncreatableAndUntestableEvenWhenAskedOtherwise()
    {
        var descriptor = Descriptor(availability: StorageProviderAvailability.Planned, tenantCreatable: true, platformCreatable: true, testMode: StorageTestMode.BackendRoundTrip);

        Assert.False(descriptor.TenantCreatable);
        Assert.False(descriptor.PlatformCreatable);
        Assert.Equal(StorageTestMode.None, descriptor.TestMode);
    }

    // ---- descriptor validation ---------------------------------------------------------------------------------------------

    [Fact]
    public void FieldNamesMustBeUniqueAcrossConfigurationAndSecretFields()
    {
        Assert.Throws<ArgumentException>(() => Descriptor(config: [Field("host")], secrets: [Secret("host")]));
        Assert.Throws<ArgumentException>(() => Descriptor(config: [Field("host"), Field("host")]));
        Assert.Throws<ArgumentException>(() => Descriptor(secrets: [Secret("token"), Secret("token")]));
    }

    [Fact]
    public void FieldNamesMustFollowTheFieldNameGrammar()
    {
        Assert.Throws<ArgumentException>(() => Descriptor(config: [Field("Host")]));
        Assert.Throws<ArgumentException>(() => Descriptor(config: [Field("1host")]));
        Assert.Throws<ArgumentException>(() => Descriptor(config: [Field("ho st")]));
        Assert.Throws<ArgumentException>(() => Descriptor(secrets: [Secret("pass_word")]));
        Assert.Throws<ArgumentException>(() => Descriptor(config: [Field("h" + new string('a', 32))]));
    }

    [Fact]
    public void AtLeastOneOwnershipIsRequired()
    {
        Assert.Throws<ArgumentException>(() => Descriptor(ownerships: []));
    }

    [Fact]
    public void DuplicateOwnershipsCollapseAndTheFirstIsTheDefault()
    {
        var descriptor = Descriptor(ownerships: [StorageOwnership.PlatformManaged, StorageOwnership.TenantManaged, StorageOwnership.PlatformManaged]);

        Assert.Equal([StorageOwnership.PlatformManaged, StorageOwnership.TenantManaged], descriptor.SupportedOwnerships);
        Assert.Equal(StorageOwnership.PlatformManaged, descriptor.DefaultOwnership);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConnectionLimitBelowOneIsRejected(int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Descriptor(max: max));
    }

    [Theory]
    [InlineData("Bad")]
    [InlineData("-bad")]
    [InlineData("")]
    public void ProviderTypeMustBeAValidKey(string providerType)
    {
        Assert.Throws<ArgumentException>(() => Descriptor(providerType: providerType));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DisplayNameMustNotBeBlank(string displayName)
    {
        Assert.Throws<ArgumentException>(() => Descriptor(displayName: displayName));
    }

    // ---- catalog -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void CatalogOrdersAvailabilityFirstAndNamesOrdinallyRegardlessOfRegistrationOrder()
    {
        var catalog = new StorageProviderCatalog([
            Descriptor("zeta", "Zeta"),
            Descriptor("planned", "Alpha planned", StorageProviderAvailability.Planned),
            Descriptor("beta", "Beta"),
            Descriptor("alpha", "Alpha")]);

        Assert.Equal(["alpha", "beta", "zeta", "planned"], catalog.Descriptors.Select(item => item.ProviderType));
    }

    [Fact]
    public void FindReturnsTheRegisteredDescriptorAndNullForUnknownOrWronglyCasedTypes()
    {
        var catalog = StorageProviderCatalog.BuiltIn();

        Assert.Same(BuiltInStorageProviders.Central, catalog.Find("central"));
        Assert.Null(catalog.Find("nope"));
        Assert.Null(catalog.Find("Central"));
        Assert.Null(catalog.Find(""));
    }

    [Fact]
    public void RegisteringTheSameProviderTypeTwiceIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new StorageProviderCatalog([Descriptor("dup", "One"), Descriptor("dup", "Two")]));
    }

    [Fact]
    public void CatalogRequiresDescriptors()
    {
        Assert.Throws<ArgumentNullException>(() => new StorageProviderCatalog(null!));
    }
}
