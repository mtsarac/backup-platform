using Microsoft.Data.SqlClient;

namespace BackupPlatform.IntegrationTests.Backup;

/// <summary>One real, uniquely named source database; never changes existing SQL configuration or user databases.</summary>
internal sealed class OrchestrationSqlSource : IAsyncDisposable
{
    private readonly string _connectionString;
    private bool _created;
    public string DatabaseName { get; } = "bp_orchestration_" + Guid.NewGuid().ToString("N");
    public string Host { get; } = AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_MSSQL_HOST");
    public int? Port { get; } = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_PORT") is { Length: > 0 } value
        ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : null;
    private readonly int _rows;

    /// <param name="rows">Number of ~8 KB incompressible rows: more rows make the real backup take longer.</param>
    public OrchestrationSqlSource(int rows = 12000)
    {
        _rows = rows;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Port is { } port ? $"tcp:{Host},{port}" : Host,
            UserID = AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_MSSQL_USER"),
            Password = AgentHostSmokeProcess.Required("BACKUP_PLATFORM_TEST_MSSQL_PASSWORD"),
            InitialCatalog = "master", Encrypt = true, TrustServerCertificate = true, Pooling = false
        };
        _connectionString = builder.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await ExecuteAsync($"CREATE DATABASE [{DatabaseName}]");
        _created = true;
        await ExecuteAsync($"""
            ALTER DATABASE [{DatabaseName}] SET RECOVERY SIMPLE;
            USE [{DatabaseName}];
            CREATE TABLE dbo.source_rows (id int IDENTITY PRIMARY KEY, payload varbinary(8000) NOT NULL);
            INSERT dbo.source_rows(payload) SELECT TOP ({_rows}) CRYPT_GEN_RANDOM(8000)
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """);
    }

    public async Task<int> FullBackupCountAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM msdb.dbo.backupset WHERE database_name=@database AND type='D'";
        command.Parameters.AddWithValue("@database", DatabaseName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 180;
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_created)
        {
            await ExecuteAsync($"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];");
            _created = false;
        }
    }
}
