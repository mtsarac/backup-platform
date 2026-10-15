using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Discovery;

namespace BackupPlatform.Application.Discovery;

/// <summary>
/// Advisory duplicate detection: which existing job already backs up a discovered database. A job only counts when it sits on the same agent (the
/// caller filters by agent), uses the same engine, port and database name, and addresses the local machine. Never blocks job creation.
/// </summary>
public static class DiscoveryJobMatcher
{
    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1", "[::1]", "." };

    public static bool Covers(BackupConfiguration configuration, DiscoveredInstance instance, string databaseName) =>
        configuration.Engine == instance.Engine
        && (configuration.Port ?? DefaultPort(configuration.Engine)) == instance.Port
        && LoopbackHosts.Contains(configuration.Host) && LoopbackHosts.Contains(instance.Host)
        && string.Equals(configuration.Database, databaseName, instance.Engine == "mssql" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static int DefaultPort(string engine) => engine == "mssql" ? 1433 : 5432;
}
