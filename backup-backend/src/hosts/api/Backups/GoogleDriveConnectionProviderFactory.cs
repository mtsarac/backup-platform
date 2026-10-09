using System.Text.Json;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.GoogleDrive;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackupPlatform.Api.Backups;

public sealed class GoogleDriveConnectionProviderFactory(
    HttpClient http,
    IOptions<GoogleDrivePlatformOptions> platformOptions,
    IGoogleDriveFileCatalog catalog,
    ISecretStore secretStore,
    DbContextOptions<PlatformDbContext> dbOptions,
    IGoogleDriveStagingStore? stagingStore = null,
    ILogger<GoogleDriveConnectionProviderFactory>? logger = null)
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
                minStagingFreeBytes: _platform.MinStagingFreeBytes,
                maxRetryAttempts: _platform.RetryMaxAttempts,
                maxRetryElapsed: _platform.RetryBudget,
                requestTimeout: _platform.RequestTimeout,
                uploadTimeout: _platform.UploadTimeout,
                maxSessionRestarts: _platform.MaxSessionRestarts);

            var reference = connection.SecretReference;
            if (reference is null || !reference.StartsWith($"sc-{connection.Id:N}-", StringComparison.Ordinal))
            {
                throw new StorageException(StorageFailureKind.AuthenticationFailed, "Google Drive connection has no credentials configured.");
            }

            CredentialSnapshot? snapshot = null;
            PlatformDbContext? refreshDb = null;

            return new GoogleDriveStorageProvider(
                http,
                options,
                _ => ValueTask.FromResult(new GoogleDriveCredential(snapshot!.RefreshToken)),
                catalog,
                async (newRefreshToken, _) =>
                {
                    snapshot = await PersistRotationAsync(refreshDb!, connection, snapshot!, newRefreshToken, secrets, options.RequestTimeout);
                },
                async (refresh, token) =>
                {
                    try
                    {
                        await using var session = await GoogleDriveRefreshSession.AcquireAsync(
                            dbOptions, connection.TenantId, connection.Id, options.MaxRetryElapsed, token);
                        refreshDb = session.Database;
                        snapshot = await ReadSnapshotAsync(refreshDb, connection, secrets, options.RequestTimeout, token);
                        var accessToken = await refresh(token);
                        await CheckSnapshotAsync(refreshDb, connection, snapshot, secrets, options.RequestTimeout, token);
                        if (connection.SecretReference != snapshot.Reference)
                            connection.AttachSecret(snapshot.Reference, ["refreshToken"], snapshot.UpdatedAt ?? DateTimeOffset.UtcNow);
                        return accessToken;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (StorageException exception)
                    {
                        logger?.LogWarning("Google Drive credential refresh failed: {Kind}.", exception.Kind);
                        throw;
                    }
                    catch
                    {
                        logger?.LogWarning("Google Drive credential refresh failed: {Kind}.", "CoordinationUnavailable");
                        throw;
                    }
                    finally
                    {
                        refreshDb = null;
                        snapshot = null;
                    }
                },
                async _ =>
                {
                    using var timeout = new CancellationTokenSource(options.RequestTimeout);
                    refreshDb!.ChangeTracker.Clear();
                    await using var transaction = await refreshDb.Database.BeginTransactionAsync(timeout.Token);
                    await GoogleDriveOAuthService.LockOwnerAsync(refreshDb, connection.TenantId, timeout.Token);
                    var current = await FindConnectionAsync(refreshDb, connection, timeout.Token);
                    if (await MatchesAsync(current, snapshot!, secrets, timeout.Token))
                    {
                        current!.Reconfigure(new Dictionary<string, string>(current.Configuration, StringComparer.Ordinal)
                        {
                            ["reauthorizationRequired"] = "true"
                        }, DateTimeOffset.UtcNow);
                        await refreshDb.SaveChangesAsync(timeout.Token);
                        await transaction.CommitAsync(timeout.Token);
                    }
                    else
                        logger?.LogWarning("Google Drive reliability finding: {Kind}", "RevocationFenceChanged");
                },
                reconciliationObserver: logger is null ? null : finding => logger.LogWarning("Google Drive reliability finding: {Kind}", finding.Kind),
                stagingStore: stagingStore);
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

    private async Task<CredentialSnapshot> PersistRotationAsync(
        PlatformDbContext db, StorageConnection connection, CredentialSnapshot expected, string newRefreshToken,
        ISecretProvider secrets, TimeSpan operationTimeout)
    {
        var reference = $"sc-{connection.Id:N}-{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(operationTimeout);
        try
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["refreshToken"] = newRefreshToken });
            await secretStore.SetAsync(new SecretReference(reference), new SecretValue(json), timeout.Token);
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
            await GoogleDriveOAuthService.LockOwnerAsync(db, connection.TenantId, timeout.Token);
            var current = await FindConnectionAsync(db, connection, timeout.Token);
            if (!await MatchesAsync(current, expected, secrets, timeout.Token))
            {
                logger?.LogWarning("Google Drive reliability finding: {Kind}", "RotationFenceChanged");
                throw InvalidCredentials();
            }
            // A new immutable reference fences generic secret changes and reauthorization on every node.
            current!.AttachSecret(reference, ["refreshToken"], DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            await db.SaveChangesAsync(timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            await GoogleDriveOAuthService.DiscardSecretAsync(dbOptions, secretStore, connection.TenantId, expected.Reference, operationTimeout);
            return Snapshot(current, newRefreshToken);
        }
        catch
        {
            // A failed commit may have succeeded: cleanup checks the committed reference before deleting.
            await GoogleDriveOAuthService.DiscardSecretAsync(dbOptions, secretStore, connection.TenantId, reference, operationTimeout);
            throw;
        }
    }

    private static async Task<CredentialSnapshot> ReadSnapshotAsync(
        PlatformDbContext db, StorageConnection connection, ISecretProvider secrets, TimeSpan operationTimeout, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(operationTimeout);
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
        await GoogleDriveOAuthService.LockOwnerAsync(db, connection.TenantId, timeout.Token);
        var current = await FindConnectionAsync(db, connection, timeout.Token);
        if (current?.SecretReference is not { } reference
            || !reference.StartsWith($"sc-{connection.Id:N}-", StringComparison.Ordinal)
            || current.Configuration.GetValueOrDefault("reauthorizationRequired") == "true")
            throw InvalidCredentials();
        var credential = await ReadCredentialAsync(secrets, new SecretReference(reference), timeout.Token);
        await transaction.CommitAsync(timeout.Token);
        return Snapshot(current, credential.RefreshToken);
    }

    private static async Task CheckSnapshotAsync(
        PlatformDbContext db, StorageConnection connection, CredentialSnapshot snapshot, ISecretProvider secrets,
        TimeSpan operationTimeout, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(operationTimeout);
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
        await GoogleDriveOAuthService.LockOwnerAsync(db, connection.TenantId, timeout.Token);
        if (!await MatchesAsync(await FindConnectionAsync(db, connection, timeout.Token), snapshot, secrets, timeout.Token))
            throw InvalidCredentials();
        await transaction.CommitAsync(timeout.Token);
    }

    private static Task<StorageConnection?> FindConnectionAsync(PlatformDbContext db, StorageConnection connection, CancellationToken cancellationToken) =>
        db.StorageConnections.SingleOrDefaultAsync(row => row.TenantId == connection.TenantId && row.Id == connection.Id
            && row.ProviderType == StorageProviderTypes.GoogleDrive, cancellationToken);

    private static async Task<bool> MatchesAsync(
        StorageConnection? connection, CredentialSnapshot expected, ISecretProvider secrets, CancellationToken cancellationToken)
    {
        if (connection is null || connection.SecretReference != expected.Reference || connection.SecretUpdatedAt != expected.UpdatedAt
            || connection.Configuration.GetValueOrDefault("reauthorizationRequired") != expected.ReauthorizationRequired)
            return false;
        var current = await ReadCredentialAsync(secrets, new SecretReference(expected.Reference), cancellationToken);
        return string.Equals(current.RefreshToken, expected.RefreshToken, StringComparison.Ordinal);
    }

    private static CredentialSnapshot Snapshot(StorageConnection connection, string refreshToken) =>
        new(connection.SecretReference!, connection.SecretUpdatedAt, connection.Configuration.GetValueOrDefault("reauthorizationRequired"), refreshToken);

    private sealed record CredentialSnapshot(string Reference, DateTimeOffset? UpdatedAt, string? ReauthorizationRequired, string RefreshToken);

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
