using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Storage;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions.FileSystem;

namespace BackupPlatform.Storage;

public enum StorageProviderAvailability
{
    /// <summary>The platform can write, read and delete through this provider today.</summary>
    Available = 1,
    /// <summary>Known and described for capability display only. No connection can be created for it and no test of it can succeed.</summary>
    Planned = 2
}

/// <summary>How a connection of this provider type can be checked from the backend.</summary>
public enum StorageTestMode
{
    /// <summary>Nothing can be tested (the provider is not implemented).</summary>
    None = 0,
    /// <summary>The backend writes, verifies and deletes a probe object itself.</summary>
    BackendRoundTrip = 1,
    /// <summary>Only the agent can reach the storage; the backend cannot look, so a test reports NotTestable instead of claiming success.</summary>
    AgentSide = 2
}

public enum StorageFieldKind
{
    Text = 1,
    Number = 2,
    Boolean = 3
}

/// <summary>A non-secret configuration field a provider type accepts. Credentials are never configuration fields (see <see cref="StorageSecretFieldDefinition"/>).</summary>
public sealed record StorageFieldDefinition(string Name, string Label, StorageFieldKind Kind, bool Required, int MaxLength = 128);

public sealed record StorageSecretFieldDefinition(string Name, string Label, bool Required);

/// <summary>
/// Everything the platform knows about a provider type BEFORE any connection exists: what it can do, who may create connections of it, which
/// fields they carry and how they are tested. Adding a provider means adding one descriptor (and a tester); connection management, quota
/// scopes, views and permissions are written against this shape and never against a provider name.
/// </summary>
public sealed record StorageProviderDescriptor
{
    public StorageProviderDescriptor(
        string providerType, string displayName, StorageProviderAvailability availability, StorageTopology? topology,
        BackupStorageExecutionLocation? executionLocation, IEnumerable<string> capabilities, IEnumerable<StorageOwnership> supportedOwnerships,
        bool tenantCreatable, bool platformCreatable, int? maxConnectionsPerTenant, IEnumerable<StorageFieldDefinition> configFields,
        IEnumerable<StorageSecretFieldDefinition> secretFields, StorageTestMode testMode)
    {
        if (!StorageProviderTypes.IsValid(providerType)) throw new ArgumentException("Invalid provider type.", nameof(providerType));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ProviderType = providerType;
        DisplayName = displayName;
        Availability = availability;
        Topology = topology;
        ExecutionLocation = executionLocation;
        Capabilities = [.. capabilities.Order(StringComparer.Ordinal)];
        SupportedOwnerships = [.. supportedOwnerships.Distinct()];
        TenantCreatable = tenantCreatable && availability == StorageProviderAvailability.Available;
        PlatformCreatable = platformCreatable && availability == StorageProviderAvailability.Available;
        MaxConnectionsPerTenant = maxConnectionsPerTenant is { } max && max < 1 ? throw new ArgumentOutOfRangeException(nameof(maxConnectionsPerTenant)) : maxConnectionsPerTenant;
        ConfigFields = [.. configFields];
        SecretFields = [.. secretFields];
        TestMode = availability == StorageProviderAvailability.Available ? testMode : StorageTestMode.None;
        var names = ConfigFields.Select(field => field.Name).Concat(SecretFields.Select(field => field.Name)).ToList();
        if (names.Any(name => !StorageConnection.IsFieldName(name)) || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            throw new ArgumentException("Field names must be valid and unique across configuration and secret fields.", nameof(configFields));
        }
        if (SupportedOwnerships.Count == 0) throw new ArgumentException("At least one ownership is required.", nameof(supportedOwnerships));
    }

    public string ProviderType { get; }
    public string DisplayName { get; }
    public StorageProviderAvailability Availability { get; }
    public StorageTopology? Topology { get; }
    public BackupStorageExecutionLocation? ExecutionLocation { get; }
    public IReadOnlyList<string> Capabilities { get; }
    public IReadOnlyList<StorageOwnership> SupportedOwnerships { get; }
    public bool TenantCreatable { get; }
    public bool PlatformCreatable { get; }
    public int? MaxConnectionsPerTenant { get; }
    public IReadOnlyList<StorageFieldDefinition> ConfigFields { get; }
    public IReadOnlyList<StorageSecretFieldDefinition> SecretFields { get; }
    public StorageTestMode TestMode { get; }

    public bool IsAvailable => Availability == StorageProviderAvailability.Available;

    /// <summary>The ownership a connection gets when the creator does not choose one: the first supported.</summary>
    public StorageOwnership DefaultOwnership => SupportedOwnerships[0];

    public static IReadOnlyList<string> CapabilityNames(StorageCapabilities capabilities) => [.. capabilities.Select(capability => capability.Value)];
}

