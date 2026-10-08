using System.Buffers;
using System.Security.Cryptography;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Net.Http.Headers;

namespace BackupPlatform.Api.Downloads;

/// <summary>One bounded buffer and awaited writes; a leased hold survives backpressure and is released on every terminal outcome.</summary>
public sealed class DownloadStreamer(IDownloadStore store, DownloadService service, TimeProvider time, ILogger<DownloadStreamer> logger)
{
    private const int BufferSize = 64 * 1024;

    public async Task StreamAsync(HttpContext context, DownloadSession session)
    {
        var owner = Guid.NewGuid().ToString("D");
        DownloadSession? claimed;
        try
        {
            claimed = await store.TransitionAsync(session.Id, new DownloadTransition(DownloadState.Ready, DownloadState.Streaming, null, owner,
                time.GetUtcNow(), DownloadService.LeaseDuration), context.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Download {SessionId} stream authorization audit failed with {ExceptionType}; no bytes transferred", session.Id, exception.GetType().Name);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "download_unavailable" }, context.RequestAborted);
            return;
        }
        if (claimed is null)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = "download_unavailable" }, context.RequestAborted);
            return;
        }
        long transferred = 0;
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using var renewStop = new CancellationTokenSource();
        var renewal = RenewAsync(session.Id, owner, () => Interlocked.Read(ref transferred), transfer, renewStop.Token);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var source = await service.OpenReadAsync(claimed, transfer.Token);
            // The same open file is verified before headers; a second running hash catches a mutation during transfer as well.
            if (source.CanSeek)
            {
                await VerifyAsync(source, session, buffer, transfer.Token);
                source.Position = 0;
            }
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = session.SizeBytes;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = session.Filename, FileNameStar = session.Filename }.ToString();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), transfer.Token);
                if (read == 0) break;
                if (Interlocked.Read(ref transferred) + read > session.SizeBytes) throw IntegrityFailure();
                hash.AppendData(buffer, 0, read);
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), transfer.Token);
                Interlocked.Add(ref transferred, read);
            }
            if (transferred != session.SizeBytes || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), session.Checksum, StringComparison.Ordinal)) throw IntegrityFailure();
            await context.Response.Body.FlushAsync(transfer.Token);
            // Stop renewal first so a progress update cannot race completion's final byte count.
            await StopRenewalAsync(renewStop, renewal);
            var completed = await store.TransitionAsync(session.Id, new DownloadTransition(DownloadState.Streaming, DownloadState.Completed, owner, null,
                time.GetUtcNow(), Bytes: transferred), transfer.Token);
            if (completed is null) throw new DownloadUnavailableException("download_interrupted");
        }
        catch (Exception exception)
        {
            await StopRenewalAsync(renewStop, renewal);
            var cancelled = context.RequestAborted.IsCancellationRequested;
            var code = cancelled ? "download_cancelled" : exception switch
            {
                DownloadUnavailableException unavailable => unavailable.Code,
                StorageException { Kind: StorageFailureKind.IntegrityMismatch } => "integrity_mismatch",
                StorageException { Kind: StorageFailureKind.NotFound } => "replica_unavailable",
                _ when transfer.IsCancellationRequested => "download_interrupted",
                _ => "transfer_failed"
            };
            logger.LogWarning("Download {SessionId} ended with {FailureCode} ({ExceptionType}) after {Bytes} bytes", session.Id, code, exception.GetType().Name, transferred);
            try
            {
                await store.TransitionAsync(session.Id, new DownloadTransition(DownloadState.Streaming, cancelled ? DownloadState.Cancelled : DownloadState.Failed,
                    owner, null, time.GetUtcNow(), Bytes: transferred, FailureCode: code), CancellationToken.None);
            }
            catch (Exception auditException)
            {
                logger.LogError("Download {SessionId} terminal audit failed with {ExceptionType}; expiring durable hold remains authoritative", session.Id, auditException.GetType().Name);
            }
            if (context.Response.HasStarted) context.Abort();
            else
            {
                context.Response.Headers.Clear();
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = code }, CancellationToken.None);
            }
        }
        finally
        {
            renewStop.Cancel();
            ArrayPool<byte>.Shared.Return(buffer);
            try { await service.ReleaseAsync(session, CancellationToken.None); }
            catch (Exception exception) { logger.LogError("Download {SessionId} temporary stream cleanup failed with {ExceptionType}", session.Id, exception.GetType().Name); }
        }
    }

    private async Task RenewAsync(Guid sessionId, string owner, Func<long> transferred, CancellationTokenSource transfer, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), time);
            while (await timer.WaitForNextTickAsync(stop))
            {
                var renewed = await store.TransitionAsync(sessionId, new DownloadTransition(DownloadState.Streaming, DownloadState.Streaming, owner, owner,
                    time.GetUtcNow(), DownloadService.LeaseDuration, transferred()), stop);
                if (renewed is not null) continue;
                transfer.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError("Download {SessionId} lease renewal failed with {ExceptionType}", sessionId, exception.GetType().Name);
            transfer.Cancel();
        }
    }

    private static async Task StopRenewalAsync(CancellationTokenSource stop, Task renewal)
    {
        stop.Cancel();
        await renewal;
    }

    private static async Task VerifyAsync(Stream source, DownloadSession session, byte[] buffer, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
            if (read == 0) break;
            length += read;
            if (length > session.SizeBytes) throw IntegrityFailure();
            hash.AppendData(buffer, 0, read);
        }
        if (length != session.SizeBytes || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), session.Checksum, StringComparison.Ordinal)) throw IntegrityFailure();
    }

    private static StorageException IntegrityFailure() => new(StorageFailureKind.IntegrityMismatch, "The stored backup failed integrity verification.");
}
