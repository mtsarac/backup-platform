using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using BackupPlatform.Contracts.Downloads;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed partial class DownloadTaskWorker
{
    private static FileStream CreatePrivateDownloadFile()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose, BufferSize = 64 * 1024
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path.Combine(Path.GetTempPath(), $"bp-direct-{Guid.NewGuid():N}"), options);
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, long size, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, 64 * 1024), token).ConfigureAwait(false)) != 0)
            {
                total = checked(total + read);
                if (total > size) throw new BackupPlatform.StorageProviders.Abstractions.StorageException(
                    BackupPlatform.StorageProviders.Abstractions.StorageFailureKind.IntegrityMismatch, "The stored length changed.");
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task<string?> SendDirectAsync(DownloadTaskSnapshot task, FileStream file, string checksum, CancellationToken token)
    {
        var offer = await BackupTaskLease.CallAsync(identities, null, identity => client.SignalAsync(identity, task.SessionId,
            new DownloadSignalRequest { LeaseToken = task.LeaseToken }, token), token).ConfigureAwait(false);
        if (!offer.IsSuccess || offer.Value.Sdp is null) return DownloadAgentFailureCodes.DirectPathUnavailable;
        await file.FlushAsync(token).ConfigureAwait(false);
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "backup-direct-download.exe" : "backup-direct-download"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("--source"); info.ArgumentList.Add(file.Name);
        info.ArgumentList.Add("--checksum"); info.ArgumentList.Add(checksum);
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) return DownloadAgentFailureCodes.DirectPathUnavailable;
        }
        catch (System.ComponentModel.Win32Exception) { return DownloadAgentFailureCodes.DirectPathUnavailable; }
        // Drain the small fixed diagnostic without logging SDP, keys or paths.
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "offer", sdp = offer.Value.Sdp }).AsMemory(), token).ConfigureAwait(false);
            process.StandardInput.Close();
            using var negotiation = CancellationTokenSource.CreateLinkedTokenSource(token);
            negotiation.CancelAfter(TimeSpan.FromSeconds(25));
            var line = await process.StandardOutput.ReadLineAsync(negotiation.Token).ConfigureAwait(false);
            if (line is null || line.Length > 65536) return DownloadAgentFailureCodes.DirectPathUnavailable;
            using var answer = JsonDocument.Parse(line);
            var result = await BackupTaskLease.CallAsync(identities, null, identity => client.SignalAsync(identity, task.SessionId,
                new DownloadSignalRequest { LeaseToken = task.LeaseToken, Sdp = answer.RootElement.GetProperty("sdp").GetString() }, token), token).ConfigureAwait(false);
            if (!result.IsSuccess) return DownloadAgentFailureCodes.DirectPathUnavailable;
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            return process.ExitCode == 0 ? null : DownloadAgentFailureCodes.DirectPathUnavailable;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return DownloadAgentFailureCodes.DirectPathUnavailable; }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
        }
    }
}
