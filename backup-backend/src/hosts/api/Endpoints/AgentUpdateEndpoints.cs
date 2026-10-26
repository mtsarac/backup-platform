using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Agents;
using Microsoft.AspNetCore.Builder;
using BackupPlatform.Encryption;
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

        platform.MapPost("", PublishUpdateAsync).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(360L * 1024 * 1024));

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
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (request.Manifest?.Payload is not { PackageSize: > 0 and <= 256L * 1024 * 1024 } payload
            || string.IsNullOrWhiteSpace(request.Manifest.KeyId)
            || !Version.TryParse(payload.Version, out var version) || version.ToString() != payload.Version
            || !Version.TryParse(payload.MinimumSupportedVersion, out var minimum) || minimum > version
            || payload.Platform is not ("linux-x64" or "linux-arm64" or "win-x64")
            || request.PackageBase64 is null || request.PackageBase64.Length > 358_000_000)
            return Results.BadRequest(new { error = "invalid_manifest" });
        var trustedPem = configuration.GetSection("AgentUpdates:TrustedKeys")[request.Manifest.KeyId];
        if (string.IsNullOrWhiteSpace(trustedPem)) return Results.BadRequest(new { error = "untrusted_signing_key" });
        using var publicKey = System.Security.Cryptography.ECDsa.Create();
        try { publicKey.ImportFromPem(trustedPem); }
        catch (ArgumentException) { return Results.Problem("The configured update verification key is invalid.", statusCode: 503); }
        if (!AgentUpdateManifestVerifier.Verify(request.Manifest, publicKey.ExportSubjectPublicKeyInfo()))
            return Results.BadRequest(new { error = "invalid_manifest_signature" });
        byte[] packageBytes;
        try { packageBytes = Convert.FromBase64String(request.PackageBase64); }
        catch (FormatException) { return Results.BadRequest(new { error = "invalid_package_encoding" }); }
        var computedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(packageBytes));

        if (!string.Equals(computedHash, request.Manifest.Payload.PackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "package_hash_mismatch", message = "Uploaded package SHA256 does not match manifest payload." });
        }

        if (packageBytes.LongLength != request.Manifest.Payload.PackageSize)
        {
            return Results.BadRequest(new { error = "package_size_mismatch", message = "Uploaded package size does not match manifest payload." });
        }

        try { await store.SaveManifestAsync(request.Manifest, packageBytes, cancellationToken); }
        catch (AgentUpdateConflictException) { return Results.Conflict(new { error = "release_already_published" }); }
        return Results.Created($"/api/agent/updates/packages/{request.Manifest.Payload.Platform}/{request.Manifest.Payload.Version}", request.Manifest);
    }

    private static async Task<IResult> GetLatestManifestAsync(
        string platform,
        IAgentUpdateManifestStore store,
        CancellationToken cancellationToken)
    {
        if (platform is not ("linux-x64" or "linux-arm64" or "win-x64"))
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
        if (platform is not ("linux-x64" or "linux-arm64" or "win-x64")
            || !Version.TryParse(version, out var parsed) || parsed.ToString() != version)
            return Results.BadRequest(new { error = "invalid_release" });
        var result = await store.GetPackageAsync(platform, version, cancellationToken);
        if (result is null)
        {
            return Results.NotFound(new { error = "package_not_found" });
        }

        return Results.File(result.Value.PackageBytes, "application/octet-stream", $"backup-agent-{platform}-{version}" + (platform == "win-x64" ? ".zip" : ".tar.gz"));
    }
}
