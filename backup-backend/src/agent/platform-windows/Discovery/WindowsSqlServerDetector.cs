using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Contracts.Discovery;
using Microsoft.Win32;

namespace BackupPlatform.Agent.Platform.Windows.Discovery;

/// <summary>
/// Finds the SQL Server instances registered on this machine from <c>HKLM\SOFTWARE\Microsoft\Microsoft SQL Server</c>: the instance names,
/// each instance's TCP port (<c>IPAll</c>: <c>TcpPort</c>, otherwise the first <c>TcpDynamicPorts</c>) and its edition and patch level. Registry reads only.
/// </summary>
public sealed class WindowsSqlServerDetector : IInstanceDetector
{
    private const string SqlServerPath = @"SOFTWARE\Microsoft\Microsoft SQL Server";
    private const string DefaultInstanceName = "MSSQLSERVER";

    private readonly Func<RegistryKey> _openRoot;

    public WindowsSqlServerDetector()
        : this(WindowsRegistry.OpenMachineRoot)
    {
    }

    /// <summary>Reads from another root (a test hive); the detector disposes what <paramref name="openRoot"/> returns.</summary>
    internal WindowsSqlServerDetector(Func<RegistryKey> openRoot) => _openRoot = openRoot;

    public string Source => DiscoverySources.Service;

    public Task<DetectionResult> DetectAsync(DiscoveryDetectionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(WindowsRegistry.Detect(_openRoot, Read));
    }

    private static List<DetectedInstance> Read(RegistryKey root)
    {
        var instances = new List<DetectedInstance>();
        using var names = root.OpenSubKey($@"{SqlServerPath}\Instance Names\SQL");
        foreach (var name in names?.GetValueNames() ?? [])
        {
            // The value is the registry folder of the instance (MSSQL16.SQLEXPRESS); it is a path segment, so never accept a separator.
            if (WindowsRegistry.Text(names, name) is not { } folder || folder.Contains('\\', StringComparison.Ordinal) || name.Length == 0)
            {
                continue;
            }
            var isDefault = name.Equals(DefaultInstanceName, StringComparison.OrdinalIgnoreCase);
            using var tcp = root.OpenSubKey($@"{SqlServerPath}\{folder}\MSSQLServer\SuperSocketNetLib\Tcp\IPAll");
            using var setup = root.OpenSubKey($@"{SqlServerPath}\{folder}\Setup");
            var port = DiscoveryParsers.ParsePort(WindowsRegistry.Text(tcp, "TcpPort"))
                ?? DiscoveryParsers.ParsePort(WindowsRegistry.Text(tcp, "TcpDynamicPorts")?.Split(',')[0])
                ?? (isDefault ? DiscoveryParsers.MssqlDefaultPort : null);
            var key = $"mssql|service|{(isDefault ? "default" : name)}";
            instances.Add(new DetectedInstance
            {
                Engine = DiscoveryEngines.MsSql,
                Source = DiscoverySources.Service,
                Key = key,
                DisplayName = isDefault ? "SQL Server (default instance)" : $"SQL Server ({name})",
                InstanceName = isDefault ? null : name,
                Port = port,
                Version = WindowsRegistry.Text(setup, "PatchLevel") ?? WindowsRegistry.Text(setup, "Version"),
                Edition = WindowsRegistry.Text(setup, "Edition"),
                HostPlatform = "Windows",
            });
        }
        return instances;
    }
}
