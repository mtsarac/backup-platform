using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.IntegrationTests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private static readonly Lazy<PostgreSqlTemplateDatabase> Template = new(() => new(GetAdministrativeConnectionString()));
    private readonly PostgreSqlTemplateDatabase? _template;

    static PostgreSqlFixture()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (!Template.IsValueCreated) return;
            try { Template.Value.Dispose(); }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"PostgreSQL test template cleanup failed ({exception.GetType().Name}).");
            }
        };
    }

    public PostgreSqlFixture() { }
    internal PostgreSqlFixture(PostgreSqlTemplateDatabase template) => _template = template;

    private readonly string _databaseName = $"backup_platform_tests_{Guid.NewGuid():N}";
    private string? _administrativeConnectionString;
    private bool _databaseCreated;
    private DbContextOptions<PlatformDbContext>? _options;
    private string? _connectionString;

    public string ConnectionString => _connectionString
        ?? throw new InvalidOperationException("The PostgreSQL fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        var template = _template ?? Template.Value;
        var templateName = await template.GetDatabaseNameAsync();
        _administrativeConnectionString = template.AdministrativeConnectionString;
        var builder = new NpgsqlConnectionStringBuilder(_administrativeConnectionString) { Database = _databaseName };
        try
        {
            await using var connection = new NpgsqlConnection(_administrativeConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\" TEMPLATE \"{templateName}\"", connection);
            await command.ExecuteNonQueryAsync();
            _databaseCreated = true;
            _connectionString = builder.ConnectionString;
            _options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ConnectionString).Options;
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public PlatformDbContext CreateContext(TenantId? tenantId) => CreateContext(new MutableTenantContext(tenantId));

    public PlatformDbContext CreateContext(ITenantContext tenantContext) => new(Options, tenantContext);

    public PlatformDbContext CreatePlatformContext() => PlatformDbContext.CreateForPlatformOperations(Options);

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_databaseCreated)
        {
            return;
        }
        await using var connection = new NpgsqlConnection(_administrativeConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
        _databaseCreated = false;
    }

    public DbContextOptions<PlatformDbContext> Options => _options
        ?? throw new InvalidOperationException("The PostgreSQL fixture has not been initialized.");

    internal static PostgreSqlTemplateDatabase SharedTemplate => Template.Value;

    internal static string GetAdministrativeConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                "Set BACKUP_PLATFORM_TEST_CONNECTION_STRING to an administrative PostgreSQL connection with CREATE DATABASE privileges before running integration tests.");
        return new NpgsqlConnectionStringBuilder(configured) { Pooling = false }.ConnectionString;
    }
}

// Migrate once per test process; every fixture still owns a separate database, including migration tests.
internal sealed class PostgreSqlTemplateDatabase : IDisposable
{
    private readonly string _databaseName = $"backup_platform_template_{Guid.NewGuid():N}";
    private readonly Lazy<Task<string>> _initialization;
    private readonly object _gate = new();
    private volatile bool _databaseCreated;
    private bool _disposed;

    internal PostgreSqlTemplateDatabase(string administrativeConnectionString)
    {
        AdministrativeConnectionString = new NpgsqlConnectionStringBuilder(administrativeConnectionString) { Pooling = false }.ConnectionString;
        _initialization = new(CreateAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal string AdministrativeConnectionString { get; }

    internal Task<string> GetDatabaseNameAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _initialization.Value;
        }
    }

    private async Task<string> CreateAsync()
    {
        try
        {
            await using (var connection = new NpgsqlConnection(AdministrativeConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", connection);
                await command.ExecuteNonQueryAsync();
                _databaseCreated = true;
            }
            var builder = new NpgsqlConnectionStringBuilder(AdministrativeConnectionString) { Database = _databaseName };
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(builder.ConnectionString).Options;
            await using (var context = PlatformDbContext.CreateForPlatformOperations(options))
                await context.Database.MigrateAsync();
            // No source sessions may remain during cloning, and no test may connect to mutate the template.
            await using (var connection = new NpgsqlConnection(AdministrativeConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"ALTER DATABASE \"{_databaseName}\" ALLOW_CONNECTIONS false", connection);
                await command.ExecuteNonQueryAsync();
            }
            return _databaseName;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_databaseCreated)
            {
                _disposed = true;
                return;
            }
            // ProcessExit cannot await; synchronous cleanup also handles a partially migrated source.
            using var connection = new NpgsqlConnection(AdministrativeConnectionString);
            connection.Open();
            using var command = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", connection);
            command.ExecuteNonQuery();
            _databaseCreated = false;
            _disposed = true;
        }
    }
}

public sealed class MutableTenantContext(TenantId? tenantId) : ITenantContext
{
    public TenantId? TenantId { get; set; } = tenantId;
}
