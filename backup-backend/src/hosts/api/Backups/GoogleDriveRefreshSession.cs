using System.Buffers.Binary;
using System.Security.Cryptography;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.Api.Backups;

internal sealed class GoogleDriveRefreshSession(NpgsqlConnection connection, PlatformDbContext database) : IAsyncDisposable
{
    public PlatformDbContext Database { get; } = database;

    public static async Task<GoogleDriveRefreshSession> AcquireAsync(
        DbContextOptions<PlatformDbContext> options, TenantId tenantId, Guid connectionId,
        TimeSpan waitTimeout, CancellationToken cancellationToken)
    {
        await using var configuration = PlatformDbContext.CreateForPlatformOperations(options);
        // Session locks must never return to a pool or be moved between backends by multiplexing.
        var settings = new NpgsqlConnectionStringBuilder(configuration.Database.GetConnectionString())
        {
            Pooling = false,
            Multiplexing = false,
            ApplicationName = "BackupPlatform.GoogleDrive.Refresh"
        };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(waitTimeout);
        try
        {
            await connection.OpenAsync(timeout.Token);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", ComputeKey(tenantId.Value, connectionId));
            while (!Equals(await command.ExecuteScalarAsync(timeout.Token), true))
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            var database = PlatformDbContext.CreateForPlatformOperations(
                new DbContextOptionsBuilder<PlatformDbContext>(options).UseNpgsql(connection).Options);
            return new GoogleDriveRefreshSession(connection, database);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await connection.DisposeAsync();
            throw new StorageException(StorageFailureKind.NetworkUnavailable, "Google Drive credential coordination timed out.");
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static long ComputeKey(Guid tenantId, Guid connectionId)
    {
        Span<byte> identity = stackalloc byte[33];
        identity[0] = 0x47;
        tenantId.TryWriteBytes(identity[1..17]);
        connectionId.TryWriteBytes(identity[17..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(identity, hash);
        return BinaryPrimitives.ReadInt64LittleEndian(hash);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Database.DisposeAsync();
        }
        finally
        {
            // Closing this nonpooled session releases the lock even after a failed transaction.
            await connection.DisposeAsync();
        }
    }
}
