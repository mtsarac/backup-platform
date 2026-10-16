namespace BackupPlatform.Api.Endpoints;

public static class AgentDistributionEndpoints
{
    public static IEndpointRouteBuilder MapAgentDistributionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var dist = endpoints.MapGroup("/api/agent-dist").AllowAnonymous();
        dist.MapMethods("/install.ps1", ["GET", "HEAD"], () => Installer("install.ps1"));
        dist.MapMethods("/install.sh", ["GET", "HEAD"], () => Installer("install.sh"));
        dist.MapMethods("/packages/{platform}", ["GET", "HEAD"], GetPackage);
        dist.MapGet("/status", (IConfiguration configuration) => Results.Ok(new
        {
            serverUrl = PublicBaseUrl(configuration),
            packages = new Dictionary<string, bool>
            {
                ["win-x64"] = PackagePath("win-x64", configuration) is not null,
                ["linux-x64"] = PackagePath("linux-x64", configuration) is not null
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

    private static IResult GetPackage(string platform, IConfiguration configuration)
    {
        if (platform is not ("win-x64" or "linux-x64"))
            return Results.BadRequest(new { error = "invalid_platform" });
        var path = PackagePath(platform, configuration);
        return path is null
            ? Results.NotFound(new { error = "package_not_found" })
            : Results.File(path, platform == "win-x64" ? "application/zip" : "application/gzip", Path.GetFileName(path));
    }

    private static string? PackagePath(string platform, IConfiguration configuration)
    {
        var directory = configuration["AgentDistribution:PackagesDirectory"];
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return null;
        var path = Path.Combine(directory, platform == "win-x64" ? "backup-agent-win-x64.zip" : "backup-agent-linux-x64.tar.gz");
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    private static string? PublicBaseUrl(IConfiguration configuration)
    {
        var value = configuration["AgentDistribution:PublicBaseUrl"];
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri.AbsoluteUri.TrimEnd('/') : null;
    }
}
