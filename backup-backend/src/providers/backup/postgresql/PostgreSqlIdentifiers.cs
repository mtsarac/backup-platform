using System.Text;

namespace BackupPlatform.BackupProviders.PostgreSql;

/// <summary>Identifier rules the provider relies on whenever a database name reaches a statement or a name the server will store.</summary>
internal static class PostgreSqlIdentifiers
{
    /// <summary>PostgreSQL's identifier limit in bytes (NAMEDATALEN - 1). A longer name is silently truncated by the server, which could aim a statement at a different database.</summary>
    public const int MaxBytes = 63;

    public static bool IsValidDatabaseName(string? name) =>
        !string.IsNullOrEmpty(name) && name == name.Trim() && !name.Any(char.IsControl) && Encoding.UTF8.GetByteCount(name) <= MaxBytes;

    /// <summary>A double-quoted SQL identifier. Only used for names that passed <see cref="IsValidDatabaseName"/> or were generated here; DDL cannot take parameters.</summary>
    public static string Quote(string name)
    {
        if (name.Contains('\0'))
        {
            throw new ArgumentException("An identifier cannot contain NUL.", nameof(name));
        }

        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>The maintenance databases and templates: never a restore target, whatever the user's role may do.</summary>
    public static bool IsProtectedDatabase(string name) =>
        name.Equals("postgres", StringComparison.OrdinalIgnoreCase) || name.Equals("template0", StringComparison.OrdinalIgnoreCase)
        || name.Equals("template1", StringComparison.OrdinalIgnoreCase);

    /// <summary>A name for a scratch database: generated, short, and recognisable as the platform's own.</summary>
    public static string ScratchDatabaseName() => $"bp_restore_{Guid.NewGuid():N}"[..27];
}
