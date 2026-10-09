using System.Text.Json;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.GoogleDrive;
using Microsoft.Extensions.Options;

namespace BackupPlatform.Api.Backups;

public sealed class GoogleDriveConnectionProviderFactory(
    HttpClient http,
    IOptions<GoogleDrivePlatformOptions> platformOptions,
    IGoogleDriveFileCatalog catalog,
    ISecretStore secretStore,
    DbContextOptions<PlatformDbContext> dbOptions)
{
    private readonly GoogleDrivePlatformOptions _platform = platformOptions?.Value ?? new();

    public IStorageProvider Create(StorageConnection connection, ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(secrets);

        if (connection.ProviderType != StorageProviderTypes.GoogleDrive || connection.Id == Guid.Empty || connection.TenantId.Value == Guid.Empty)
            throw InvalidConfiguration();

        try
        {
            var config = connection.Configuration;
            var folderId = config.GetValueOrDefault("folderId");
            var stagingDir = !string.IsNullOrWhiteSpace(_platform.StagingDirectory)
                ? _platform.StagingDirectory
                : Path.Combine(Path.GetTempPath(), "backup-platform-staging");

            var options = new GoogleDriveStorageOptions(
                _platform.ClientId,
                _platform.ClientSecret,
                connection.TenantId.Value,
                connection.Id,
                folderId,
                stagingDir,
                minStagingFreeBytes: _platform.MinStagingFreeBytes);

            var reference = connection.SecretReference;
            if (reference is null || !reference.StartsWith($"sc-{connection.Id:N}-", StringComparison.Ordinal))
            {
                throw new StorageException(StorageFailureKind.AuthenticationFailed, "Google Drive connection has no credentials configured.");
            }

            var secretReference = new SecretReference(reference);
            PlatformDbContext? refreshDb = null;
            StorageConnection? refreshConnection = null;

            return new GoogleDriveStorageProvider(
                http,
                options,
                token => ReadCredentialAsync(secrets, secretReference, token),
                catalog,
                async (newRefreshToken, token) =>
                {
                    var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["refreshToken"] = newRefreshToken });
                    await secretStore.SetAsync(secretReference, new SecretValue(json), token);
                },
                async (refresh, token) =>
                {
                    await using var db = PlatformDbContext.CreateForPlatformOperations(dbOptions);
                    await using var transaction = await db.Database.BeginTransactionAsync(token);
                    // Match connection-management's tenant lock, including callback/disconnect and generic secret changes.
                    await GoogleDriveOAuthService.LockOwnerAsync(db, connection.TenantId, token);
                    refreshConnection = await db.StorageConnections.SingleOrDefaultAsync(
                        row => row.TenantId == connection.TenantId && row.Id == connection.Id
                            && row.ProviderType == StorageProviderTypes.GoogleDrive, token);
                    if (refreshConnection is null || refreshConnection.SecretReference is not { } currentReference
                        || !currentReference.StartsWith($"sc-{connection.Id:N}-", StringComparison.Ordinal))
                        throw InvalidCredentials();
                    secretReference = new SecretReference(currentReference);
                    refreshDb = db;
                    try
                    {
                        return await refresh(token);
                    }
                    finally
                    {
                        refreshDb = null;
                        refreshConnection = null;
                    }
                },
                async token =>
                {
                    var config = new Dictionary<string, string>(refreshConnection!.Configuration, StringComparer.Ordinal)
                    {
                        ["reauthorizationRequired"] = "true"
                    };
                    refreshConnection.Reconfigure(config, DateTimeOffset.UtcNow);
                    await refreshDb!.SaveChangesAsync(token);
                    await refreshDb.Database.CurrentTransaction!.CommitAsync(token);
                });
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw InvalidConfiguration();
        }
    }

    private static async ValueTask<GoogleDriveCredential> ReadCredentialAsync(
        ISecretProvider secrets, SecretReference reference, CancellationToken cancellationToken)
    {
        try
        {
            var value = await secrets.ResolveAsync(reference, cancellationToken);
            var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(value.Reveal());
            if (fields is null || !fields.TryGetValue("refreshToken", out var refreshToken) || string.IsNullOrWhiteSpace(refreshToken))
            {
                throw InvalidCredentials();
            }
            return new GoogleDriveCredential(refreshToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StorageException)
        {
            throw;
        }
        catch
        {
            throw InvalidCredentials();
        }
    }

    private static StorageException InvalidConfiguration() =>
        new(StorageFailureKind.InvalidConfiguration, "The Google Drive storage connection configuration is invalid.");

    private static StorageException InvalidCredentials() =>
        new(StorageFailureKind.AuthenticationFailed, "The Google Drive credentials could not be read or are invalid.");
}