/// <summary>The providers this build knows. Planned ones are listed so capability status is visible, never so they can be used.</summary>
public static class BuiltInStorageProviders
{
    public static StorageProviderDescriptor Local { get; } = new(
        StorageProviderTypes.Local, "Local (Agent diski)", StorageProviderAvailability.Available, StorageTopology.AgentDirect, BackupStorageExecutionLocation.Agent,
        StorageProviderDescriptor.CapabilityNames(FileSystemStorageProvider.FileSystemCapabilities), [StorageOwnership.TenantManaged],
        tenantCreatable: true, platformCreatable: false, maxConnectionsPerTenant: null,
        [new("volume", "Disk adı (agent'ta tanımlı)", StorageFieldKind.Text, true, 32)], [], StorageTestMode.AgentSide);

    public static StorageProviderDescriptor Central { get; } = new(
        StorageProviderTypes.Central, "Managed Storage (Central)", StorageProviderAvailability.Available, StorageTopology.AgentDirect, BackupStorageExecutionLocation.Backend,
        StorageProviderDescriptor.CapabilityNames(FileSystemStorageProvider.FileSystemCapabilities), [StorageOwnership.PlatformManaged],
        tenantCreatable: false, platformCreatable: true, maxConnectionsPerTenant: 1, [], [], StorageTestMode.BackendRoundTrip);

    public static StorageProviderDescriptor Sftp { get; } = new(
        StorageProviderTypes.Sftp, "SFTP", StorageProviderAvailability.Available, StorageTopology.BackendRelayed, BackupStorageExecutionLocation.Backend,
        ["storage.read", "storage.delete", "storage.atomic-publish", "storage.verify-absence"], [StorageOwnership.TenantManaged],
        tenantCreatable: true, platformCreatable: false, maxConnectionsPerTenant: null,
        [
            new("host", "Host", StorageFieldKind.Text, true, 253),
            new("port", "Port", StorageFieldKind.Number, false, 5),
            new("username", "Username", StorageFieldKind.Text, true, 128),
            new("authenticationType", "Authentication type", StorageFieldKind.Text, true, 16),
            new("remoteBaseDirectory", "Remote base directory", StorageFieldKind.Text, true, 512),
            new("hostKeyFingerprint", "SSH host key fingerprint (SHA256)", StorageFieldKind.Text, true, 64)
        ],
        [new("password", "Password", false), new("privateKey", "SSH private key", false), new("passphrase", "Private key passphrase", false)],
        StorageTestMode.BackendRoundTrip);

    public static StorageProviderDescriptor GoogleDrive { get; } = new(
        StorageProviderTypes.GoogleDrive, "Google Drive", StorageProviderAvailability.Available, StorageTopology.BackendRelayed, BackupStorageExecutionLocation.Backend,
        ["storage.read", "storage.delete", "storage.atomic-publish", "storage.verify-absence"], [StorageOwnership.TenantManaged],
        tenantCreatable: true, platformCreatable: false, maxConnectionsPerTenant: null,
        [
            new("accountEmail", "Google hesabı", StorageFieldKind.Text, false, 256),
            new("accountName", "Hesap adı", StorageFieldKind.Text, false, 256),
            new("folderId", "Klasör ID", StorageFieldKind.Text, false, 128)
        ],
        [new("refreshToken", "Refresh token", false)],
        StorageTestMode.BackendRoundTrip);

    public static IReadOnlyList<StorageProviderDescriptor> All { get; } = [Local, Central, Sftp, GoogleDrive];

    private static StorageProviderDescriptor Planned(string providerType, string displayName) => new(
        providerType, displayName, StorageProviderAvailability.Planned, null, null, [], [StorageOwnership.TenantManaged],
        tenantCreatable: false, platformCreatable: false, maxConnectionsPerTenant: null, [], [], StorageTestMode.None);
}


/// <summary>Descriptor lookup. A new provider is registered by adding its descriptor to the composition; nothing else here changes.</summary>
public interface IStorageProviderCatalog
{
    IReadOnlyList<StorageProviderDescriptor> Descriptors { get; }

    StorageProviderDescriptor? Find(string providerType);
}

public sealed class StorageProviderCatalog : IStorageProviderCatalog
{
    private readonly Dictionary<string, StorageProviderDescriptor> _byType;

    public StorageProviderCatalog(IEnumerable<StorageProviderDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var list = descriptors.ToList();
        _byType = list.ToDictionary(descriptor => descriptor.ProviderType, StringComparer.Ordinal);
        Descriptors = [.. list.OrderBy(descriptor => descriptor.Availability).ThenBy(descriptor => descriptor.DisplayName, StringComparer.Ordinal)];
    }

    public static StorageProviderCatalog BuiltIn() => new(BuiltInStorageProviders.All);

    public IReadOnlyList<StorageProviderDescriptor> Descriptors { get; }

    public StorageProviderDescriptor? Find(string providerType) => _byType.GetValueOrDefault(providerType);
}
