namespace BackupPlatform.ManagedService.Subscriptions;

public enum EffectiveStorageLimitSource
{
    Plan = 1,
    Override = 2
}

/// <summary>
/// The storage limit a tenant is entitled to. This is the only place the "override wins over the plan" rule exists;
/// endpoints, UI read models and the later quota slice all call <see cref="Resolve"/>.
/// </summary>
public readonly record struct EffectiveStorageLimit(bool IsUnlimited, long? Bytes, EffectiveStorageLimitSource Source)
{
    public static EffectiveStorageLimit Resolve(long? planStorageLimitBytes, StorageLimitOverride limitOverride) => limitOverride.Kind switch
    {
        StorageLimitOverrideKind.Limited => new(false, limitOverride.Bytes, EffectiveStorageLimitSource.Override),
        StorageLimitOverrideKind.Unlimited => new(true, null, EffectiveStorageLimitSource.Override),
        _ => planStorageLimitBytes is { } bytes
            ? new(false, bytes, EffectiveStorageLimitSource.Plan)
            : new(true, null, EffectiveStorageLimitSource.Plan)
    };
}
