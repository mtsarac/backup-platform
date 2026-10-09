using BackupPlatform.Domain.Backups;
using System.Collections.ObjectModel;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Storage;

/// <summary>Who operates a storage destination. It is data on the connection, never derived from the provider type.</summary>
public enum StorageOwnership
{
    /// <summary>Operated by the platform (for example the Central storage, offered to tenants as Managed Storage). Only a PlatformAdmin changes it.</summary>
    PlatformManaged = 1,
    /// <summary>Operated by the tenant (the agent's own disk, and every customer-supplied service later). The tenant manages it.</summary>
    TenantManaged = 2
}

/// <summary>What the last connection test concluded. <see cref="NotTestable"/> is never a success: the platform could not look, so it claims nothing.</summary>
public enum StorageTestStatus
{
    Succeeded = 1,
    Failed = 2,
    NotTestable = 3
}

/// <summary>Provider type keys. A provider type is a lowercase key (the same grammar as a storage provider id); new providers add a key, not a code path.</summary>
public static class StorageProviderTypes
{
    public const string Local = "local";
    public const string Central = "central";
    public const string Sftp = "sftp";
    public const string GoogleDrive = "google-drive";

    public static bool IsValid(string? value) =>
        value is { Length: >= 1 and <= 32 } && value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') && value[0] != '-' && value[^1] != '-';
}

/// <summary>
/// Quota scope labels: the billing selector a QuotaPolicy may match. Ownership only supplies the DEFAULT label; the label itself is data and can
/// be changed (by a platform administrator) without touching ownership or provider.
/// </summary>
public static class StorageQuotaScopes
{
    public const string Managed = "managed";
    public const string Tenant = "tenant";
    public const int MaximumLength = 64;

    public static string DefaultFor(StorageOwnership ownership) => ownership == StorageOwnership.PlatformManaged ? Managed : Tenant;

    public static bool IsValid(string? value) =>
        value is { Length: >= 1 and <= MaximumLength } && value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') && value[0] != '-' && value[^1] != '-';
}

/// <summary>The destination every tenant has without asking: the connections existing replicas and jobs resolve to when none is named.</summary>
public sealed record DefaultStorageConnection(string ProviderType, string Name, StorageOwnership Ownership, string QuotaScope);

public static class StorageConnectionDefaults
{
    public static readonly DefaultStorageConnection Local = new(StorageProviderTypes.Local, "Local", StorageOwnership.TenantManaged, StorageQuotaScopes.Tenant);
    public static readonly DefaultStorageConnection Central = new(StorageProviderTypes.Central, "Managed Storage", StorageOwnership.PlatformManaged, StorageQuotaScopes.Managed);

    public static IReadOnlyList<DefaultStorageConnection> All { get; } = [Local, Central];

    public static DefaultStorageConnection? For(string providerType) => All.FirstOrDefault(item => item.ProviderType == providerType);
}

/// <summary>
/// One storage destination of one tenant: provider type, ownership, quota scope, enabled flag, safe (non-secret) configuration, and a
/// REFERENCE to credentials held by the secret provider. The platform never keeps a credential value on this row, so nothing here can leak one.
/// </summary>
/// <remarks>
/// Disabling a connection stops NEW runs from targeting it; replicas already stored on it are untouched (still Available, still counted, still
/// downloadable, restorable and subject to retention). That rule lives in the persistence gate that creates runs, not on this entity.
/// </remarks>
public sealed class StorageConnection : ITenantOwned
{
    public const int MaximumNameLength = 100;
    public const int MaximumConfigurationEntries = 16;
    public const int MaximumConfigurationValueLength = 512;

    private static readonly IReadOnlyDictionary<string, string> NoConfiguration = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    private StorageConnection()
    {
        TenantId = null!;
        Name = null!;
        NormalizedName = null!;
        ProviderType = null!;
        QuotaScope = null!;
        Configuration = NoConfiguration;
        SecretFieldNames = [];
    }

