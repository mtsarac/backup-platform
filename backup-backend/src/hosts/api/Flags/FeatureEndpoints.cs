using BackupPlatform.Contracts.Flags;

namespace BackupPlatform.Api.Flags;

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
