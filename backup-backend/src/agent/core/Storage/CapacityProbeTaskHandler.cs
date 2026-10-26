using System.IO;
using BackupPlatform.Contracts.Storage;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Storage;

public sealed class CapacityProbeTaskHandler(
    ISftpCapacityProbeExecutor? sftpExecutor = null,
    ILogger<CapacityProbeTaskHandler>? logger = null,
    IReadOnlyDictionary<string, string>? localVolumeDirectories = null)
{
    public async Task<CapacityProbeTaskResult> ExecuteAsync(CapacityProbeTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.Equals(request.ProviderType, "local", StringComparison.OrdinalIgnoreCase))
        {
            return ProbeLocal(request);
        }

        if (string.Equals(request.ProviderType, "sftp", StringComparison.OrdinalIgnoreCase))
        {
            if (sftpExecutor is null)
            {
                logger?.LogWarning("Capacity probe: SFTP executor is not configured on this agent.");
                return CapacityProbeTaskResult.Failure(request.TaskId, request.ConnectionId, CapacityProbeFailureCodes.CapacityUnavailable, "SFTP probe executor is not available.");
            }
            return await sftpExecutor.ProbeAsync(request, cancellationToken).ConfigureAwait(false);
        }

        logger?.LogWarning("Capacity probe: unsupported provider type {ProviderType}", request.ProviderType);
        return CapacityProbeTaskResult.Failure(request.TaskId, request.ConnectionId, CapacityProbeFailureCodes.InvalidConfiguration, $"Unsupported provider type for capacity probe: '{request.ProviderType}'.");
    }

    private CapacityProbeTaskResult ProbeLocal(CapacityProbeTaskRequest request)
    {
        try
        {
            var volume = request.Configuration.GetValueOrDefault("volume") ?? "local";
            var target = localVolumeDirectories?.GetValueOrDefault(volume);
            if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target))
                return CapacityProbeTaskResult.Failure(request.TaskId, request.ConnectionId, CapacityProbeFailureCodes.InvalidConfiguration);
            var (total, used, free) = BackupPlatform.StorageProviders.Abstractions.FileSystem.FileSystemCapacity.Query(target);
            return CapacityProbeTaskResult.Success(request.TaskId, request.ConnectionId, total, used, free);
        }
        catch (DirectoryNotFoundException)
        {
            return CapacityProbeTaskResult.Failure(request.TaskId, request.ConnectionId, CapacityProbeFailureCodes.TargetNotFound);
        }
        catch (Exception ex) when (ex is ArgumentException or DriveNotFoundException or IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Failed to query local capacity for connection {ConnectionId}", request.ConnectionId);
            return CapacityProbeTaskResult.Failure(request.TaskId, request.ConnectionId, CapacityProbeFailureCodes.CapacityUnavailable);
        }
    }
}
