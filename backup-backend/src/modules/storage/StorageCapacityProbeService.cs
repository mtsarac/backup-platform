using BackupPlatform.Application.Storage;
using BackupPlatform.Contracts.Storage;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Storage;

public sealed class StorageCapacityProbeService(
    IStorageConnectionStore connectionStore,
    IStorageCapacitySnapshotStore snapshotStore,
    IStorageCapacityTaskQueue taskQueue,
    IGoogleDriveCapacityClient? googleDriveClient = null,
    TimeProvider? timeProvider = null,
    ILogger<StorageCapacityProbeService>? logger = null,
    IPlatformStorageCapacityClient? platformStorage = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<StorageCapacityResponse?> GetCapacityAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        var pending = await taskQueue.GetPendingAsync(tenantId, connectionId, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        var snapshots = await snapshotStore.GetHistoryAsync(tenantId, connectionId, StorageCapacityResponse.MaximumHistory, cancellationToken).ConfigureAwait(false);
        var response = pending ?? (snapshots.Count == 0 ? null : StorageCapacityResponse.From(snapshots[0]));
        return response is null ? null : response with
        {
            History = snapshots.Select(s => new StorageCapacityHistoryEntry(s.ReportedAt, s.TotalBytes, s.UsedBytes, s.FreeBytes,
                s.Status == StorageCapacityStatus.CapacityUnavailable ? s.Status : StorageCapacityStatus.Evaluate(s.TotalBytes, s.UsedBytes, s.FreeBytes))).ToArray()
        };
    }

    public async Task<StorageCapacityResponse?> ProbeAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        var record = await connectionStore.FindAsync(connectionId, tenantId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var connection = record.Connection;
        var now = _time.GetUtcNow();

        if (string.Equals(connection.ProviderType, StorageProviderTypes.GoogleDrive, StringComparison.OrdinalIgnoreCase))
        {
            return await ProbeGoogleDriveAsync(tenantId, connection, now, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(connection.ProviderType, StorageProviderTypes.Central, StringComparison.OrdinalIgnoreCase))
        {
            StorageCapacitySnapshot snapshot;
            try
            {
                if (platformStorage is null) throw new InvalidOperationException("Platform storage capacity is not configured.");
                var (total, used, free) = await platformStorage.QueryCapacityAsync(connection, cancellationToken).ConfigureAwait(false);
                snapshot = StorageCapacitySnapshot.AvailableResult(tenantId, connection.Id, total, used, free, now);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                snapshot = StorageCapacitySnapshot.UnavailableResult(tenantId, connection.Id, now, "Platform storage capacity is unavailable.");
            }
            await SaveAndNotifyAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return StorageCapacityResponse.From(snapshot);
        }

        if (string.Equals(connection.ProviderType, StorageProviderTypes.Local, StringComparison.OrdinalIgnoreCase)
            || string.Equals(connection.ProviderType, StorageProviderTypes.Sftp, StringComparison.OrdinalIgnoreCase))
        {
            return await ProbeAgentSideAsync(tenantId, connection, now, cancellationToken).ConfigureAwait(false);
        }

        var unsupportedSnapshot = StorageCapacitySnapshot.UnavailableResult(tenantId, connectionId, now, $"Unsupported provider type: {connection.ProviderType}");
        await SaveAndNotifyAsync(unsupportedSnapshot, cancellationToken).ConfigureAwait(false);
        return StorageCapacityResponse.From(unsupportedSnapshot);
    }

    private async Task<StorageCapacityResponse> ProbeGoogleDriveAsync(
        TenantId tenantId, StorageConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (googleDriveClient is null)
        {
            logger?.LogWarning("Google Drive capacity client is not configured.");
            var unavailable = StorageCapacitySnapshot.UnavailableResult(tenantId, connection.Id, now, "Google Drive capacity query is not configured on this host.");
            await SaveAndNotifyAsync(unavailable, cancellationToken).ConfigureAwait(false);
            return StorageCapacityResponse.From(unavailable);
        }

        try
        {
            var (total, used, free) = await googleDriveClient.QueryCapacityAsync(connection, cancellationToken).ConfigureAwait(false);
            var snapshot = StorageCapacitySnapshot.AvailableResult(tenantId, connection.Id, total, used, free, now);
            await SaveAndNotifyAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return StorageCapacityResponse.From(snapshot);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Direct Google Drive capacity query failed for connection {ConnectionId}", connection.Id);
            var snapshot = StorageCapacitySnapshot.UnavailableResult(tenantId, connection.Id, now, ex.Message);
            await SaveAndNotifyAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return StorageCapacityResponse.From(snapshot);
        }
    }

    private Task<StorageCapacityResponse> ProbeAgentSideAsync(
        TenantId tenantId, StorageConnection connection, DateTimeOffset now, CancellationToken cancellationToken) =>
        taskQueue.EnqueueAsync(tenantId, connection.Id, now, cancellationToken);


    private Task SaveAndNotifyAsync(StorageCapacitySnapshot snapshot, CancellationToken cancellationToken) =>
        snapshotStore.SaveAsync(snapshot, cancellationToken);
}
