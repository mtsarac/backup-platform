using BackupPlatform.Api.Security;
using BackupPlatform.Core.Application.Agents;
using BackupPlatform.Contracts.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BackupPlatform.Api.Endpoints;

public static class AgentUpdateEndpoints
{
    public static IEndpointRouteBuilder MapAgentUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Platform Admin can publish updates
        var platform = endpoints.MapGroup("/api/platform/agent-updates")
            .RequireAuthorization(AuthorizationPolicies.PlatformAdmin);

        platform.MapPost("", PublishUpdateAsync);

        // Agents can check for manifest and download package
        var agent = endpoints.MapGroup("/api/agent/updates")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol())
            .RequireAuthorization(AuthorizationPolicies.Agent);

        agent.MapGet("/manifest", GetLatestManifestAsync);
        agent.MapGet("/packages/{platform}/{version}", DownloadPackageAsync);

        return endpoints;
    }

    public sealed record PublishUpdateRequest
    {
        public required AgentUpdateManifest Manifest { get; init; }
        public required string PackageBase64 { get; init; }
    }

    private static async Task<IResult> PublishUpdateAsync(
        PublishUpdateRequest request,
        IAgentUpdateManifestStore store,
        CancellationToken cancellationToken)
    {
        var packageBytes = Convert.FromBase64String(request.PackageBase64);
        var computedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(packageBytes));

        if (!string.Equals(computedHash, request.Manifest.Payload.PackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "package_hash_mismatch", message = "Uploaded package SHA256 does not match manifest payload." });
        }

        if (packageBytes.LongLength != request.Manifest.Payload.PackageSize)
        {
            return Results.BadRequest(new { error = "package_size_mismatch", message = "Uploaded package size does not match manifest payload." });
        }

        await store.SaveManifestAsync(request.Manifest, packageBytes, cancellationToken);
        return Results.Created($"/api/agent/updates/packages/{request.Manifest.Payload.Platform}/{request.Manifest.Payload.Version}", request.Manifest);
    }

    private static async Task<IResult> GetLatestManifestAsync(
        string platform,
        IAgentUpdateManifestStore store,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return Results.BadRequest(new { error = "platform_required" });
        }

        var manifest = await store.GetLatestManifestAsync(platform, cancellationToken);
        if (manifest is null)
        {
            return Results.NotFound(new { error = "no_update_available" });
        }

        return Results.Ok(manifest);
    }

    private static async Task<IResult> DownloadPackageAsync(
        string platform,
        string version,
        IAgentUpdateManifestStore store,
        CancellationToken cancellationToken)
    {
        var result = await store.GetPackageAsync(platform, version, cancellationToken);
        if (result is null)
        {
            return Results.NotFound(new { error = "package_not_found" });
        }

        return Results.File(result.Value.PackageBytes, "application/octet-stream", $"backup-agent-{platform}-{version}.tar.gz");
    }
}
