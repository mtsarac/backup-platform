namespace BackupPlatform.ManagedService.Plans;

/// <summary>
/// A purchasable (or system-managed) capacity package. Plans are data, not an enumeration: adding 50 GB, 100 GB or a custom
/// enterprise plan is a new row and never a code change. Plans are global (not tenant-owned) and are never physically
/// deleted by the application; a plan used by any subscription is protected by a foreign key as well.
/// </summary>
public sealed class Plan
{
    /// <summary>System key of the plan every pre-existing tenant is moved onto when the commercial layer is deployed.</summary>
    public const string LegacyUnlimitedSystemKey = "legacy_unlimited";

    /// <summary>
    /// Schema version of the typed policy configuration a plan will carry. Version 1 defines no members; the version is stored
    /// so that later slices can add typed members without guessing what an old row meant.
    /// </summary>
    public const int CurrentPolicyConfigurationVersion = 1;

    private Plan()
    {
        Id = null!;
        Name = null!;
        NormalizedName = null!;
    }

    public Plan(PlanId id, string name, long? storageLimitBytes, string? quotaPolicyReference, bool enabled, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(id);
        var (display, normalized) = ManagedServiceValidation.PlanName(name);
        Id = id;
        Name = display;
        NormalizedName = normalized;
        StorageLimitBytes = ManagedServiceValidation.PlanStorageLimit(storageLimitBytes);
        QuotaPolicyReference = ManagedServiceValidation.QuotaPolicyReference(quotaPolicyReference);
        PolicyConfigurationVersion = CurrentPolicyConfigurationVersion;
        Enabled = enabled;
        CreatedAt = now.ToUniversalTime();
        UpdatedAt = CreatedAt;
    }

    public PlanId Id { get; private set; }
    public string Name { get; private set; }
    /// <summary>Case-insensitive, whitespace-collapsed business key.</summary>
    public string NormalizedName { get; private set; }
    public bool Enabled { get; private set; }
    /// <summary>Null is an unlimited plan. A tenant-specific override lives on the subscription, not here.</summary>
    public long? StorageLimitBytes { get; private set; }
    /// <summary>Opaque reference to a quota policy a later slice will define; M1 stores it and attaches no behavior.</summary>
    public string? QuotaPolicyReference { get; private set; }
    public int PolicyConfigurationVersion { get; private set; }
    /// <summary>System plans (the legacy plan) are created by migration, hidden from the default sales picker and read-only.</summary>
    public bool IsSystem { get; private set; }
    public string? SystemKey { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsUnlimited => StorageLimitBytes is null;

    /// <summary>Returns false, leaving <see cref="UpdatedAt"/> untouched, when every value is already current.</summary>
    public bool Update(string name, long? storageLimitBytes, string? quotaPolicyReference, DateTimeOffset now)
    {
        EnsureMutable();
        var (display, normalized) = ManagedServiceValidation.PlanName(name);
        var limit = ManagedServiceValidation.PlanStorageLimit(storageLimitBytes);
        var reference = ManagedServiceValidation.QuotaPolicyReference(quotaPolicyReference);
        if (string.Equals(Name, display, StringComparison.Ordinal) && StorageLimitBytes == limit
            && string.Equals(QuotaPolicyReference, reference, StringComparison.Ordinal))
        {
            return false;
        }
        Name = display;
        NormalizedName = normalized;
        StorageLimitBytes = limit;
        QuotaPolicyReference = reference;
        UpdatedAt = now.ToUniversalTime();
        return true;
    }

    /// <summary>Disabling only stops new assignments; it never touches existing subscriptions.</summary>
    public bool SetEnabled(bool enabled, DateTimeOffset now)
    {
        EnsureMutable();
        if (Enabled == enabled)
        {
            return false;
        }
        Enabled = enabled;
        UpdatedAt = now.ToUniversalTime();
        return true;
    }

    private void EnsureMutable()
    {
        if (IsSystem)
        {
            throw new InvalidOperationException("System plans are read-only.");
        }
    }
}
