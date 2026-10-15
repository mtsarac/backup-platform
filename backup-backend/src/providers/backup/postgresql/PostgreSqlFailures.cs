using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql.Connectivity;
using BackupPlatform.BackupProviders.PostgreSql.Tools;

namespace BackupPlatform.BackupProviders.PostgreSql;

internal static class PostgreSqlFailures
{
    public static PostgreSqlToolFailure FromConnection(DatabaseProviderException error)
    {
        if (PostgreSqlSession.IsDatabaseMissing(error))
            return new(PostgreSqlReasons.DatabaseNotFound, "The database does not exist on this server.", BackupFailureCause.DatabaseNotFound);
        return error.Kind switch
        {
            DatabaseFailureKind.AuthenticationFailed => new(PostgreSqlReasons.AuthenticationFailed, error.Message, BackupFailureCause.AuthenticationFailed),
            DatabaseFailureKind.PermissionDenied => new(PostgreSqlReasons.PermissionDenied, error.Message, BackupFailureCause.PermissionDenied),
            DatabaseFailureKind.Timeout => new(PostgreSqlReasons.ConnectionTimeout, error.Message, BackupFailureCause.Timeout),
            DatabaseFailureKind.InvalidConfiguration => new(PostgreSqlReasons.ConnectionSettingsInvalid, error.Message, BackupFailureCause.InvalidConfiguration),
            DatabaseFailureKind.Unreachable => new(PostgreSqlReasons.ServerUnreachable, error.Message, BackupFailureCause.DatabaseUnavailable),
            _ => new(PostgreSqlReasons.MetadataUnavailable, error.Message, BackupFailureCause.Unclassified)
        };
    }

    public static BackupPreCheckItem Failed(string check, string reason, string message, BackupFailureCause cause) =>
        new(check, BackupPreCheckStatus.Failed, reason, message, cause);
    public static BackupPreCheckItem Passed(string check, string message) =>
        new(check, BackupPreCheckStatus.Passed, "verified", message);
}
