using BackupPlatform.ManagedService.Plans;

namespace BackupPlatform.ManagedService.Subscriptions;

public enum StorageLimitOverrideKind
{
    /// <summary>No override: the plan limit applies. The default value of the type.</summary>
    None = 0,
    Limited = 1,
    Unlimited = 2
}

/// <summary>
/// A tenant-specific storage limit. "Unlimited" is its own kind instead of a null byte count, so an absent override
/// and an explicit unlimited override can never be confused.
/// </summary>
public readonly record struct StorageLimitOverride
{
    private StorageLimitOverride(StorageLimitOverrideKind kind, long? bytes)
    {
        Kind = kind;
        Bytes = bytes;
    }

    public StorageLimitOverrideKind Kind { get; }
    /// <summary>Decimal bytes; set only for <see cref="StorageLimitOverrideKind.Limited"/>.</summary>
    public long? Bytes { get; }

    public static StorageLimitOverride None => default;

    public static StorageLimitOverride Unlimited => new(StorageLimitOverrideKind.Unlimited, null);

    public static StorageLimitOverride Limited(long bytes)
    {
        if (!StorageLimits.IsValid(bytes))
        {
            throw new InvalidManagedServiceRequestException("storageLimitOverride", "out_of_range");
        }
        return new StorageLimitOverride(StorageLimitOverrideKind.Limited, bytes);
    }

    /// <summary>Rebuilds a stored pair; used by persistence, which has already been constrained by the database.</summary>
    public static StorageLimitOverride From(StorageLimitOverrideKind kind, long? bytes) => kind switch
    {
        StorageLimitOverrideKind.None when bytes is null => None,
        StorageLimitOverrideKind.Unlimited when bytes is null => Unlimited,
        StorageLimitOverrideKind.Limited when bytes is { } value => Limited(value),
        _ => throw new ArgumentException("The override kind and byte count are inconsistent.", nameof(kind))
    };
}
