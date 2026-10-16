using System.Globalization;
using BackupPlatform.Contracts.Discovery;

namespace BackupPlatform.Agent.Core.Discovery;

/// <summary>
/// Finds MSSQL and PostgreSQL containers through the <c>docker</c> CLI (<c>docker ps</c>, read-only). The agent asks for no extra
/// privilege: a missing CLI or a denied socket only changes this source's status.
/// </summary>
public sealed class DockerContainerDetector : IInstanceDetector
{
    private const int MaximumOutputCharacters = 256 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly string[] Arguments = ["ps", "--no-trunc", "--format", DiscoveryParsers.DockerFormat];

    public string Source => DiscoverySources.Docker;

    public async Task<DetectionResult> DetectAsync(DiscoveryDetectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IncludeDocker)
        {
            return DetectionResult.Disabled("disabled_by_settings");
        }
        return Interpret(await DiscoveryTool.RunAsync("docker", Arguments, Timeout, MaximumOutputCharacters, cancellationToken).ConfigureAwait(false));
    }

    internal static DetectionResult Interpret(ToolResult result)
    {
        switch (result.Outcome)
        {
            case ToolOutcome.NotFound:
                return DetectionResult.Unavailable("docker_not_found");
            case ToolOutcome.AccessDenied:
                return DetectionResult.Unavailable("docker_access_denied");
            case ToolOutcome.TimedOut:
                return DetectionResult.Failed("docker_timeout");
        }
        if (!result.Succeeded)
        {
            return result.StdErr.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                ? DetectionResult.Unavailable("docker_access_denied")
                : DetectionResult.Failed("docker_failed");
        }
        var instances = result.StdOut.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(DiscoveryParsers.ParseDockerLine)
            .OfType<DockerContainer>()
            .SelectMany(ToInstances)
            .ToList();
        return DetectionResult.Scanned(instances);
    }

    private static IEnumerable<DetectedInstance> ToInstances(DockerContainer container)
    {
        var classified = container.Ports
            .Select(mapping => (mapping, engine: DiscoveryParsers.ClassifyDockerEngine(container.Image, mapping.ContainerPort)))
            .Where(entry => entry.engine is not null)
            .GroupBy(entry => (entry.engine, entry.mapping.ContainerPort))
            .Select(group => group.OrderBy(entry => entry.mapping.HostPort is null).First())
            .ToList();
        if (classified.Count == 0 && DiscoveryParsers.EngineOfImage(container.Image) is { } engineByImage)
        {
            // No exposed port at all: the image still says what runs inside, on that engine's well-known container port.
            var port = engineByImage == DiscoveryEngines.MsSql ? DiscoveryParsers.MssqlDefaultPort : DiscoveryParsers.PostgreSqlDefaultPort;
            classified = [(new DockerPortMapping(null, port), engineByImage)];
        }
        foreach (var (mapping, engine) in classified)
        {
            yield return new DetectedInstance
            {
                Engine = engine!,
                Source = DiscoverySources.Docker,
                Key = string.Create(CultureInfo.InvariantCulture, $"{engine}|docker|{container.Name}|{mapping.ContainerPort}"),
                DisplayName = $"{(engine == DiscoveryEngines.MsSql ? "SQL Server" : "PostgreSQL")} container {container.Name}",
                Port = mapping.HostPort,
                ContainerName = container.Name,
                ContainerImage = container.Image,
                ReasonCode = mapping.HostPort is null ? "no_published_port" : null,
            };
        }
    }
}
