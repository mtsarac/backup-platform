using System.IO;
using BackupPlatform.Contracts.Storage;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Storage;

public sealed class CapacityProbeTaskHandler(
    ISftpCapacityProbeExecutor? sftpExecutor = null,
    ILogger<CapacityProbeTaskHandler>? logger = null)
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
            string? target = null;
            if (request.Configuration.TryGetValue("volume", out var vol) && !string.IsNullOrWhiteSpace(vol))
            {
                target = vol.Trim();
            }
            else if (request.Configuration.TryGetValue("path", out var p) && !string.IsNullOrWhiteSpace(p))
            {
                target = p.Trim();
            }

            DriveInfo? drive = null;
            if (!string.IsNullOrWhiteSpace(target))
            {
                try
                {
                    drive = new DriveInfo(target);
                }
                catch
                {
                    // Fall back to matching longest mounted root
                    try
                    {
                        var full = Path.GetFullPath(target);
                        drive = DriveInfo.GetDrives()
                            .Where(d => full.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(d => d.RootDirectory.FullName.Length)
                            .FirstOrDefault();
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }

            if (drive is null)
            {
                var root = Path.GetPathRoot(Environment.CurrentDirectory) ?? "/";
                drive = new DriveInfo(root);
            }

            if (!drive.IsReady)
            {
                logger?.LogWarning("Local drive {DriveName} is not ready for capacity probe.", drive.Name);
                return CapacityProbeTaskResult.Failure(request.TaskId, request.ConnectionId, CapacityProbeFailureCodes.CapacityUnavailable, $"Drive '{drive.Name}' is not ready.");
            }

            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            var used = Math.Max(0, total - free);

            return CapacityProbeTaskResult.Success(request.TaskId, request.ConnectionId, total, used, free);
        }
        catch (Exception ex) when (ex is ArgumentException or DriveNotFoundException or IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Failed to query local capacity for connection {ConnectionId}", request.ConnectionId);
            return CapacityProbeTaskResult.Failure(request.TaskId, request.ConnectionId, CapacityProbeFailureCodes.CapacityUnavailable, ex.Message);
        }
    }
}
