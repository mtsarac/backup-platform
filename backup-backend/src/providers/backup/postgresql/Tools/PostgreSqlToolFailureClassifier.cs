using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.BackupProviders.PostgreSql.Tools;

/// <summary>A tool failure as the platform reports it: a stable reason, a fixed user-safe sentence and the engine-independent cause.</summary>
internal sealed record PostgreSqlToolFailure(string ReasonCode, string Message, BackupFailureCause Cause);

internal enum PostgreSqlToolKind
{
    Dump,
    Restore,
    Verify
}

/// <summary>
/// Turns the last stderr lines of pg_dump/pg_restore into a classified, user-safe failure. The tools' text is only ever matched here, never
/// forwarded: messages can name tables, roles or hosts. The server's own SQLSTATE-based classification (pre-check) is preferred wherever a
/// connection exists; this is the fallback for what happens inside the tool. Pure.
/// </summary>
internal static class PostgreSqlToolFailureClassifier
{
    public static PostgreSqlToolFailure Classify(PostgreSqlToolKind kind, IReadOnlyList<string> errorLines)
    {
        var text = string.Join('\n', errorLines);
        bool Has(string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);

        if (Has("password authentication failed") || Has("no pg_hba.conf entry") || Has("authentication failed") || Has("SCRAM authentication")
            || (Has("role \"") && Has("does not exist") && Has("FATAL")) || Has("no password supplied") || Has("fe_sendauth"))
        {
            return new(PostgreSqlReasons.AuthenticationFailed, "The server rejected the login.", BackupFailureCause.AuthenticationFailed);
        }

        if (Has("aborting because of server version mismatch") || Has("server version:") && Has("pg_dump version:"))
        {
            return new(PostgreSqlReasons.ToolVersionTooOld, "The installed pg_dump is older than the PostgreSQL server, so it refuses to dump it. Install a client at least as new as the server.", BackupFailureCause.Unsupported);
        }

        if (Has("unsupported version") && Has("in file header"))
        {
            return new(PostgreSqlReasons.ArchiveVersionUnsupported, "The archive was written by a newer pg_dump than the installed pg_restore can read.", BackupFailureCause.Unsupported);
        }

        if (Has("does not appear to be a valid archive") || Has("input file is too short") || Has("did not find magic string") || Has("unexpected end of file")
            || Has("could not read from input file") || Has("corrupt") || Has("invalid") && Has("archive") || Has("data block") && Has("does not match")
            || Has("error during file seek") || Has("could not uncompress") || Has("could not read data block") || Has("zlib") || Has("decompress"))
        {
            return new(PostgreSqlReasons.ArchiveInvalid, "The backup archive is damaged or is not a PostgreSQL custom-format archive.", BackupFailureCause.Unclassified);
        }

        if (Has("could not obtain lock") || Has("lock timeout") || Has("canceling statement due to lock"))
        {
            return new(PostgreSqlReasons.DumpLockTimeout, "The dump could not lock a table in time because another session held a conflicting lock.", BackupFailureCause.Timeout);
        }

        // Before the generic permission rule: an untrusted extension is the one privilege a CREATEDB-only verification login can never obtain.
        if (Has("permission denied to create extension") || Has("must be superuser to create this extension"))
        {
            return new(PostgreSqlReasons.ExtensionRequiresSuperuser, "An extension in the backup can only be created by a superuser, so it cannot be restored with this login.", BackupFailureCause.PermissionDenied);
        }

        if (Has("permission denied") || Has("must be superuser") || Has("must be owner") || Has("insufficient privilege") || Has("must be member of"))
        {
            return new(PostgreSqlReasons.PermissionDenied, "The login lacks a privilege the operation needs.", BackupFailureCause.PermissionDenied);
        }

        if (Has("is not available") && Has("extension") || Has("could not open extension control file") || Has("extension") && Has("does not exist"))
        {
            return new(PostgreSqlReasons.ExtensionUnavailable, "An extension the backup needs is not available on the target server.", BackupFailureCause.Unsupported);
        }

        if (Has("role \"") && Has("does not exist"))
        {
            return new(PostgreSqlReasons.RoleMissing, "A role that owns objects in the backup does not exist on the target server.", BackupFailureCause.Unclassified);
        }

        if (Has("no space left on device") || Has("disk full") || Has("could not write to output file"))
        {
            return new(PostgreSqlReasons.StagingDiskFull, "The staging volume ran out of space.", BackupFailureCause.StagingInvalid);
        }

        if (Has("database \"") && Has("does not exist"))
        {
            return new(PostgreSqlReasons.DatabaseNotFound, "The database does not exist on the server.", BackupFailureCause.DatabaseNotFound);
        }

        if (Has("could not connect to server") || Has("connection refused") || Has("could not translate host name") || Has("connection to server")
            && (Has("failed") || Has("timeout")) || Has("server closed the connection unexpectedly") || Has("terminating connection") || Has("connection timed out")
            || Has("SSL") && Has("error") || Has("no route to host") || Has("lost synchronization") || Has("could not receive data from server"))
        {
            return new(PostgreSqlReasons.ConnectionLost, "The connection to the server failed or was lost.", BackupFailureCause.DatabaseUnavailable);
        }

        return kind switch
        {
            PostgreSqlToolKind.Dump => new(PostgreSqlReasons.DumpFailed, "pg_dump could not complete the backup. Details are in the agent log.", BackupFailureCause.Unclassified),
            PostgreSqlToolKind.Verify => new(PostgreSqlReasons.VerifyFailed, "pg_restore could not read the whole archive. Details are in the agent log.", BackupFailureCause.Unclassified),
            _ => new(PostgreSqlReasons.RestoreFailed, "pg_restore could not complete the restore. Details are in the agent log.", BackupFailureCause.Unclassified)
        };
    }
}
