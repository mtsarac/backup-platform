using System.Text.Json;
using System.Globalization;
using BackupPlatform.Contracts.Discovery;

namespace BackupPlatform.Agent.Core.Discovery;

/// <summary>
/// Finds database containers through Docker's read-only local Engine API. Only published loopback-reachable TCP ports
/// become backup endpoints; container environment variables and credentials are never requested.
/// </summary>
public sealed class DockerContainerDetector : IInstanceDetector
{
    private const int MaximumOutputCharacters = 256 * 1024;
    private readonly IDockerEngineTransport transport;

    public DockerContainerDetector(IDockerEngineTransport transport) =>
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    public string Source => DiscoverySources.Docker;

    public async Task<DetectionResult> DetectAsync(DiscoveryDetectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IncludeDocker)
        {
            return DetectionResult.Disabled("disabled_by_settings");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var payload = await transport.QueryContainersAsync(timeout.Token).ConfigureAwait(false);
            if (payload is null) return DetectionResult.Unavailable("docker_unavailable");
            using var document = JsonDocument.Parse(payload);
            var instances = new List<DetectedInstance>();
            foreach (var container in document.RootElement.EnumerateArray())
            {
                var name = container.GetProperty("Names").EnumerateArray().FirstOrDefault().GetString()?.TrimStart('/');
                var image = container.GetProperty("Image").GetString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(image)) continue;
                var mappings = new List<DockerPortMapping>();
                foreach (var port in container.GetProperty("Ports").EnumerateArray())
                {
                    if (port.GetProperty("Type").GetString() != "tcp") continue;
                    var address = port.TryGetProperty("IP", out var ip) ? ip.GetString() : null;
                    int? host = (address is null or "" or "0.0.0.0" or "::" or "127.0.0.1" or "::1")
                        && port.TryGetProperty("PublicPort", out var published) ? published.GetInt32() : null;
                    var privatePort = port.GetProperty("PrivatePort").GetInt32();
                    if (host is < 1 or > 65535 || privatePort is < 1 or > 65535) return DetectionResult.Failed("docker_invalid_response");
                    mappings.Add(new DockerPortMapping(host, privatePort));
                }
                instances.AddRange(ToInstances(new DockerContainer(name, image, mappings)));
            }
            return DetectionResult.Scanned(instances);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return DetectionResult.Failed("docker_timeout"); }
        catch (UnauthorizedAccessException)
        {
            return DetectionResult.Unavailable("docker_access_denied");
        }
        catch (IOException)
        {
            return DetectionResult.Unavailable("docker_socket_unavailable");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return DetectionResult.Failed("docker_invalid_response");
        }
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
