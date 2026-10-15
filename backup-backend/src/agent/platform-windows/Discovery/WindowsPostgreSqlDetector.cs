using System.Text.RegularExpressions;
using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Contracts.Discovery;
using Microsoft.Win32;

namespace BackupPlatform.Agent.Platform.Windows.Discovery;

/// <summary>
/// Finds PostgreSQL services from <c>HKLM\SOFTWARE\PostgreSQL\Installations</c> (the EDB installer) and from <c>postgresql*</c> Windows
/// service registrations, whose <c>ImagePath</c> names the data directory. The port is read from <c>postgresql.conf</c> when
/// the file is readable, otherwise it is the default 5432. Registry and file reads only.
/// </summary>
public sealed partial class WindowsPostgreSqlDetector : IInstanceDetector
{
    private const string InstallationsPath = @"SOFTWARE\PostgreSQL\Installations";
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    private readonly Func<RegistryKey> _openRoot;
    private readonly Func<string, string?> _readFile;

    public WindowsPostgreSqlDetector()
        : this(WindowsRegistry.OpenMachineRoot, DiscoveryTool.ReadSmallFile)
    {
    }

    /// <summary>Reads from another root (a test hive) and another file source; the detector disposes what <paramref name="openRoot"/> returns.</summary>
    internal WindowsPostgreSqlDetector(Func<RegistryKey> openRoot, Func<string, string?> readFile)
    {
        _openRoot = openRoot;
        _readFile = readFile;
    }

    public string Source => DiscoverySources.Service;

    public Task<DetectionResult> DetectAsync(DiscoveryDetectionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(WindowsRegistry.Detect(_openRoot, Read));
    }

    /// <summary>The <c>-D</c> argument of a service command line, quoted or not; null when there is none.</summary>
    internal static string? ParseDataDirectory(string imagePath)
    {
        var match = DataDirectoryArgument().Match(imagePath);
        return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
    }

    [GeneratedRegex("""\s-D\s+(?:"([^"]+)"|(\S+))""")]
    private static partial Regex DataDirectoryArgument();

    private sealed record Registration(string Service, string? Version, string? DataDirectory);

    private List<DetectedInstance> Read(RegistryKey root)
    {
        var registrations = new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);
        using (var installations = root.OpenSubKey(InstallationsPath))
        {
            foreach (var name in installations?.GetSubKeyNames() ?? [])
            {
                using var installation = installations!.OpenSubKey(name);
                var service = WindowsRegistry.Text(installation, "Service ID") ?? name;
                registrations[service] = new Registration(service, WindowsRegistry.Text(installation, "Version"), WindowsRegistry.Text(installation, "Data Directory"));
            }
        }
        using (var services = root.OpenSubKey(ServicesPath))
        {
            foreach (var name in services?.GetSubKeyNames().Where(name => name.StartsWith("postgresql", StringComparison.OrdinalIgnoreCase)) ?? [])
            {
                using var service = services!.OpenSubKey(name);
                var dataDirectory = WindowsRegistry.Text(service, "ImagePath") is { } imagePath ? ParseDataDirectory(imagePath) : null;
                registrations[name] = registrations.TryGetValue(name, out var known)
                    ? known with { DataDirectory = known.DataDirectory ?? dataDirectory }
                    : new Registration(name, null, dataDirectory);
            }
        }

        return [.. registrations.Values.OrderBy(registration => registration.Service, StringComparer.OrdinalIgnoreCase).Select(registration =>
        {
            var configuration = registration.DataDirectory is { } directory ? _readFile(Path.Combine(directory, "postgresql.conf")) : null;
            return new DetectedInstance
            {
                Engine = DiscoveryEngines.PostgreSql,
                Source = DiscoverySources.Service,
                Key = $"postgres|service|{registration.Service}",
                DisplayName = registration.Version is { } version ? $"PostgreSQL {version} ({registration.Service})" : $"PostgreSQL ({registration.Service})",
                InstanceName = registration.Service,
                Port = (configuration is null ? null : DiscoveryParsers.ParsePostgreSqlConfPort(configuration)) ?? DiscoveryParsers.PostgreSqlDefaultPort,
                Version = registration.Version,
                HostPlatform = "Windows",
            };
        })];
    }
}
