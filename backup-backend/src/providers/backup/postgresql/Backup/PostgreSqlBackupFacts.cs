using System.Globalization;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql.Connectivity;
using BackupPlatform.BackupProviders.PostgreSql.Discovery;

namespace BackupPlatform.BackupProviders.PostgreSql.Backup;

/// <summary>What the server says about the database about to be dumped, read through a connection to that very database.</summary>
internal sealed record PostgreSqlBackupFacts(
    PostgreSqlServerInfo Server,
    PostgreSqlDatabaseInfo Database,
    PostgreSqlRoleInfo Role,
    IReadOnlyList<string> Extensions,
    int TableCount,
    int UnreadableTables,
    int RowSecurityBlockedTables,
    bool? LargeObjectsReadable,
    bool HasForeignTables,
    bool HasSubscriptions,
    bool IsStandby)
{
    public static async Task<PostgreSqlBackupFacts> ReadAsync(PostgreSqlSession session, string database, CancellationToken cancellationToken)
    {
        var server = await PostgreSqlDiscovery.ReadServerAsync(session, cancellationToken).ConfigureAwait(false);
        var info = await PostgreSqlDiscovery.FindDatabaseAsync(session, database, cancellationToken).ConfigureAwait(false)
            ?? throw new DatabaseProviderException(DatabaseFailureKind.InvalidConfiguration, "The database does not exist on this server.");
        var role = await PostgreSqlDiscovery.ReadRoleAsync(session, cancellationToken).ConfigureAwait(false);

        var extensions = (await session.QueryAsync("SELECT extname FROM pg_extension ORDER BY extname", cancellationToken).ConfigureAwait(false))
            .Select(row => Convert.ToString(row[0], CultureInfo.InvariantCulture)!).ToArray();

        // Everything a dump reads: ordinary data tables (rows are COPY'd), and every sequence/materialized view the archive carries. Superusers read it all.
        var tables = Convert.ToInt32(await session.ScalarAsync(UserRelations + " AND c.relkind = 'r'", cancellationToken, []).ConfigureAwait(false), CultureInfo.InvariantCulture);
        var unreadable = role.IsSuperuser ? 0 : Convert.ToInt32(await session.ScalarAsync(
            UserRelations + """
             AND ((c.relkind IN ('r', 'p', 'm') AND NOT has_table_privilege(c.oid, 'SELECT'))
               OR (c.relkind = 'S' AND NOT has_sequence_privilege(c.oid, 'SELECT')))
            """, cancellationToken, []).ConfigureAwait(false), CultureInfo.InvariantCulture);

        // pg_dump turns row_security off; a table whose policies would then filter rows makes the dump fail instead of silently losing rows.
        var rowSecurity = role.IsSuperuser || role.BypassesRowSecurity ? 0 : Convert.ToInt32(await session.ScalarAsync(
            UserRelations + " AND c.relkind IN ('r', 'p') AND c.relrowsecurity AND (c.relforcerowsecurity OR NOT pg_has_role(c.relowner, 'MEMBER'))",
            cancellationToken, []).ConfigureAwait(false), CultureInfo.InvariantCulture);

        var external = await session.QueryAsync("""
            SELECT EXISTS(SELECT 1 FROM pg_class WHERE relkind = 'f'),
                   EXISTS(SELECT 1 FROM pg_subscription WHERE subdbid = (SELECT oid FROM pg_database WHERE datname = current_database())),
                   pg_is_in_recovery()
            """, cancellationToken).ConfigureAwait(false);
        return new PostgreSqlBackupFacts(server, info, role, extensions, tables, unreadable, rowSecurity,
            await ProbeLargeObjectsAsync(session, role, cancellationToken).ConfigureAwait(false),
            (bool)external[0][0]!, (bool)external[0][1]!, (bool)external[0][2]!);
    }

    /// <summary>
    /// null when the server cannot tell (older servers have no <c>has_largeobject_privilege</c>, and <c>lo_compat_privileges</c> turns the check off).
    /// A dump of a large object the login may not read fails mid-run, so a clearly unreadable one is worth knowing up front.
    /// </summary>
    private static async Task<bool?> ProbeLargeObjectsAsync(PostgreSqlSession session, PostgreSqlRoleInfo role, CancellationToken cancellationToken)
    {
        if (role.IsSuperuser)
        {
            return true;
        }

        try
        {
            var blocked = await session.ScalarAsync(
                "SELECT count(*) FROM pg_largeobject_metadata m WHERE NOT has_largeobject_privilege(m.oid, 'SELECT')", cancellationToken, []).ConfigureAwait(false);
            return Convert.ToInt64(blocked, CultureInfo.InvariantCulture) == 0;
        }
        catch (DatabaseProviderException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UndefinedFunction })
        {
            return null;
        }
    }

    private const string UserRelations =
        """
        SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname !~ '^pg_(toast|temp)'
        """;
}
