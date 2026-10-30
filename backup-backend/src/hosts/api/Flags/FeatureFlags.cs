namespace BackupPlatform.Api.Flags;

public sealed record Feature(string Key, bool EnabledByDefault);

/// <summary>
/// Every feature that can be switched off. Adding one is one line below; the operator sets it with configuration <c>Features:{key}</c>
/// (environment <c>Features__{key}</c>) and the UI reads the result from <c>GET /api/features</c>.
/// </summary>
public static class Features
{
    private static readonly List<Feature> all = [];
    public static IReadOnlyList<Feature> All => all;

    public static readonly Feature Sms = Register("sms", enabledByDefault: false);

    private static Feature Register(string key, bool enabledByDefault)
    {
        var feature = new Feature(key, enabledByDefault);
        all.Add(feature);
        return feature;
    }
}

/// <summary>The one "is this feature enabled" query: endpoint registration, handlers on shared routes and the UI all ask it.</summary>
public sealed class FeatureFlags(IConfiguration configuration)
{
    public bool IsEnabled(Feature feature) => configuration.GetValue<bool?>($"Features:{feature.Key}") ?? feature.EnabledByDefault;
}

public static class FeatureEndpoints
{
    /// <summary>Evaluated once at startup: a disabled feature's routes are never registered, so they answer 404.</summary>
    public static bool IsFeatureEnabled(this IEndpointRouteBuilder endpoints, Feature feature) =>
        endpoints.ServiceProvider.GetRequiredService<FeatureFlags>().IsEnabled(feature);

    public static IEndpointRouteBuilder MapFeatureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/features", (FeatureFlags flags) => Features.All.ToDictionary(feature => feature.Key, flags.IsEnabled)).AllowAnonymous();
        return endpoints;
    }
}
