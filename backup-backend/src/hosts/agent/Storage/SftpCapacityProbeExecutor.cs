using System.Globalization;
using System.Text.RegularExpressions;
using BackupPlatform.Contracts.Storage;
using BackupPlatform.StorageProviders.Sftp;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace BackupPlatform.Agent.Host.Storage;

public sealed partial class SftpCapacityProbeExecutor(SftpNetworkOptions network, ILogger<SftpCapacityProbeExecutor>? logger = null) : ISftpCapacityProbeExecutor
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
            long total, free;
            try
            {
                var info = await client.GetStatusAsync(path, cancellationToken).ConfigureAwait(false);
                var blockSize = checked((long)(info.FileSystemBlockSize > 0 ? info.FileSystemBlockSize : info.BlockSize));
                total = checked((long)info.TotalBlocks * blockSize);
                free = checked((long)info.AvailableBlocks * blockSize);
            }
            catch (Exception statvfsFailure) when (statvfsFailure is not OperationCanceledException)
            {
                // The statvfs@openssh.com extension is missing or refused (non-OpenSSH servers); ask the server's df over an exec channel instead.
                logger?.LogInformation("SFTP statvfs failed for connection {ConnectionId} ({ExceptionType}); falling back to df.", request.ConnectionId, statvfsFailure.GetType().Name);
                (total, free) = await DfAsync(client.ConnectionInfo, fingerprint, path, cancellationToken).ConfigureAwait(false);
            }
            ValidateCapacity(total, free);
            return CapacityProbeTaskResult.Success(request.TaskId, request.ConnectionId, total, total - free, free);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger?.LogWarning("SFTP capacity probe failed for connection {ConnectionId} ({ExceptionType}).", request.ConnectionId, ex.GetType().Name);
            return CapacityProbeTaskResult.Failure(request.TaskId, request.ConnectionId, CapacityProbeFailureCodes.CapacityUnavailable);
        }
    }

    private async Task<(long Total, long Free)> DfAsync(ConnectionInfo connection, string fingerprint, string path, CancellationToken cancellationToken)
    {
        if (path.Any(char.IsControl)) throw new IOException("The remote directory cannot be queried.");
        using var ssh = new SshClient(connection);
        ssh.HostKeyReceived += (_, e) => e.CanTrust = SftpConfiguration.MatchesHostKey(fingerprint, e.HostKey);
        await ssh.ConnectAsync(cancellationToken).ConfigureAwait(false);
        using var command = ssh.CreateCommand($"df -Pk -- '{path.Replace("'", "'\\''")}'");
        command.CommandTimeout = network.OperationTimeout;
        await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        if (command.ExitStatus != 0) throw new IOException("The remote df command failed.");
        return ParseDf(command.Result);
    }

    /// <summary>Parses POSIX <c>df -Pk</c> output (1024-byte blocks): the last line is <c>filesystem total used available capacity mount</c>.</summary>
    internal static (long Total, long Free) ParseDf(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        var match = line is null ? null : DfLine().Match(line);
        if (match is not { Success: true } || !long.TryParse(match.Groups["total"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var blocks)
            || !long.TryParse(match.Groups["available"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var available))
            throw new IOException("The remote df output is not understood.");
        return (checked(blocks * 1024), checked(available * 1024));
    }

    private static void ValidateCapacity(long total, long free)
    {
        if (total <= 0 || free < 0 || free > total) throw new IOException("Invalid filesystem capacity.");
    }

    [GeneratedRegex(@"^\S.*?\s+(?<total>\d+)\s+\d+\s+(?<available>\d+)\s+\d+%\s+\S.*$")]
    private static partial Regex DfLine();
}
