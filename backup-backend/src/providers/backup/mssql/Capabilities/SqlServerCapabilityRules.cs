using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Discovery;

namespace BackupPlatform.BackupProviders.MSSQL.Capabilities;

/// <summary>Stable reason codes attached to evaluated capabilities.</summary>
public static class SqlServerCapabilityReasons
{
    public const string FeatureAvailable = "feature-available";
    public const string VersionUnknown = "version-unknown";
    public const string VersionTooOld = "version-too-old";
    public const string EditionNotSupported = "edition-not-supported";
    public const string EditionUnverified = "edition-unverified";
    public const string EngineEditionNotEvaluated = "engine-edition-not-evaluated";
    public const string ManagedService = "managed-service";
    public const string SystemDatabaseTempdb = "system-database-tempdb";
    public const string SystemDatabaseMasterFullOnly = "system-database-master-full-only";
    public const string DatabaseSnapshot = "database-snapshot";
    public const string RecoveryModelSimple = "recovery-model-simple";
    public const string RecoveryModelUnknown = "recovery-model-unknown";
}

/// <summary>
/// One line of a capability's edition matrix. <see cref="Family"/> null matches every product sharing the engine edition.
/// A supported row may carry its own <see cref="MinVersion"/> where an edition gained the feature later than the rest.
/// </summary>
internal sealed record EditionRow(
    SqlServerEngineEdition Engine,
    SqlServerEditionFamily? Family,
    DatabaseCapabilitySupport Support,
    string ReasonCode,
    string Explanation,
    SqlServerVersion? MinVersion = null);

/// <summary>A capability's rule: the first release that has the feature at all, then an ordered edition matrix (first match wins).</summary>
internal sealed record CapabilityRule(DatabaseCapabilityId Id, SqlServerVersion MinimumVersion, IReadOnlyList<EditionRow> Rows);

/// <summary>
/// The capability matrix as data. Adding an edition, a version gate or a capability is a row here, not another branch.
/// Support is declared only where the documented edition and version matrix is unambiguous; everything else is
/// <see cref="DatabaseCapabilitySupport.Unknown"/> so a guess is never presented as a fact.
/// </summary>
internal static class SqlServerCapabilityRules
{
    private static readonly SqlServerVersion Sql2005 = new(9, 0, 0);
    private static readonly SqlServerVersion Sql2008 = new(10, 0, 0);
    private static readonly SqlServerVersion Sql2008R2 = new(10, 50, 0);
    private static readonly SqlServerVersion Sql2014 = new(12, 0, 0);
    private static readonly SqlServerVersion Sql2019 = new(15, 0, 0);

    public static IReadOnlyList<CapabilityRule> All { get; } =
    [
        new(SqlServerCapabilityIds.FullBackup, Sql2005, CoreBackupRows()),
        new(SqlServerCapabilityIds.DifferentialBackup, Sql2005, CoreBackupRows()),
        new(SqlServerCapabilityIds.LogBackup, Sql2005, CoreBackupRows()),
        new(SqlServerCapabilityIds.BackupChecksum, Sql2005, CoreBackupRows()),
        new(SqlServerCapabilityIds.CopyOnly, Sql2005, CoreBackupRows()),
        new(SqlServerCapabilityIds.RestoreVerifyOnly, Sql2005, CoreBackupRows()),

        // Compression: 2008 Enterprise-class only; Standard gained it in 2008 R2. Express cannot create compressed backups.
        // Web and Business Intelligence share engine edition 2 with Standard and are not asserted.
        new(SqlServerCapabilityIds.BackupCompression, Sql2008,
        [
            Managed(),
            Supported(SqlServerEngineEdition.Enterprise),
            Supported(SqlServerEngineEdition.Standard, SqlServerEditionFamily.Standard, Sql2008R2),
            Unverified(SqlServerEngineEdition.Standard),
            Unsupported(SqlServerEngineEdition.Express, SqlServerCapabilityReasons.EditionNotSupported,
                "This edition cannot create compressed backups.")
        ]),

        // Backup encryption: 2014 and later. Only the Enterprise engine class is asserted; it additionally needs a certificate
        // or asymmetric key at backup time, which no instance metadata can promise.
        new(SqlServerCapabilityIds.BackupEncryption, Sql2014,
        [
            Managed(),
            Supported(SqlServerEngineEdition.Enterprise),
            Unverified(SqlServerEngineEdition.Standard),
            Unverified(SqlServerEngineEdition.Express)
        ]),

        // TDE: Enterprise-class editions; Standard from 2019. Other editions are not asserted.
        new(SqlServerCapabilityIds.TransparentDataEncryption, Sql2008,
        [
            Supported(SqlServerEngineEdition.Enterprise),
            Supported(SqlServerEngineEdition.Standard, SqlServerEditionFamily.Standard, Sql2019),
            Unverified(SqlServerEngineEdition.Standard),
            Unverified(SqlServerEngineEdition.Express)
        ])
    ];

    // BACKUP DATABASE exists on every engine class of the product. Azure SQL Database takes platform-managed backups instead.
    // Managed Instance, Edge and Synapse are intentionally absent: they are not evaluated, so their capabilities stay Unknown.
    private static EditionRow[] CoreBackupRows() =>
    [
        Managed(),
        Supported(SqlServerEngineEdition.Personal),
        Supported(SqlServerEngineEdition.Standard),
        Supported(SqlServerEngineEdition.Enterprise),
        Supported(SqlServerEngineEdition.Express)
    ];

    private static EditionRow Supported(SqlServerEngineEdition engine, SqlServerEditionFamily? family = null, SqlServerVersion? minVersion = null) =>
        new(engine, family, DatabaseCapabilitySupport.Supported, SqlServerCapabilityReasons.FeatureAvailable,
            "The edition and version provide this feature.", minVersion);

    private static EditionRow Unsupported(SqlServerEngineEdition engine, string reasonCode, string explanation) =>
        new(engine, null, DatabaseCapabilitySupport.Unsupported, reasonCode, explanation);

    private static EditionRow Unverified(SqlServerEngineEdition engine) =>
        new(engine, null, DatabaseCapabilitySupport.Unknown, SqlServerCapabilityReasons.EditionUnverified,
            "The edition shares an engine class with products whose support differs, so this is not asserted.");

    private static EditionRow Managed() =>
        Unsupported(SqlServerEngineEdition.AzureSqlDatabase, SqlServerCapabilityReasons.ManagedService,
            "Azure SQL Database backs up automatically and does not accept user-initiated backups.");
}
