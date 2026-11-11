namespace BackupPlatform.Contracts.Flags;

public sealed record Feature(string Key, bool EnabledByDefault);

/// <summary>
/// Every feature that can be switched off, shared by the API and the agent. Adding one is one line below; the operator sets it with
/// configuration <c>Features:{key}</c> (environment <c>Features__{key}</c>) and the API reports the result at <c>GET /api/features</c>.
/// </summary>
public static class Features
{
    private static readonly List<Feature> all = [];
    public static IReadOnlyList<Feature> All => all;

    public static readonly Feature Sms = Register("sms", enabledByDefault: false);
    public static readonly Feature Smb = Register("smb", enabledByDefault: false);
    public static readonly Feature SizeAnomaly = Register("sizeAnomaly", enabledByDefault: false);
    /// <summary>Agent-side: a file source configured with a VSS snapshot.</summary>
    public static readonly Feature WindowsVss = Register("windowsVss", enabledByDefault: false);
    /// <summary>Agent-side: a file source configured with an LVM, Btrfs or ZFS snapshot.</summary>
    public static readonly Feature LinuxSnapshots = Register("linuxSnapshots", enabledByDefault: false);
    /// <summary>Server-side: <c>Secrets:Provider</c> other than <c>Database</c>.</summary>
    public static readonly Feature ExternalSecrets = Register("externalSecrets", enabledByDefault: false);
    /// <summary>Policy exception: on by default, so multi-destination repair keeps working; off disables manual and automatic repair.</summary>
    public static readonly Feature ReplicaRepair = Register("replicaRepair", enabledByDefault: true);

    private static Feature Register(string key, bool enabledByDefault)
    {
        var feature = new Feature(key, enabledByDefault);
        all.Add(feature);
        return feature;
    }
}

/// <summary>The one "is this feature enabled" query. <paramref name="configured"/> returns the operator's override for a key, or null.</summary>
public sealed class FeatureFlags(Func<string, bool?> configured)
{
    public bool IsEnabled(Feature feature) => configured(feature.Key) ?? feature.EnabledByDefault;
}
