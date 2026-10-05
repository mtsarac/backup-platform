using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using Microsoft.Data.SqlClient;

namespace BackupPlatform.IntegrationTests.MsSql;

/// <summary>
/// A real SQL Server. Configuration comes from environment variables and is mandatory: with no server configured the fixture
/// throws, so these tests fail loudly instead of passing or skipping silently (the same rule as the PostgreSQL fixture).
/// The administrative login needs sysadmin; the fixture creates uniquely named databases and logins and drops them afterwards.
/// </summary>
public sealed class MsSqlFixture : IAsyncLifetime
{
    private const string ConfigurationHint =
        "Set BACKUP_PLATFORM_TEST_MSSQL_HOST, BACKUP_PLATFORM_TEST_MSSQL_USER and BACKUP_PLATFORM_TEST_MSSQL_PASSWORD (a sysadmin SQL login) "
        + "before running MSSQL integration tests. Optional: BACKUP_PLATFORM_TEST_MSSQL_PORT or BACKUP_PLATFORM_TEST_MSSQL_INSTANCE (not both), "
        + "BACKUP_PLATFORM_TEST_MSSQL_TRUST_SERVER_CERTIFICATE=true for self-signed servers.";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10];
    private readonly List<string> _databases = [];
    private readonly List<string> _logins = [];
    private DatabaseConnectionSettings? _settings;
    private PasswordDatabaseCredential? _admin;

    public DatabaseConnectionSettings Settings => _settings ?? throw NotInitialized();
    public PasswordDatabaseCredential Admin => _admin ?? throw NotInitialized();

    /// <summary>ONLINE, FULL recovery, compatibility level 130.</summary>
    public string OnlineFull => $"bp_it_full_{_suffix}";
    public string OnlineSimple => $"bp_it_simple_{_suffix}";
    public string OnlineBulkLogged => $"bp_it_bulk_{_suffix}";
    public string Offline => $"bp_it_offline_{_suffix}";
    public string Emergency => $"bp_it_emergency_{_suffix}";
    public string SingleUser => $"bp_it_single_{_suffix}";

    public SqlLogin Operator { get; private set; } = null!;
    public SqlLogin GrantedOnly { get; private set; } = null!;
    public SqlLogin ReaderOnly { get; private set; } = null!;
    public SqlLogin Denied { get; private set; } = null!;
    public SqlLogin NoUser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var host = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_HOST");
        var user = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_USER");
        var password = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_PASSWORD");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(ConfigurationHint);
        }

        var portText = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_PORT");
        var instance = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_INSTANCE");
        var trust = string.Equals(Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_TRUST_SERVER_CERTIFICATE"), "true", StringComparison.OrdinalIgnoreCase);

        _settings = new DatabaseConnectionSettings(
            new DatabaseEndpoint(
                host,
                string.IsNullOrWhiteSpace(portText) ? null : int.Parse(portText, System.Globalization.CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(instance) ? null : instance),
            new DatabaseConnectionOptions(trustServerCertificate: trust));
        _admin = new PasswordDatabaseCredential(user, new DatabaseSecret(password));

        try
        {
            await CreateDatabasesAsync();
            await CreateLoginsAsync();
        }
        catch (Exception initializationFailure)
        {
            try
            {
                await DisposeAsync();
            }
            catch (AggregateException cleanupFailure)
            {
                throw new AggregateException("MSSQL fixture initialization failed and its cleanup did not complete.", initializationFailure, cleanupFailure);
            }

            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_settings is null)
        {
            return;
        }

        var failures = new List<Exception>();
        foreach (var database in _databases)
        {
            // An OFFLINE or EMERGENCY database keeps its files when dropped, so it is brought back online first. Without this the
            // fixture leaked .mdf/.ldf files on the server (observed on SQL Server 2025). SINGLE_USER removes other sessions' locks.
            await TryExecuteAsync(
                $"IF DB_ID(N'{database}') IS NOT NULL BEGIN "
                + $"ALTER DATABASE [{database}] SET ONLINE; "
                + $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
                + $"DROP DATABASE [{database}]; END",
                failures);
        }

        foreach (var login in _logins)
        {
            await TryExecuteAsync($"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}]", failures);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("MSSQL fixture cleanup left objects on the server; remove them manually (names start with bp_it_).", failures);
        }
    }

    public PasswordDatabaseCredential Credential(SqlLogin login) => new(login.Name, new DatabaseSecret(login.Password));

    /// <summary>Runs a statement as the administrative login, outside the provider, to arrange or independently verify server state.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<object?[]>> QueryAsync(string sql)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
            {
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(row);
        }

        return rows;
    }

    private async Task CreateDatabasesAsync()
    {
        await CreateDatabaseAsync(OnlineFull, "FULL");
        await CreateDatabaseAsync(OnlineSimple, "SIMPLE");
        await CreateDatabaseAsync(OnlineBulkLogged, "BULK_LOGGED");
        await CreateDatabaseAsync(Offline, "FULL");
        await CreateDatabaseAsync(Emergency, "FULL");
        await CreateDatabaseAsync(SingleUser, "FULL");

        await ExecuteAsync($"ALTER DATABASE [{Offline}] SET OFFLINE WITH ROLLBACK IMMEDIATE");
        await ExecuteAsync($"ALTER DATABASE [{Emergency}] SET EMERGENCY");
        await ExecuteAsync($"ALTER DATABASE [{SingleUser}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
    }

    private async Task CreateDatabaseAsync(string name, string recoveryModel)
    {
        _databases.Add(name);
        await ExecuteAsync($"CREATE DATABASE [{name}]");
        await ExecuteAsync($"ALTER DATABASE [{name}] SET RECOVERY {recoveryModel}");
        await ExecuteAsync($"ALTER DATABASE [{name}] SET COMPATIBILITY_LEVEL = 130");
    }

    private async Task CreateLoginsAsync()
    {
        Operator = await CreateLoginAsync("operator", "ALTER ROLE db_backupoperator ADD MEMBER [{0}]");
        GrantedOnly = await CreateLoginAsync("granted", "GRANT BACKUP DATABASE TO [{0}]");
        ReaderOnly = await CreateLoginAsync("reader", "ALTER ROLE db_datareader ADD MEMBER [{0}]");
        Denied = await CreateLoginAsync("denied", "ALTER ROLE db_backupoperator ADD MEMBER [{0}]; DENY BACKUP DATABASE TO [{0}]");
        NoUser = await CreateLoginAsync("nouser", null);
    }

    // The generated password mixes character classes and contains no quote, so it is safe inside the literal below.
    private async Task<SqlLogin> CreateLoginAsync(string role, string? databaseStatement)
    {
        var name = $"bp_it_{role}_{_suffix}";
        var password = $"Aa1!{Guid.NewGuid():N}";
        _logins.Add(name);
        await ExecuteAsync($"CREATE LOGIN [{name}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF");
        if (databaseStatement is not null)
        {
            var statement = string.Format(System.Globalization.CultureInfo.InvariantCulture, databaseStatement, name);
            await ExecuteAsync($"EXEC [{OnlineFull}].sys.sp_executesql N'CREATE USER [{name}] FOR LOGIN [{name}]'");
            await ExecuteAsync($"EXEC [{OnlineFull}].sys.sp_executesql N'{statement}'");
        }

        return new SqlLogin(name, password);
    }

    private async Task<SqlConnection> OpenAdminAsync()
    {
        var connection = new SqlConnection(SqlServerConnectionStringFactory.Create(Settings, Admin).ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task TryExecuteAsync(string sql, List<Exception> failures)
    {
        try
        {
            await ExecuteAsync(sql);
        }
        catch (SqlException exception)
        {
            // Cleanup continues so one stuck object does not leak the rest, but the failure is reported when the fixture is disposed.
            failures.Add(new InvalidOperationException($"MSSQL fixture cleanup statement failed: {sql}", exception));
        }
    }

    private static InvalidOperationException NotInitialized() => new("The MSSQL fixture has not been initialized.");
}

public sealed record SqlLogin(string Name, string Password)
{
    public override string ToString() => Name;
}
