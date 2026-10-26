using System.Buffers;
using System.Security.Cryptography;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Encryption;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Net.Http.Headers;

namespace BackupPlatform.Api.Downloads;

/// <summary>One bounded buffer and awaited writes; a leased hold survives backpressure and is released on every terminal outcome.</summary>
public sealed class DownloadStreamer(IDownloadStore store, DownloadService service, TimeProvider time, ILogger<DownloadStreamer> logger, AgentDownloadSpool spool, ITenantWrapKeyStore? keys = null)
{
    private const int BufferSize = 64 * 1024;

    public async Task StreamAsync(HttpContext context, DownloadSession session)
    {
        var owner = Guid.NewGuid().ToString("D");
        var releaseSession = session;
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
        using var expiry = new CancellationTokenSource();
        if (claimed.BreakGlassRequestId is not null)
        {
            var remaining = claimed.ExpiresAt - time.GetUtcNow();
            expiry.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, expiry.Token);
        using var renewStop = new CancellationTokenSource();
        var renewal = RenewAsync(session.Id, owner, () => Interlocked.Read(ref transferred), transfer, renewStop.Token);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var (source, activeSession) = await service.OpenReadWithFallbackAsync(claimed, transfer.Token);
            await using (source)
            {
                session = activeSession;
                context.Response.ContentType = "application/octet-stream";
                if (!session.Decrypted) context.Response.ContentLength = session.SizeBytes; // a decrypted stream's plaintext size is not known up front
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = session.Filename, FileNameStar = session.Filename }.ToString();
                if (session.Decrypted)
                {
                    await DecryptToResponseAsync(context, session, source, buffer, transfer.Token);
                    Interlocked.Exchange(ref transferred, session.SizeBytes);
                }
                else
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long readBytes = 0;
                    while (readBytes < session.SizeBytes)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferSize, session.SizeBytes - readBytes)), transfer.Token);
                        if (read == 0) throw IntegrityFailure();
                        hash.AppendData(buffer, 0, read);
                        readBytes += read;
                        var writeLength = readBytes == session.SizeBytes ? read - 1 : read;
                        if (writeLength > 0)
                        {
                            await context.Response.Body.WriteAsync(buffer.AsMemory(0, writeLength), transfer.Token);
                            Interlocked.Add(ref transferred, writeLength);
                        }
                        if (readBytes == session.SizeBytes) buffer[0] = buffer[read - 1];
                    }

                    // Withhold the final byte until EOF and SHA-256 agree, so corrupt data cannot satisfy Content-Length.
                    if (await source.ReadAsync(buffer.AsMemory(1, 1), transfer.Token) != 0 ||
                        !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), session.Checksum, StringComparison.Ordinal)) throw IntegrityFailure();
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, 1), transfer.Token);
                    Interlocked.Increment(ref transferred);
                }
                await context.Response.Body.FlushAsync(transfer.Token);
                await StopRenewalAsync(renewStop, renewal);
                var completed = await store.TransitionAsync(session.Id, new DownloadTransition(DownloadState.Streaming, DownloadState.Completed, owner, null,
                    time.GetUtcNow(), Bytes: transferred), transfer.Token);
                if (completed is null) throw new DownloadUnavailableException("download_interrupted");
            }
        }
        catch (Exception exception)
        {
            await StopRenewalAsync(renewStop, renewal);
            var cancelled = context.RequestAborted.IsCancellationRequested;
            var code = cancelled ? "download_cancelled" : exception switch
            {
                DownloadUnavailableException unavailable => unavailable.Code,
                BpeFormatException => "integrity_mismatch",
                TenantWrapKeyUnavailableException => "key_unavailable",
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
            try { await service.ReleaseAsync(releaseSession, CancellationToken.None); }
            catch (Exception exception) { logger.LogError("Download {SessionId} temporary stream cleanup failed with {ExceptionType}", session.Id, exception.GetType().Name); }
        }
    }

    /// <summary>Verify the full stored copy before unwrapping, then verify private plaintext staging before exposing any plaintext response byte.</summary>
    private async Task DecryptToResponseAsync(HttpContext context, DownloadSession session, Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var artifact = await store.GetArtifactAsync(session.TenantId, session.ArtifactId, cancellationToken) ?? throw new DownloadUnavailableException("artifact_not_found");
        if (artifact is not { Format: BackupArtifact.BpeFormat, EncryptionKeyId: { } keyId, EncryptionWrappedDekBase64: { } wrapped }) throw new DownloadUnavailableException("artifact_not_encrypted");
        await using var ciphertext = spool.CreateFile();
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            long length = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                if (read == 0) break;
                length += read;
                if (length > session.SizeBytes) throw IntegrityFailure();
                hash.AppendData(buffer, 0, read);
                await ciphertext.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (length != session.SizeBytes || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), session.Checksum, StringComparison.Ordinal))
                throw IntegrityFailure();
        }
        ciphertext.Position = 0;
        await using var plaintext = spool.CreateFile();
        await BpeDecryptor.DecryptAsync(ciphertext, plaintext,
            (_, token) => (keys ?? throw new TenantWrapKeyUnavailableException()).UnwrapDekAsync(session.TenantId, "download", session.Id, keyId, Convert.FromBase64String(wrapped), token), cancellationToken);
        plaintext.Position = 0;
        var checksum = Convert.ToHexStringLower(await SHA256.HashDataAsync(plaintext, cancellationToken));
        if (!string.Equals(checksum, artifact.SourceChecksum, StringComparison.Ordinal)) throw IntegrityFailure();
        plaintext.Position = 0;
        // Revalidate the hold after staging, before the first plaintext byte; renewal also fences long staging and backpressured responses.
        if (await store.TransitionAsync(session.Id, new DownloadTransition(DownloadState.Streaming, DownloadState.Streaming, session.LeaseOwner, session.LeaseOwner,
            time.GetUtcNow(), DownloadService.LeaseDuration), cancellationToken) is null) throw new DownloadUnavailableException("download_interrupted");
        await plaintext.CopyToAsync(context.Response.Body, BufferSize, cancellationToken);
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

    private static StorageException IntegrityFailure() => new(StorageFailureKind.IntegrityMismatch, "The stored backup failed integrity verification.");
}
