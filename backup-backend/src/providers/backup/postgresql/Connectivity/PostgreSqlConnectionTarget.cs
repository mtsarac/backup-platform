using BackupPlatform.BackupProviders.Abstractions.Database;
using Npgsql;

namespace BackupPlatform.BackupProviders.PostgreSql.Connectivity;

/// <summary>
/// Everything needed to reach one PostgreSQL server, validated once. Used both for Npgsql sessions and for the client tools, so the two always
/// agree on host, port, identity and transport security. The password lives only here and in the environment of a tool process (never on a
/// command line, where any local user could read it) and is never rendered.
/// </summary>
internal sealed class PostgreSqlConnectionTarget
{
    public const int DefaultPort = 5432;

    private readonly string _password;

    private PostgreSqlConnectionTarget(string host, int port, string userName, string password, SslMode sslMode, int connectTimeoutSeconds, int commandTimeoutSeconds)
    {
        Host = host;
        Port = port;
        UserName = userName;
        _password = password;
        SslMode = sslMode;
        ConnectTimeoutSeconds = connectTimeoutSeconds;
        CommandTimeoutSeconds = commandTimeoutSeconds;
    }

    public string Host { get; }
    public int Port { get; }
    public string UserName { get; }
    public SslMode SslMode { get; }
    public int ConnectTimeoutSeconds { get; }
    public int CommandTimeoutSeconds { get; }

    /// <exception cref="DatabaseProviderException">The settings cannot form a PostgreSQL connection (named instance, integrated security, no password).</exception>
    public static PostgreSqlConnectionTarget Create(DatabaseConnectionSettings settings, DatabaseCredential credential)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credential);
        if (settings.Endpoint.InstanceName is not null)
        {
            throw new DatabaseProviderException(DatabaseFailureKind.InvalidConfiguration, "PostgreSQL has no named instances; use a host and a port.");
        }

        if (credential is not PasswordDatabaseCredential password)
        {
            throw new DatabaseProviderException(DatabaseFailureKind.InvalidConfiguration, "PostgreSQL requires a user name and password credential.");
        }

        var options = settings.Options;
        // Encrypt without trust verifies the certificate chain and the host name; trust accepts any certificate; no encryption is opportunistic.
        var ssl = options.RequireEncryption ? (options.TrustServerCertificate ? SslMode.Require : SslMode.VerifyFull) : SslMode.Prefer;
        return new PostgreSqlConnectionTarget(settings.Endpoint.Host, settings.Endpoint.Port ?? DefaultPort, password.UserName, password.Password.Reveal(), ssl,
            (int)options.ConnectTimeout.TotalSeconds, (int)options.CommandTimeout.TotalSeconds);
    }

    public NpgsqlConnectionStringBuilder ToNpgsql(string database) => new()
    {
        Host = Host,
        Port = Port,
        Database = database,
        Username = UserName,
        Password = _password,
        SslMode = SslMode,
        Timeout = ConnectTimeoutSeconds,
        CommandTimeout = CommandTimeoutSeconds,
        // One short-lived dedicated connection per operation: a pooled connection could outlive the statement it is cancelled by.
        Pooling = false,
        ApplicationName = "backup-platform-agent",
        IncludeErrorDetail = false
    };

    /// <summary>
    /// A libpq connection string for pg_dump/pg_restore <c>--dbname</c>. The database name is quoted inside it, so a name containing
    /// <c>=</c>, spaces, quotes or a URI prefix can never be read as another connection parameter. It carries no password.
    /// </summary>
    public string ToConninfo(string database) => string.Join(' ',
        $"host={QuoteConninfo(Host)}", $"port={Port}", $"dbname={QuoteConninfo(database)}", $"user={QuoteConninfo(UserName)}",
        $"sslmode={SslMode switch { SslMode.VerifyFull => "verify-full", SslMode.Require => "require", _ => "prefer" }}",
        $"connect_timeout={ConnectTimeoutSeconds}", "application_name=backup-platform-agent");

    /// <summary>The secret as an environment value for one tool process (<c>PGPASSWORD</c>).</summary>
    public string Password => _password;

    internal static string QuoteConninfo(string value)
    {
        if (value.Contains('\0'))
        {
            throw new DatabaseProviderException(DatabaseFailureKind.InvalidConfiguration, "A connection value contains an invalid character.");
        }

        return "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    }

    public override string ToString() => $"{UserName}@{Host}:{Port}";
}
