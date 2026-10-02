using System.Reflection;
using System.Runtime.InteropServices;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Core.Connectivity;

/// <summary>Static facts the heartbeat reports about this agent build and machine.</summary>
public sealed record AgentSoftwareInfo(string Version, string Platform, string? OsDescription, string? RuntimeVersion)
{
    private const int MaximumTextLength = 128;

    /// <summary>
    /// Reads the release version from the assembly's informational version (build metadata after <c>+</c> is dropped),
    /// falling back to the assembly version, and the platform from the running operating system.
    /// </summary>
    public static AgentSoftwareInfo Detect(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational is { Length: > 0 }
            ? informational.Split('+', 2)[0]
            : assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var platform = OperatingSystem.IsWindows() ? AgentPlatformNames.Windows
            : OperatingSystem.IsLinux() ? AgentPlatformNames.Linux
            : throw new PlatformNotSupportedException("The agent runs on Windows and Linux only.");
        return new AgentSoftwareInfo(version, platform, Truncate(RuntimeInformation.OSDescription), Environment.Version.ToString());
    }

    internal AgentHeartbeatRequest ToHeartbeat(string? capabilityRevision) => new()
    {
        AgentVersion = Version,
        Platform = Platform,
        CapabilityRevision = capabilityRevision,
        Runtime = new AgentRuntimeInfo { OsDescription = OsDescription, RuntimeVersion = RuntimeVersion },
    };

    private static string Truncate(string value) => value.Length <= MaximumTextLength ? value : value[..MaximumTextLength];
}
