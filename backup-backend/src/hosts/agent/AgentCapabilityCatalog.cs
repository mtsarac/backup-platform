using BackupPlatform.BackupProviders.MSSQL.Capabilities;
using BackupPlatform.BackupProviders.PostgreSql;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Discovery;
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

    public static AgentCapabilityCatalog CreateDefault() => Create(sqlServer: true);

    /// <summary>
    /// <paramref name="sqlServer"/> is whether this agent's configuration can run SQL Server work at all (see <see cref="SqlServerEligibility"/>). When it cannot,
    /// the SQL Server capabilities are reported Unsupported so the backend refuses SQL Server jobs for this agent up front.
    /// </summary>
    public static AgentCapabilityCatalog Create(bool sqlServer)
    {
        Func<string, AgentCapabilityReport> sql = sqlServer ? Supported : Unsupported;
        return new(
    [
        sql(SqlServerCapabilityIds.FullBackup.Value),
        sql(SqlServerCapabilityIds.BackupChecksum.Value),
        sql(SqlServerCapabilityIds.BackupCompression.Value),
        sql(SqlServerCapabilityIds.CopyOnly.Value),
        sql(SqlServerCapabilityIds.RestoreVerifyOnly.Value),
        sql(SqlServerCapabilityIds.RestoreDatabase.Value),
        Supported(RestoreTaskTypes.PrecheckCapability),
        Supported(RestoreTaskTypes.DiscoveryCapability),
        sql(RestoreTaskTypes.MssqlVerificationCapability),
        Supported(RestoreTaskTypes.PostgresVerificationCapability),
        Supported("file-system.backup.full"),
        Supported("file-system.restore.full"),
        Supported("file-system.restore.precheck"),
        Supported("file-system.restore.discovery"),
        Supported(PostgreSqlCapabilityIds.FullBackup.Value),
        Supported(PostgreSqlCapabilityIds.BackupChecksum.Value),
        Supported(PostgreSqlCapabilityIds.BackupCompression.Value),
        Supported(PostgreSqlCapabilityIds.CopyOnly.Value),
        Supported(PostgreSqlCapabilityIds.RestoreFull.Value),
        Supported(PostgreSqlCapabilityIds.RestorePrecheck.Value),
        Supported(PostgreSqlCapabilityIds.RestoreDiscovery.Value),
        Supported(PostgreSqlCapabilityIds.RestoreVerify.Value),
        Unsupported(PostgreSqlCapabilityIds.DifferentialBackup.Value),
        Unsupported(PostgreSqlCapabilityIds.IncrementalBackup.Value),
        Unsupported(PostgreSqlCapabilityIds.WalArchiving.Value),
        Supported(DownloadTaskTypes.Capability),
        Supported(TaskStorageProtocol.Capability),
        Supported(TaskStorageProtocol.DirectCapability),
        Supported(ReplicaLifecycleProtocol.Capability),
        Supported("storage.s3"),
        Supported("storage.smb"),
        Supported("backup.three-two-one"),
        Supported(BackupTaskProtocol.MultiDestinationCapability),
        Supported(BackupTaskProtocol.QuotaBoundedUploadCapability),
        Supported(BackupTaskProtocol.UploadBandwidthLimitCapability),
        Supported(DiscoveryProtocol.Capability),
        Supported(BackupPlatform.Contracts.Storage.CapacityProbeProtocol.Capability),
        // Not implemented in this product yet, whatever the SQL Server instance could do.
        Unsupported(SqlServerCapabilityIds.DifferentialBackup.Value),
        Unsupported(SqlServerCapabilityIds.LogBackup.Value)
    ]);
    }

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
