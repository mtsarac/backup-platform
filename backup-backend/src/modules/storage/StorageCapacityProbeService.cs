using System.Text.Json;
using BackupPlatform.Application.Storage;
using BackupPlatform.Contracts.Storage;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Storage;

public sealed class StorageCapacityProbeService(
    IStorageConnectionStore connectionStore,
    IStorageCapacitySnapshotStore snapshotStore,
    ISecretProvider secrets,
    ITenantAgentLiveness agentLiveness,
    IAgentCapacityProbeDispatcher agentDispatcher,
    IGoogleDriveCapacityClient? googleDriveClient = null,
    TimeProvider? timeProvider = null,
    ILogger<StorageCapacityProbeService>? logger = null,
    IStorageCapacityNotificationSink? notificationSink = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<StorageCapacityResponse?> GetCapacityAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        var snapshot = await snapshotStore.GetLatestAsync(tenantId, connectionId, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : StorageCapacityResponse.From(snapshot);
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

    private async Task<StorageCapacityResponse> ProbeAgentSideAsync(
        TenantId tenantId, StorageConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var activeAgentId = await agentLiveness.FindActiveAgentAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (activeAgentId is null)
        {
            logger?.LogInformation("No active agent found for tenant {TenantId} to probe connection {ConnectionId}.", tenantId.Value, connection.Id);
            var unavailable = StorageCapacitySnapshot.UnavailableResult(tenantId, connection.Id, now, "No active agent available to reach storage destination.");
            await SaveAndNotifyAsync(unavailable, cancellationToken).ConfigureAwait(false);
            return StorageCapacityResponse.From(unavailable);
        }

        Dictionary<string, string>? secretValues = null;
        if (connection.SecretReference is not null)
        {
            try
            {
                var secretVal = await secrets.ResolveAsync(new SecretReference(connection.SecretReference), cancellationToken).ConfigureAwait(false);
                secretValues = JsonSerializer.Deserialize<Dictionary<string, string>>(secretVal.Reveal());
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to resolve secret for connection {ConnectionId}", connection.Id);
            }
        }

        var request = new CapacityProbeTaskRequest
        {
            TaskId = Guid.NewGuid(),
            ConnectionId = connection.Id,
            ProviderType = connection.ProviderType,
            Configuration = connection.Configuration,
            Secret = secretValues
        };

        try
        {
            var result = await agentDispatcher.DispatchAsync(activeAgentId.Value, request, cancellationToken).ConfigureAwait(false);
            StorageCapacitySnapshot snapshot;
            if (result.Succeeded)
            {
                snapshot = StorageCapacitySnapshot.AvailableResult(tenantId, connection.Id, result.TotalBytes, result.UsedBytes, result.FreeBytes, now);
            }
            else
            {
                snapshot = StorageCapacitySnapshot.UnavailableResult(tenantId, connection.Id, now, result.ErrorMessage ?? result.ErrorCode);
            }
            await SaveAndNotifyAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return StorageCapacityResponse.From(snapshot);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Delegating capacity probe to agent {AgentId} failed for connection {ConnectionId}", activeAgentId, connection.Id);
            var snapshot = StorageCapacitySnapshot.UnavailableResult(tenantId, connection.Id, now, ex.Message);
            await SaveAndNotifyAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return StorageCapacityResponse.From(snapshot);
        }
    }


    private async Task SaveAndNotifyAsync(StorageCapacitySnapshot snapshot, CancellationToken cancellationToken)
    {
        await snapshotStore.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (notificationSink is not null)
        {
            try
            {
                await notificationSink.NotifyCapacityEvaluatedAsync(snapshot.TenantId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to notify notification sink for tenant {TenantId} after saving capacity snapshot.", snapshot.TenantId);
            }
        }
    }
}
