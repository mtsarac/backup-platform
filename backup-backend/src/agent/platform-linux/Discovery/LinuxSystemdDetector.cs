using System.Globalization;
using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Contracts.Discovery;

namespace BackupPlatform.Agent.Platform.Linux.Discovery;

/// <summary>
/// Finds SQL Server (<c>mssql-server</c>) and PostgreSQL (<c>postgresql*</c> units, Debian clusters from <c>pg_lsclusters</c>) registered
/// with systemd. Ports come from the configuration files when readable, otherwise the engine's default. Read-only: no unit is
/// started, stopped or queried beyond the list of running ones.
/// </summary>
public sealed class LinuxSystemdDetector : IInstanceDetector
{
    private const int MaximumOutputCharacters = 256 * 1024;
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] SystemctlArguments = ["list-units", "--type=service", "--state=running", "--no-legend", "--plain"];
    private static readonly string[] PgLsClustersArguments = ["--no-header"];

    public string Source => DiscoverySources.Service;

    public async Task<DetectionResult> DetectAsync(DiscoveryDetectionContext context, CancellationToken cancellationToken)
    {
        var systemctl = await DiscoveryTool.RunAsync("systemctl", SystemctlArguments, ToolTimeout, MaximumOutputCharacters, cancellationToken).ConfigureAwait(false);
        var clusters = await DiscoveryTool.RunAsync("pg_lsclusters", PgLsClustersArguments, ToolTimeout, MaximumOutputCharacters, cancellationToken).ConfigureAwait(false);
        return Build(systemctl, clusters, DiscoveryTool.ReadSmallFile);
    }

    internal static DetectionResult Build(ToolResult systemctl, ToolResult lsClusters, Func<string, string?> readFile)
    {
        var systemdUnavailable = systemctl.Outcome == ToolOutcome.NotFound ? "systemd_not_found"
            : !systemctl.Succeeded && systemctl.Outcome == ToolOutcome.Completed
                && (systemctl.StdErr.Contains("not been booted", StringComparison.OrdinalIgnoreCase)
                    || systemctl.StdErr.Contains("Failed to connect to bus", StringComparison.OrdinalIgnoreCase)) ? "systemd_not_running"
            : null;
        if (systemctl.Outcome == ToolOutcome.AccessDenied)
        {
            return DetectionResult.Unavailable("systemd_access_denied");
        }
        if (systemdUnavailable is null && !systemctl.Succeeded)
        {
            return DetectionResult.Failed("systemd_failed");
        }
        // Without systemd the Debian cluster tool can still tell what runs; with neither there is nothing to scan.
        if (systemdUnavailable is not null && !lsClusters.Succeeded)
        {
            return DetectionResult.Unavailable(systemdUnavailable);
        }

        var units = systemctl.Succeeded ? DiscoveryParsers.ParseSystemdUnits(systemctl.StdOut) : [];
        var clusters = lsClusters.Succeeded
            ? lsClusters.StdOut.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(DiscoveryParsers.ParsePgLsClustersLine).OfType<PostgreSqlCluster>()
                .Where(cluster => cluster.Status.StartsWith("online", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];

        var instances = new Dictionary<string, DetectedInstance>(StringComparer.Ordinal);
        if (units.Any(unit => unit.StartsWith("mssql-server", StringComparison.Ordinal)))
        {
            var port = ReadPort(readFile, "/var/opt/mssql/mssql.conf", DiscoveryParsers.ParseMssqlConfTcpPort) ?? DiscoveryParsers.MssqlDefaultPort;
            instances["mssql|service|default"] = Linux(DiscoveryEngines.MsSql, "mssql|service|default", "SQL Server (mssql-server)", port);
        }
        foreach (var cluster in clusters)
        {
            var key = $"postgres|service|{cluster.Version}-{cluster.Name}";
            instances[key] = Linux(DiscoveryEngines.PostgreSql, key, $"PostgreSQL {cluster.Version} ({cluster.Name})", cluster.Port) with
            {
                InstanceName = cluster.Name,
                Version = cluster.Version,
            };
        }
        foreach (var unit in units.Where(unit => unit.StartsWith("postgresql", StringComparison.Ordinal)))
        {
            var (key, display, version, configuration) = DescribePostgreSqlUnit(unit);
            if (!instances.ContainsKey(key))
            {
                var port = ReadPort(readFile, configuration, DiscoveryParsers.ParsePostgreSqlConfPort) ?? DiscoveryParsers.PostgreSqlDefaultPort;
                instances[key] = Linux(DiscoveryEngines.PostgreSql, key, display, port) with { Version = version };
            }
        }
        return DetectionResult.Scanned([.. instances.Values]);
    }

    /// <summary>
    /// Identity and configuration location of a PostgreSQL unit: <c>postgresql@16-main</c> is the Debian cluster 16/main,
    /// <c>postgresql-16</c> the Red Hat style versioned service, <c>postgresql</c> the unversioned one.
    /// </summary>
    private static (string Key, string DisplayName, string? Version, string Configuration) DescribePostgreSqlUnit(string unit)
    {
        if (unit.StartsWith("postgresql@", StringComparison.Ordinal))
        {
            var instance = unit["postgresql@".Length..];
            var dash = instance.IndexOf('-');
            var (version, cluster) = dash > 0 ? (instance[..dash], instance[(dash + 1)..]) : (instance, "main");
            return ($"postgres|service|{instance}", $"PostgreSQL {version} ({cluster})", version, $"/etc/postgresql/{version}/{cluster}/postgresql.conf");
        }
        var suffix = unit.StartsWith("postgresql-", StringComparison.Ordinal) ? unit["postgresql-".Length..] : null;
        return suffix is { Length: > 0 } && suffix.All(char.IsAsciiDigit)
            ? ($"postgres|service|{unit}", $"PostgreSQL {suffix} ({unit})", suffix, string.Create(CultureInfo.InvariantCulture, $"/var/lib/pgsql/{suffix}/data/postgresql.conf"))
            : ($"postgres|service|{unit}", $"PostgreSQL ({unit})", null, "/var/lib/pgsql/data/postgresql.conf");
    }

    private static DetectedInstance Linux(string engine, string key, string displayName, int port) => new()
    {
        Engine = engine,
        Source = DiscoverySources.Service,
        Key = key,
        DisplayName = displayName,
        Port = port,
        HostPlatform = "Linux",
    };

    private static int? ReadPort(Func<string, string?> readFile, string path, Func<string, int?> parse) =>
        readFile(path) is { } text ? parse(text) : null;
}
