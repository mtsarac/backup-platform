using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.IntegrationTests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly string _databaseName = $"backup_platform_tests_{Guid.NewGuid():N}";
    private string? _administrativeConnectionString;
    private bool _databaseCreated;
    private DbContextOptions<PlatformDbContext>? _options;
    private string? _connectionString;

    public string ConnectionString => _connectionString
        ?? throw new InvalidOperationException("The PostgreSQL fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Set BACKUP_PLATFORM_TEST_CONNECTION_STRING to an administrative PostgreSQL connection with CREATE DATABASE privileges before running integration tests.");
        }
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        _administrativeConnectionString = builder.ConnectionString;
        await using var connection = new NpgsqlConnection(_administrativeConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", connection);
        await command.ExecuteNonQueryAsync();
        _databaseCreated = true;
        builder.Database = _databaseName;
        _connectionString = builder.ConnectionString;
        _options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ConnectionString).Options;
        try
        {
            await using var context = CreatePlatformContext();
            await context.Database.MigrateAsync();
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

    private DbContextOptions<PlatformDbContext> Options => _options
        ?? throw new InvalidOperationException("The PostgreSQL fixture has not been initialized.");
}

public sealed class MutableTenantContext(TenantId? tenantId) : ITenantContext
{
    public TenantId? TenantId { get; set; } = tenantId;
}
