using System.Globalization;
using BackupPlatform.Contracts.Storage;
using BackupPlatform.StorageProviders.Sftp;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Host.Storage;

public sealed class SftpCapacityProbeExecutor(SftpNetworkOptions network, ILogger<SftpCapacityProbeExecutor>? logger = null) : ISftpCapacityProbeExecutor
{
    public async Task<CapacityProbeTaskResult> ProbeAsync(CapacityProbeTaskRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestCancellation = cancellationToken;

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
            var options = new SftpStorageOptions(host, port, username, authType, remoteDir, fingerprint, request.TenantId, request.ConnectionId);
            SftpConfiguration.Validate(options, network);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(network.ConnectionTimeout + network.OperationTimeout);
            cancellationToken = lifetime.Token;
            var manager = new SftpSessionManager(options, _ => ValueTask.FromResult(new SftpCredential(password, privateKey, passphrase)), network);

            using var client = await manager.CreateConnectedClientAsync(cancellationToken).ConfigureAwait(false);
            var path = string.IsNullOrWhiteSpace(remoteDir) ? "/" : remoteDir;
            var info = await client.GetStatusAsync(path, cancellationToken).ConfigureAwait(false);

            var blockSize = checked((long)(info.FileSystemBlockSize > 0 ? info.FileSystemBlockSize : info.BlockSize));
            var total = checked((long)info.TotalBlocks * blockSize);
            var free = checked((long)info.AvailableBlocks * blockSize);
            if (total <= 0 || free < 0 || free > total) throw new IOException("Invalid filesystem capacity.");
            var used = total - free;

            return CapacityProbeTaskResult.Success(request.TaskId, request.ConnectionId, total, used, free);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger?.LogWarning("SFTP capacity probe failed for connection {ConnectionId} ({ExceptionType}).", request.ConnectionId, ex.GetType().Name);
            return CapacityProbeTaskResult.Failure(request.TaskId, request.ConnectionId, CapacityProbeFailureCodes.CapacityUnavailable);
        }
    }
}
