using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.BackupProviders.PostgreSql.Discovery;

/// <summary>Evaluates what a server and a database allow. Pure; every explanation is a fixed sentence that never embeds server-supplied text.</summary>
internal static class PostgreSqlCapabilityEvaluator
{
    public static DatabaseCapabilitySet EvaluateInstance(PostgreSqlServerInfo server)
    {
        var supported = server.Major >= PostgreSqlDiscovery.MinimumServerMajor;
        DatabaseCapability Capability(DatabaseCapabilityId id, bool ok, string okReason, string okText, string noReason, string noText) =>
            new(id, ok ? DatabaseCapabilitySupport.Supported : DatabaseCapabilitySupport.Unsupported, ok ? okReason : noReason, ok ? okText : noText);

        return new DatabaseCapabilitySet(
        [
            Capability(PostgreSqlCapabilityIds.FullBackup, supported, "pg-dump-custom", "A consistent logical full backup is available (pg_dump, custom archive).",
                PostgreSqlReasons.ServerVersionUnsupported, "This PostgreSQL server version is older than the supported minimum."),
            Capability(PostgreSqlCapabilityIds.BackupCompression, supported, "archive-compression", "The custom archive is compressed.",
                PostgreSqlReasons.ServerVersionUnsupported, "This PostgreSQL server version is older than the supported minimum."),
            Capability(PostgreSqlCapabilityIds.CopyOnly, supported, "logical-dump", "A logical dump never affects a backup chain.",
                PostgreSqlReasons.ServerVersionUnsupported, "This PostgreSQL server version is older than the supported minimum."),
            Capability(PostgreSqlCapabilityIds.BackupChecksum, supported && server.DataChecksums, "data-checksums-on", "Data checksums are enabled, so every page the dump reads is verified.",
                supported ? "data-checksums-off" : PostgreSqlReasons.ServerVersionUnsupported,
                supported ? "Data checksums are disabled on this cluster." : "This PostgreSQL server version is older than the supported minimum."),
            new(PostgreSqlCapabilityIds.DifferentialBackup, DatabaseCapabilitySupport.Unsupported, "not-implemented", "Differential backup is not implemented."),
            new(PostgreSqlCapabilityIds.IncrementalBackup, DatabaseCapabilitySupport.Unsupported, "not-implemented", "Incremental backup is not implemented."),
            new(PostgreSqlCapabilityIds.WalArchiving, DatabaseCapabilitySupport.Unsupported, "not-implemented", "WAL archiving and point-in-time recovery are not implemented.")
        ]);
    }

    /// <summary>The database-level view: backup capabilities are withdrawn for a database that cannot be dumped (template0 refuses connections; a login without CONNECT cannot dump).</summary>
    public static DatabaseCapabilitySet EvaluateDatabase(DatabaseCapabilitySet instance, PostgreSqlDatabaseInfo database)
    {
        if (database.AllowsConnections && database.CanConnect)
        {
            return instance;
        }

        var reason = database.AllowsConnections ? "connect-privilege-missing" : "connections-disallowed";
        var text = database.AllowsConnections ? "The login has no CONNECT privilege on this database." : "This database does not accept connections.";
        return new DatabaseCapabilitySet(instance.Select(capability =>
            PostgreSqlCapabilityIds.IsBackupCapability(capability.Id) && capability.Support == DatabaseCapabilitySupport.Supported
                ? new DatabaseCapability(capability.Id, DatabaseCapabilitySupport.Unsupported, reason, text)
                : capability));
    }
}
