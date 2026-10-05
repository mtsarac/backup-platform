using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Discovery;

namespace BackupPlatform.BackupProviders.MSSQL.PreChecks;

/// <summary>Everything an extension needs to add checks: what was requested and what was discovered. Read-only.</summary>
public sealed record SqlServerPreCheckContext(
    FullBackupPreCheckRequest Request,
    SqlServerInstanceInfo Instance,
    SqlServerDatabaseInfo Database);

/// <summary>
/// Extension point for checks that go beyond metadata. It runs only once the server was reached, the login accepted and the
/// database found. The first planned implementation is the SQL-Server-service-account write check for the staging path
/// (<c>staging.path-server-writable</c>): the file is created by the database service account, not by the agent process, so it
/// must be probed from the server's side and must never be confused with an agent-side file system check.
/// </summary>
public interface ISqlServerPreCheckExtension
{
    Task<IReadOnlyList<BackupPreCheckItem>> CheckAsync(SqlServerPreCheckContext context, CancellationToken cancellationToken);
}