    public StorageConnection(
        Guid id, TenantId tenantId, string name, string providerType, StorageOwnership ownership, string? quotaScope,
        IReadOnlyDictionary<string, string>? configuration, bool isDefault, DateTimeOffset now)
    {
        BackupGuard.Id(id);
        ArgumentNullException.ThrowIfNull(tenantId);
        if (!StorageProviderTypes.IsValid(providerType)) throw new ArgumentException("Invalid storage provider type.", nameof(providerType));
        if (!Enum.IsDefined(ownership)) throw new ArgumentException("Unknown storage ownership.", nameof(ownership));
        var label = quotaScope ?? StorageQuotaScopes.DefaultFor(ownership);
        if (!StorageQuotaScopes.IsValid(label)) throw new ArgumentException("Invalid quota scope.", nameof(quotaScope));
        var (display, normalized) = Names(name);
        Id = id;
        TenantId = tenantId;
        Name = display;
        NormalizedName = normalized;
        ProviderType = providerType;
        Ownership = ownership;
        QuotaScope = label;
        Configuration = Snapshot(configuration);
        SecretFieldNames = [];
        Enabled = true;
        IsDefault = isDefault;
        CreatedAt = BackupGuard.Timestamp(now);
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public string Name { get; private set; }
    /// <summary>Case-insensitive uniqueness key within the tenant.</summary>
    public string NormalizedName { get; private set; }
    /// <summary>Immutable after creation: a connection is one kind of destination for its whole life, which is what lets replicas keep pointing at it.</summary>
    public string ProviderType { get; private set; }
    /// <summary>Immutable after creation.</summary>
    public StorageOwnership Ownership { get; private set; }
    public string QuotaScope { get; private set; }
    /// <summary>The connection owner's preference, independent of the platform's administrative lock.</summary>
    public bool Enabled { get; private set; }
    /// <summary>Only platform operations change this safety lock; unlocking never changes the owner's preference.</summary>
    public bool AdministrativelyDisabled { get; private set; }
    public bool EffectiveEnabled => Enabled && !AdministrativelyDisabled;
    /// <summary>The tenant's system default for its provider type (materialized on demand and by the upgrade). At most one per tenant and provider.</summary>
    public bool IsDefault { get; private set; }
    /// <summary>Non-secret, provider-specific values (a host name, a folder). Validated against the provider descriptor by the application layer; never credentials.</summary>
    public IReadOnlyDictionary<string, string> Configuration { get; private set; }
    /// <summary>The name of the secret in the secret provider, or null. Not secret material.</summary>
    public string? SecretReference { get; private set; }
    /// <summary>Which secret fields were stored (names only), so a view can say what is set without ever reading it.</summary>
    public IReadOnlyList<string> SecretFieldNames { get; private set; }
    public DateTimeOffset? SecretUpdatedAt { get; private set; }
    public StorageTestStatus? LastTestStatus { get; private set; }
    public string? LastTestCode { get; private set; }
    public DateTimeOffset? LastTestedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    /// <summary>When either availability control last changed; null until the first change.</summary>
    public DateTimeOffset? StateChangedAt { get; private set; }

    public bool HasSecret => SecretReference is not null;

    public static (string Display, string Normalized) Names(string? name)
    {
        var display = name?.Trim();
        if (string.IsNullOrEmpty(display) || display.Length > MaximumNameLength || display.Any(char.IsControl))
        {
            throw new ArgumentException("Invalid storage connection name.", nameof(name));
        }
        return (display, display.ToLowerInvariant());
    }

    /// <summary>False, leaving everything untouched, when nothing changes.</summary>
    public bool Rename(string name, DateTimeOffset now)
    {
        var (display, normalized) = Names(name);
        if (string.Equals(Name, display, StringComparison.Ordinal)) return false;
        Name = display;
        NormalizedName = normalized;
        Touch(now);
        return true;
    }

    public bool Reconfigure(IReadOnlyDictionary<string, string>? configuration, DateTimeOffset now)
    {
        var next = Snapshot(configuration);
        if (next.Count == Configuration.Count && next.All(pair => Configuration.TryGetValue(pair.Key, out var current) && current == pair.Value)) return false;
        Configuration = next;
        InvalidateTest();
        Touch(now);
        return true;
    }

    public bool ChangeQuotaScope(string quotaScope, DateTimeOffset now)
    {
        if (!StorageQuotaScopes.IsValid(quotaScope)) throw new ArgumentException("Invalid quota scope.", nameof(quotaScope));
        if (string.Equals(QuotaScope, quotaScope, StringComparison.Ordinal)) return false;
        QuotaScope = quotaScope;
        Touch(now);
        return true;
    }

    public bool SetEnabled(bool enabled, DateTimeOffset now)
    {
        if (Enabled == enabled) return false;
        Enabled = enabled;
        StateChangedAt = BackupGuard.Timestamp(now);
        Touch(now);
        return true;
    }

    public bool SetAdministrativelyDisabled(bool disabled, DateTimeOffset now)
    {
        if (AdministrativelyDisabled == disabled) return false;
        AdministrativelyDisabled = disabled;
        StateChangedAt = BackupGuard.Timestamp(now);
        Touch(now);
        return true;
    }

    /// <summary>Points the connection at a (new) secret. Returns the reference it replaced so the caller can delete that secret once this change is committed.</summary>
    public string? AttachSecret(string reference, IEnumerable<string> fieldNames, DateTimeOffset now)
    {
        BackupGuard.Reference(reference);
        var names = fieldNames?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() ?? throw new ArgumentNullException(nameof(fieldNames));
        if (names.Count == 0 || names.Count > MaximumConfigurationEntries || names.Any(name => !IsFieldName(name))) throw new ArgumentException("Invalid secret field names.", nameof(fieldNames));
        var previous = SecretReference;
        SecretReference = reference;
        SecretFieldNames = names;
        SecretUpdatedAt = BackupGuard.Timestamp(now);
        InvalidateTest();
        Touch(now);
        return previous;
    }

    /// <summary>Forgets the secret. Returns the reference that was held (or null) for the caller to delete after commit.</summary>
    public string? DetachSecret(DateTimeOffset now)
    {
        if (SecretReference is null) return null;
        var previous = SecretReference;
        SecretReference = null;
        SecretFieldNames = [];
        SecretUpdatedAt = BackupGuard.Timestamp(now);
        InvalidateTest();
        Touch(now);
        return previous;
    }

    /// <summary>Records the outcome of a connection test. <paramref name="code"/> is a stable reason code, never provider output.</summary>
    public void RecordTest(StorageTestStatus status, string? code, DateTimeOffset now)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentException("Unknown test status.", nameof(status));
        if (code is not null) BackupGuard.Reference(code);
        LastTestStatus = status;
        LastTestCode = code;
        LastTestedAt = BackupGuard.Timestamp(now);
    }

    private void InvalidateTest()
    {
        LastTestStatus = null;
        LastTestCode = null;
        LastTestedAt = null;
    }

    /// <summary>Configuration keys are short identifiers (also the secret field names).</summary>
    public static bool IsFieldName(string? name) =>
        name is { Length: >= 1 and <= 32 } && char.IsAsciiLetterLower(name[0]) && name.All(character => char.IsAsciiLetterOrDigit(character));

    private void Touch(DateTimeOffset now) => UpdatedAt = BackupGuard.Timestamp(now);

    private static IReadOnlyDictionary<string, string> Snapshot(IReadOnlyDictionary<string, string>? configuration)
    {
        if (configuration is null || configuration.Count == 0) return NoConfiguration;
        if (configuration.Count > MaximumConfigurationEntries) throw new ArgumentException("Too many configuration entries.", nameof(configuration));
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in configuration)
        {
            if (!IsFieldName(key) || value is null || value.Length > MaximumConfigurationValueLength || value.Any(char.IsControl))
            {
                throw new ArgumentException("Invalid configuration entry.", nameof(configuration));
            }
            copy[key] = value;
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }
}
