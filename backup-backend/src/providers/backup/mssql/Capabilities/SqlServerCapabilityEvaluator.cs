using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Discovery;

namespace BackupPlatform.BackupProviders.MSSQL.Capabilities;

/// <summary>
/// Evaluates capabilities from discovered metadata only; nothing is executed. Instance evaluation applies the edition matrix
/// in <see cref="SqlServerCapabilityRules"/> to version, engine edition and edition family. Database evaluation narrows an
/// instance result by restrictions that belong to a specific database (system databases, snapshots, recovery model).
/// </summary>
public static class SqlServerCapabilityEvaluator
{
    public static DatabaseCapabilitySet EvaluateInstance(SqlServerInstanceInfo instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new DatabaseCapabilitySet(SqlServerCapabilityRules.All.Select(rule => Evaluate(rule, instance)));
    }

    public static DatabaseCapabilitySet EvaluateDatabase(DatabaseCapabilitySet instanceCapabilities, SqlServerDatabaseInfo database)
    {
        ArgumentNullException.ThrowIfNull(instanceCapabilities);
        ArgumentNullException.ThrowIfNull(database);

        return new DatabaseCapabilitySet(instanceCapabilities.Select(capability =>
            Restrict(capability, database) is { } restriction ? Narrow(capability, restriction) : capability));
    }

    private static DatabaseCapability Evaluate(CapabilityRule rule, SqlServerInstanceInfo instance)
    {
        if (instance.Version is not { } version)
        {
            return new DatabaseCapability(rule.Id, DatabaseCapabilitySupport.Unknown, SqlServerCapabilityReasons.VersionUnknown,
                "The server version could not be determined.");
        }

        // Azure SQL Database reports a legacy 12.x version; its rows answer before any version gate would misjudge it.
        var edition = instance.Edition;
        var row = rule.Rows.FirstOrDefault(candidate =>
            candidate.Engine == edition.Engine && (candidate.Family is null || candidate.Family == edition.Family));

        if (row is { Engine: SqlServerEngineEdition.AzureSqlDatabase })
        {
            return new DatabaseCapability(rule.Id, row.Support, row.ReasonCode, row.Explanation);
        }

        if (version < rule.MinimumVersion)
        {
            return TooOld(rule.Id, rule.MinimumVersion);
        }

        if (row is null)
        {
            return new DatabaseCapability(rule.Id, DatabaseCapabilitySupport.Unknown, SqlServerCapabilityReasons.EngineEditionNotEvaluated,
                "The capability rules do not cover this engine edition, so the feature is not assumed.");
        }

        if (row.Support == DatabaseCapabilitySupport.Supported && row.MinVersion is { } editionMinimum && version < editionMinimum)
        {
            return TooOld(rule.Id, editionMinimum);
        }

        return new DatabaseCapability(rule.Id, row.Support, row.ReasonCode, row.Explanation);
    }

    private static DatabaseCapability TooOld(DatabaseCapabilityId id, SqlServerVersion minimum) =>
        new(id, DatabaseCapabilitySupport.Unsupported, SqlServerCapabilityReasons.VersionTooOld,
            $"This edition provides the feature from SQL Server {minimum.Major}.{minimum.Minor}.");

    // A database restriction is a definite negative, or an Unknown when the metadata needed to decide is missing.
    private static DatabaseCapability? Restrict(DatabaseCapability capability, SqlServerDatabaseInfo database)
    {
        var id = capability.Id;
        if (SqlServerCapabilityIds.IsBackupCapability(id))
        {
            if (database.IsSnapshot)
            {
                return Deny(id, SqlServerCapabilityReasons.DatabaseSnapshot, "A database snapshot cannot be backed up.");
            }

            if (database.SystemDatabase == SqlServerSystemDatabase.Tempdb)
            {
                return Deny(id, SqlServerCapabilityReasons.SystemDatabaseTempdb, "tempdb is re-created at every start and cannot be backed up.");
            }

            if (database.SystemDatabase == SqlServerSystemDatabase.Master
                && (id == SqlServerCapabilityIds.DifferentialBackup || id == SqlServerCapabilityIds.LogBackup))
            {
                return Deny(id, SqlServerCapabilityReasons.SystemDatabaseMasterFullOnly, "master supports full backups only.");
            }
        }

        if (id == SqlServerCapabilityIds.LogBackup)
        {
            return database.RecoveryModel switch
            {
                SqlServerRecoveryModel.Simple => Deny(id, SqlServerCapabilityReasons.RecoveryModelSimple,
                    "Transaction log backups need the FULL or BULK_LOGGED recovery model."),
                SqlServerRecoveryModel.Unknown => new DatabaseCapability(id, DatabaseCapabilitySupport.Unknown,
                    SqlServerCapabilityReasons.RecoveryModelUnknown, "The recovery model is not known, so log backup support cannot be decided."),
                _ => null
            };
        }

        return null;
    }

    // A restriction never upgrades a result. A definite negative replaces anything but an existing negative (which keeps its own,
    // usually more fundamental, reason); an Unknown restriction only weakens a Supported result.
    private static DatabaseCapability Narrow(DatabaseCapability current, DatabaseCapability restriction) =>
        current.Support == DatabaseCapabilitySupport.Unsupported ? current
        : restriction.Support == DatabaseCapabilitySupport.Unsupported ? restriction
        : current.Support == DatabaseCapabilitySupport.Supported ? restriction
        : current;

    private static DatabaseCapability Deny(DatabaseCapabilityId id, string reasonCode, string explanation) =>
        new(id, DatabaseCapabilitySupport.Unsupported, reasonCode, explanation);
}
