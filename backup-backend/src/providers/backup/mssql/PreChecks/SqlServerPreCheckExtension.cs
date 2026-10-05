using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Discovery;

namespace BackupPlatform.BackupProviders.MSSQL.PreChecks;

/// <summary>Everything an extension needs to add checks: what was requested and what was discovered. Read-only.</summary>
public sealed record SqlServerPreCheckContext(
    FullBackupPreCheckRequest Request,
    SqlServerInstanceInfo Instance,
    SqlServerDatabaseInfo Database);

/// <summary>
/// Extension point for checks that go beyond what the provider itself verifies. It runs only once the server was reached, the
/// login accepted and the database found, after the built-in checks (which already include the staging checks:
/// <c>staging.agent-access</c>, <c>staging.server-writable</c>, <c>staging.mapping</c>, <c>staging.disk-space</c>). An extension
/// adds items; it cannot change or remove built-in ones. A check about the database service account must be probed from the
/// server's side and must never be confused with an agent-side file system check.
/// </summary>
public interface ISqlServerPreCheckExtension
{
    Task<IReadOnlyList<BackupPreCheckItem>> CheckAsync(SqlServerPreCheckContext context, CancellationToken cancellationToken);
}
