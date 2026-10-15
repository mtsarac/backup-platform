using BackupPlatform.Contracts.Discovery;

namespace BackupPlatform.Agent.Core.Discovery;

/// <summary>A database instance a detector found on this host. Finding it is not access to it: nothing here is credentialed.</summary>
public sealed record DetectedInstance
{
    public required string Engine { get; init; }
    public required string Source { get; init; }

    /// <summary>Stable identity on this agent, e.g. <c>mssql|service|SQLEXPRESS</c>; survives restarts and port changes.</summary>
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public string? InstanceName { get; init; }
    public string Host { get; init; } = "localhost";

    /// <summary>The loopback TCP port; null when the source cannot tell (a dynamic port, a container without a published port).</summary>
    public int? Port { get; init; }
    public string? Version { get; init; }
    public string? Edition { get; init; }
    public string? HostPlatform { get; init; }
    public string? ContainerName { get; init; }
    public string? ContainerImage { get; init; }

    /// <summary>Set by the scanner from the loopback handshake, never by a detector.</summary>
    public string Reachability { get; init; } = DiscoveryReachability.Unknown;

    /// <summary>Fixed code explaining why the instance cannot be connected to (e.g. <c>no_published_port</c>).</summary>
    public string? ReasonCode { get; init; }
}

/// <summary>What a detector can say about its source, independent of how many instances it found.</summary>
public sealed record DetectionResult(string Status, string? ReasonCode, IReadOnlyList<DetectedInstance> Instances)
{
    public static DetectionResult Scanned(IReadOnlyList<DetectedInstance> instances) => new(DiscoverySourceStates.Scanned, null, instances);

    public static DetectionResult Unavailable(string reasonCode) => new(DiscoverySourceStates.Unavailable, reasonCode, []);

    public static DetectionResult Disabled(string reasonCode) => new(DiscoverySourceStates.Disabled, reasonCode, []);

    public static DetectionResult Failed(string reasonCode) => new(DiscoverySourceStates.Failed, reasonCode, []);
}

public sealed record DiscoveryDetectionContext(bool IncludeDocker);

/// <summary>
/// Finds instances of one <see cref="DiscoverySources"/> source. Implementations only read (registry, files, a short-lived tool) and
/// must honour the token; expected problems (tool absent, access denied) are statuses, not exceptions.
/// </summary>
public interface IInstanceDetector
{
    /// <summary>One of <see cref="DiscoverySources"/>. Several detectors may share a source; the scanner combines their statuses.</summary>
    string Source { get; }

    Task<DetectionResult> DetectAsync(DiscoveryDetectionContext context, CancellationToken cancellationToken);
}
