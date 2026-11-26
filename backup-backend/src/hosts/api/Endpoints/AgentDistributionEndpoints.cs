using BackupPlatform.Agents.Enrollment;

namespace BackupPlatform.Api.Endpoints;

public static class AgentDistributionEndpoints
{
    // The Windows setup wizard is one pipeline-built file for every deployment. The server address is stamped after the executable
    // as this trailer when it is downloaded, and the wizard reads it back from its own file (deploy/windows/installer/BackupAgentSetup.iss).
    // shortcut: an Authenticode signature would be invalidated by appended bytes; revisit when signing is added.
    public const string SetupTrailerPrefix = "\nBackupPlatformServer=";

    public static IEndpointRouteBuilder MapAgentDistributionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var dist = endpoints.MapGroup("/api/agent-dist").AllowAnonymous();
        dist.MapMethods("/install.ps1", ["GET", "HEAD"], () => Installer("install.ps1"));
        dist.MapMethods("/install-agent.sh", ["GET", "HEAD"], () => Installer("install-agent.sh"));
        dist.MapMethods("/packages/{platform}", ["GET", "HEAD"], GetPackage);
        dist.MapGet("/status", async (IConfiguration configuration, PlatformAgentSettingsService settings, CancellationToken cancellationToken) => Results.Ok(new
        {
            serverUrl = await settings.GetServerUrlAsync(cancellationToken) ?? PublicBaseUrl(configuration),
            packages = new Dictionary<string, bool>
            {
                ["win-x64"] = PackagePath("win-x64", configuration) is not null,
                ["linux-x64"] = PackagePath("linux-x64", configuration) is not null,
                ["linux-arm64"] = PackagePath("linux-arm64", configuration) is not null,
                ["win-x64-setup"] = PackagePath("win-x64-setup", configuration) is not null
            }
        }));
        return endpoints;
    }

    private static IResult Installer(string name)
    {
        using var stream = typeof(AgentDistributionEndpoints).Assembly.GetManifestResourceStream($"BackupPlatform.AgentInstallers.{name}")
            ?? throw new InvalidOperationException($"Embedded agent installer '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return Results.Content(reader.ReadToEnd(), "text/plain; charset=utf-8");
    }

    private static async Task<IResult> GetPackage(string platform, IConfiguration configuration, PlatformAgentSettingsService settings, CancellationToken cancellationToken)
    {
        if (platform is not ("win-x64" or "linux-x64" or "linux-arm64" or "win-x64-setup"))
            return Results.BadRequest(new { error = "invalid_platform" });
        var path = PackagePath(platform, configuration);
        if (path is null) return Results.NotFound(new { error = "package_not_found" });
        if (platform != "win-x64-setup")
            return Results.File(path, platform == "win-x64" ? "application/zip" : "application/gzip", Path.GetFileName(path));
        var serverUrl = await settings.GetServerUrlAsync(cancellationToken) ?? PublicBaseUrl(configuration);
        if (serverUrl is null) return Results.Conflict(new { error = "server_url_not_configured" });
        var setup = await File.ReadAllBytesAsync(path, cancellationToken);
        var trailer = System.Text.Encoding.UTF8.GetBytes($"{SetupTrailerPrefix}{serverUrl}\n");
        return Results.File([.. setup, .. trailer], "application/vnd.microsoft.portable-executable", Path.GetFileName(path));
    }

    private static string? PackagePath(string platform, IConfiguration configuration)
    {
        var directory = configuration["AgentDistribution:PackagesDirectory"];
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return null;
        var fileName = platform switch { "win-x64" => "backup-agent-win-x64.zip", "win-x64-setup" => "backup-agent-win-x64-setup.exe", _ => $"backup-agent-{platform}.tar.gz" };
        var path = Path.Combine(directory, fileName);
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    private static string? PublicBaseUrl(IConfiguration configuration) =>
        AgentServerUrl.TryNormalize(configuration["AgentDistribution:PublicBaseUrl"], out var normalized) ? normalized : null;
}
