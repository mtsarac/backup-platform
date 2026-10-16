using System.Globalization;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql.Connectivity;

namespace BackupPlatform.BackupProviders.PostgreSql.Discovery;

internal sealed record PostgreSqlServerInfo(int VersionNumber, string Version, string? HostPlatform, bool DataChecksums)
{
    public int Major => VersionNumber / 10000;
}

internal sealed record PostgreSqlDatabaseInfo(
    string Name,
    bool IsTemplate,
    bool AllowsConnections,
    bool CanConnect,
    string Encoding,
    string? Collation,
    string? CType,
    long? SizeBytes,
    string Owner)
{
    public bool IsSystem => IsTemplate || PostgreSqlIdentifiers.IsProtectedDatabase(Name);
}

/// <summary>The privileges of the connected login, as they matter to a dump or a restore.</summary>
internal sealed record PostgreSqlRoleInfo(string Name, bool IsSuperuser, bool CanCreateDatabase, bool BypassesRowSecurity, bool CanSignalBackends);

internal static class PostgreSqlDiscovery
{
    /// <summary>The oldest server major this provider dumps and restores. PostgreSQL 11 and older are out of community support.</summary>
    public const int MinimumServerMajor = 12;

    public static async Task<PostgreSqlServerInfo> ReadServerAsync(PostgreSqlSession session, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(
            "SELECT current_setting('server_version_num')::int, version(), current_setting('data_checksums')", cancellationToken).ConfigureAwait(false);
        var row = rows[0];
        var number = Convert.ToInt32(row[0], CultureInfo.InvariantCulture);
        var banner = Convert.ToString(row[1], CultureInfo.InvariantCulture) ?? string.Empty;
        var platform = ParsePlatform(banner);
        return new PostgreSqlServerInfo(number, FormatVersion(number), platform, string.Equals(Convert.ToString(row[2], CultureInfo.InvariantCulture), "on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>180006 is 18.6; 90624 (before 10) is 9.6.24. Pure.</summary>
    internal static string FormatVersion(int versionNumber) => versionNumber >= 100000
        ? string.Create(CultureInfo.InvariantCulture, $"{versionNumber / 10000}.{versionNumber % 10000}")
        : string.Create(CultureInfo.InvariantCulture, $"{versionNumber / 10000}.{versionNumber / 100 % 100}.{versionNumber % 100}");

    /// <summary>The platform triple of <c>version()</c>: "PostgreSQL 18.6 ... on x86_64-pc-linux-gnu, compiled by ..." gives <c>x86_64-pc-linux-gnu</c>. Pure.</summary>
    internal static string? ParsePlatform(string banner)
    {
        const string marker = " on ";
        var start = banner.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = banner.IndexOfAny([',', ' '], start);
        var value = end < 0 ? banner[start..] : banner[start..end];
        return value.Length is > 0 and <= 128 ? value : null;
    }

    public static async Task<IReadOnlyList<PostgreSqlDatabaseInfo>> ListDatabasesAsync(PostgreSqlSession session, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(DatabaseQuery + " ORDER BY d.datname", cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToDatabase)];
    }

    /// <summary>The database of exactly this name (case-sensitive, as PostgreSQL compares), or null.</summary>
    public static async Task<PostgreSqlDatabaseInfo?> FindDatabaseAsync(PostgreSqlSession session, string name, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(DatabaseQuery + " WHERE d.datname = @name", cancellationToken, ("name", name)).ConfigureAwait(false);
        return rows.Count == 0 ? null : ToDatabase(rows[0]);
    }

    public static async Task<PostgreSqlRoleInfo> ReadRoleAsync(PostgreSqlSession session, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(
            """
            SELECT r.rolname, r.rolsuper, r.rolcreatedb, r.rolbypassrls,
                   r.rolsuper OR pg_has_role(r.oid, 'pg_signal_backend', 'MEMBER')
            FROM pg_roles r WHERE r.rolname = current_user
            """, cancellationToken).ConfigureAwait(false);
        var row = rows[0];
        return new PostgreSqlRoleInfo(Convert.ToString(row[0], CultureInfo.InvariantCulture)!, (bool)row[1]!, (bool)row[2]!, (bool)row[3]!, (bool)row[4]!);
    }

    private const string DatabaseQuery =
        """
        SELECT d.datname, d.datistemplate, d.datallowconn, has_database_privilege(d.oid, 'CONNECT'),
               pg_encoding_to_char(d.encoding), d.datcollate, d.datctype,
               CASE WHEN has_database_privilege(d.oid, 'CONNECT') THEN pg_database_size(d.oid) END,
               pg_get_userbyid(d.datdba)
        FROM pg_database d
        """;

    private static PostgreSqlDatabaseInfo ToDatabase(object?[] row) => new(
        Convert.ToString(row[0], CultureInfo.InvariantCulture)!,
        (bool)row[1]!,
        (bool)row[2]!,
        (bool)row[3]!,
        Convert.ToString(row[4], CultureInfo.InvariantCulture)!,
        Convert.ToString(row[5], CultureInfo.InvariantCulture),
        Convert.ToString(row[6], CultureInfo.InvariantCulture),
        row[7] is null ? null : Convert.ToInt64(row[7], CultureInfo.InvariantCulture),
        Convert.ToString(row[8], CultureInfo.InvariantCulture)!);
}
