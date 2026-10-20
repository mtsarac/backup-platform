using System.Globalization;
using BackupPlatform.Contracts.Storage;
using BackupPlatform.StorageProviders.Sftp;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Host.Storage;

public sealed class SftpCapacityProbeExecutor(ILogger<SftpCapacityProbeExecutor>? logger = null) : ISftpCapacityProbeExecutor
{
    public async Task<CapacityProbeTaskResult> ProbeAsync(CapacityProbeTaskRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Configuration.TryGetValue("host", out var host) || string.IsNullOrWhiteSpace(host))
        {
            return CapacityProbeTaskResult.Failure(request.TaskId, request.ConnectionId, CapacityProbeFailureCodes.InvalidConfiguration, "Missing SFTP host.");
        }

        var port = 22;
        if (request.Configuration.TryGetValue("port", out var portStr) && int.TryParse(portStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
        {
            port = parsedPort;
        }

        var username = request.Configuration.GetValueOrDefault("username") ?? string.Empty;
        var authType = request.Configuration.GetValueOrDefault("authenticationType") ?? "password";
        var remoteDir = request.Configuration.GetValueOrDefault("remoteBaseDirectory") ?? "/";
        var fingerprint = request.Configuration.GetValueOrDefault("hostKeyFingerprint") ?? string.Empty;

        var password = request.Secret?.GetValueOrDefault("password");
        var privateKey = request.Secret?.GetValueOrDefault("privateKey");
        var passphrase = request.Secret?.GetValueOrDefault("passphrase");

        try
        {
            var options = new SftpStorageOptions(host, port, username, authType, remoteDir, fingerprint, Guid.Empty, request.ConnectionId);
            var network = new SftpNetworkOptions();
            var manager = new SftpSessionManager(options, _ => ValueTask.FromResult(new SftpCredential(password, privateKey, passphrase)), network);

            using var client = await manager.CreateConnectedClientAsync(cancellationToken).ConfigureAwait(false);
            var path = string.IsNullOrWhiteSpace(remoteDir) ? "/" : remoteDir;
            var info = await client.GetStatusAsync(path, cancellationToken).ConfigureAwait(false);

            var blockSize = info.BlockSize > 0 ? (long)info.BlockSize : (long)info.FileSystemBlockSize;
            var total = (long)info.TotalBlocks * blockSize;
            var free = (long)info.AvailableBlocks * blockSize;
            var used = Math.Max(0, total - free);

            return CapacityProbeTaskResult.Success(request.TaskId, request.ConnectionId, total, used, free);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "SFTP capacity probe failed for connection {ConnectionId}", request.ConnectionId);
            return CapacityProbeTaskResult.Failure(request.TaskId, request.ConnectionId, CapacityProbeFailureCodes.CapacityUnavailable, ex.Message);
        }
    }
}
