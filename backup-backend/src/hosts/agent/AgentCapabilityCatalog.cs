using BackupPlatform.BackupProviders.MSSQL.Capabilities;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Downloads;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Agent.Host;

/// <summary>
/// The capabilities this agent build advertises, with the revision derived from them. The content is static and
/// honest: it states what the product implements, not what a particular SQL Server instance allows. Instance-level
/// support is still decided by the provider's pre-check when a backup or restore runs.
/// </summary>
public sealed class AgentCapabilityCatalog
{
    private const string ScopeKey = "scope";
    private const string AgentScope = "agent";

    public AgentCapabilityCatalog(IEnumerable<AgentCapabilityReport> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        Capabilities = capabilities.ToList();
        Revision = AgentCapabilityRevision.Compute(Capabilities);
    }

    public IReadOnlyList<AgentCapabilityReport> Capabilities { get; }

    /// <summary>Revision of <see cref="Capabilities"/>, shared with the backend's verification.</summary>
    public string Revision { get; }

    public static AgentCapabilityCatalog CreateDefault() => new(
    [
        Supported(SqlServerCapabilityIds.FullBackup.Value),
        Supported(SqlServerCapabilityIds.BackupChecksum.Value),
        Supported(SqlServerCapabilityIds.BackupCompression.Value),
        Supported(SqlServerCapabilityIds.CopyOnly.Value),
        Supported(SqlServerCapabilityIds.RestoreVerifyOnly.Value),
        Supported(SqlServerCapabilityIds.RestoreDatabase.Value),
        Supported(RestoreTaskTypes.PrecheckCapability),
        Supported(RestoreTaskTypes.DiscoveryCapability),
        Supported(DownloadTaskTypes.Capability),
        Supported(TaskStorageProtocol.Capability),
        // Not implemented in this product yet, whatever the SQL Server instance could do.
        Unsupported(SqlServerCapabilityIds.DifferentialBackup.Value),
        Unsupported(SqlServerCapabilityIds.LogBackup.Value)
    ]);

    private static AgentCapabilityReport Supported(string identifier) => new()
    {
        Identifier = identifier,
        Support = AgentCapabilitySupportNames.Supported,
        Metadata = new Dictionary<string, string> { [ScopeKey] = AgentScope }
    };

    private static AgentCapabilityReport Unsupported(string identifier) => new()
    {
        Identifier = identifier,
        Support = AgentCapabilitySupportNames.Unsupported
    };
}
