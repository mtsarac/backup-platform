namespace BackupPlatform.ManagedService.Quota;

/// <summary>
/// HOW usage is counted and enforced; the limit itself comes from the plan or the subscription override. A policy is data: the seeded
/// default (<see cref="DefaultKey"/>) holds the first commercial behaviour, and changing what is counted or when runs are refused is an update
/// of a row, never a code change. Plans point at a policy by its <see cref="Key"/>; a plan without one uses the default policy.
/// </summary>
/// <remarks>
/// <see cref="Version"/> grows on every change. A stored usage projection records the policy version it was computed under, so a projection
/// computed under an older version is recognised as stale and recomputed before anyone relies on it.
/// </remarks>
public sealed class QuotaPolicy
{
    public const string DefaultKey = "default";
    /// <summary>Fixed so the migration's seed row and the code agree without a lookup.</summary>
    public static readonly QuotaPolicyId DefaultId = new(Guid.Parse("14e8fce9-11ed-47a9-aa04-b6ead7a8cd3d"));
    public const int DefaultWarningThresholdPercent = 80;
    public const int DefaultCriticalThresholdPercent = 90;

    private QuotaPolicy()
    {
        Id = null!;
        Key = null!;
        Name = null!;
        NormalizedName = null!;
        IncludedStorageScopes = null!;
    }

    public QuotaPolicy(
        QuotaPolicyId id, string key, string name, QuotaAccountingMode accountingMode, QuotaScopeRule includedStorageScopes,
        QuotaEnforcementMode enforcementMode, int warningThresholdPercent, int criticalThresholdPercent, bool enabled, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(id);
        Id = id;
        Key = ManagedServiceValidation.QuotaPolicyReference(key, "key") ?? throw new InvalidManagedServiceRequestException("key", "required");
        var (display, normalized) = ManagedServiceValidation.PolicyName(name);
        Name = display;
        NormalizedName = normalized;
        AccountingMode = Defined(accountingMode, "accountingMode");
        IncludedStorageScopes = includedStorageScopes ?? throw new InvalidManagedServiceRequestException(QuotaScopeRule.Field, "required");
        EnforcementMode = Defined(enforcementMode, "enforcementMode");
        (WarningThresholdPercent, CriticalThresholdPercent) = Thresholds(warningThresholdPercent, criticalThresholdPercent);
        Enabled = enabled;
        Version = 1;
        CreatedAt = now.ToUniversalTime();
        UpdatedAt = CreatedAt;
    }

    public QuotaPolicyId Id { get; private set; }
    /// <summary>The reference a plan stores in <c>QuotaPolicyReference</c>; lowercase, unique, never changed after creation.</summary>
    public string Key { get; private set; }
    public string Name { get; private set; }
    public string NormalizedName { get; private set; }
    public int Version { get; private set; }
    public QuotaAccountingMode AccountingMode { get; private set; }
    public QuotaScopeRule IncludedStorageScopes { get; private set; }
    public QuotaEnforcementMode EnforcementMode { get; private set; }
    public int WarningThresholdPercent { get; private set; }
    public int CriticalThresholdPercent { get; private set; }
    /// <summary>A disabled policy is no longer picked by plans; tenants that referenced it use the default policy.</summary>
    public bool Enabled { get; private set; }
    /// <summary>Exactly one policy is the default; it is the one the migration seeds, it is always enabled and applies to every plan without a reference.</summary>
    public bool IsDefault { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Returns false, leaving <see cref="Version"/> and <see cref="UpdatedAt"/> untouched, when every value is already current.</summary>
    public bool Update(
        string name, QuotaAccountingMode accountingMode, QuotaScopeRule includedStorageScopes, QuotaEnforcementMode enforcementMode,
        int warningThresholdPercent, int criticalThresholdPercent, bool enabled, DateTimeOffset now)
    {
        var (display, normalized) = ManagedServiceValidation.PolicyName(name);
        Defined(accountingMode, "accountingMode");
        Defined(enforcementMode, "enforcementMode");
        ArgumentNullException.ThrowIfNull(includedStorageScopes);
        var (warning, critical) = Thresholds(warningThresholdPercent, criticalThresholdPercent);
        if (IsDefault && !enabled)
        {
            throw new InvalidManagedServiceRequestException("enabled", "default_policy_required");
        }
        if (string.Equals(Name, display, StringComparison.Ordinal) && AccountingMode == accountingMode && IncludedStorageScopes.Equals(includedStorageScopes)
            && EnforcementMode == enforcementMode && WarningThresholdPercent == warning && CriticalThresholdPercent == critical && Enabled == enabled)
        {
            return false;
        }
        Name = display;
        NormalizedName = normalized;
        AccountingMode = accountingMode;
        IncludedStorageScopes = includedStorageScopes;
        EnforcementMode = enforcementMode;
        WarningThresholdPercent = warning;
        CriticalThresholdPercent = critical;
        Enabled = enabled;
        Version = checked(Version + 1);
        UpdatedAt = now.ToUniversalTime();
        return true;
    }

    public static (int Warning, int Critical) Thresholds(int warning, int critical)
    {
        if (warning is < 1 or > 99)
        {
            throw new InvalidManagedServiceRequestException("warningThresholdPercent", "out_of_range");
        }
        if (critical is < 2 or > 100 || critical <= warning)
        {
            throw new InvalidManagedServiceRequestException("criticalThresholdPercent", "out_of_range");
        }
        return (warning, critical);
    }

    private static T Defined<T>(T value, string field) where T : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new InvalidManagedServiceRequestException(field, "invalid_value");
}
