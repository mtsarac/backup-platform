using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

if (args.Length != 1 || args[0] is not ("postgres" or "mssql"))
{
    Console.Error.WriteLine("Usage: DatabaseProbe postgres|mssql");
    return 2;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
try
{
    await using DbConnection connection = args[0] == "postgres"
        ? new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Required("BACKUP_PLATFORM_TEST_CONNECTION_STRING"))
        {
            Pooling = false,
            Timeout = 10,
            CommandTimeout = 10
        }.ConnectionString)
        : CreateSqlConnection();
    await connection.OpenAsync(timeout.Token);
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT 1";
    command.CommandTimeout = 10;
    var result = await command.ExecuteScalarAsync(timeout.Token);
    if (Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) != 1)
    {
        Console.Error.WriteLine("Unexpected database probe result.");
        return 1;
    }
    Console.WriteLine(args[0] == "postgres"
        ? "PostgreSQL authenticated connection and SELECT 1 succeeded."
        : "MSSQL authenticated connection and SELECT 1 succeeded.");
    return 0;
}
catch (Exception exception) when (exception is DbException or ArgumentException or InvalidOperationException or OperationCanceledException)
{
    // Provider messages may contain credentials or connection strings; only report safe diagnostic codes.
    var code = exception switch
    {
        SqlException sql => $"SQL error {sql.Number}",
        PostgresException postgres => $"SQLSTATE {postgres.SqlState}",
        _ => exception.GetType().Name
    };
    Console.Error.WriteLine($"Database connection check failed ({code}); check endpoint, credentials and access.");
    return 1;
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Missing {name}");

static SqlConnection CreateSqlConnection()
{
    var host = Required("BACKUP_PLATFORM_TEST_MSSQL_HOST");
    var port = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_PORT");
    var instance = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_INSTANCE");
    if (!string.IsNullOrWhiteSpace(port) && !string.IsNullOrWhiteSpace(instance))
    {
        throw new ArgumentException("Configure port or instance, not both.");
    }
    var endpoint = !string.IsNullOrWhiteSpace(port) ? $"tcp:{host},{port}"
        : !string.IsNullOrWhiteSpace(instance) ? $"{host}\\{instance}" : host;
    return new SqlConnection(new SqlConnectionStringBuilder
    {
        DataSource = endpoint,
        InitialCatalog = "master",
        UserID = Required("BACKUP_PLATFORM_TEST_MSSQL_USER"),
        Password = Required("BACKUP_PLATFORM_TEST_MSSQL_PASSWORD"),
        Encrypt = true,
        TrustServerCertificate = string.Equals(Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_TRUST_SERVER_CERTIFICATE"), "true", StringComparison.OrdinalIgnoreCase),
        Pooling = false,
        ConnectTimeout = 10
    }.ConnectionString);
}
