using BackupPlatform.Domain.Storage;
using System.Buffers;
using System.Security.Cryptography;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Application.Downloads;

public sealed record ReplicaDownloadAvailability(Guid ReplicaId, string StorageProvider, string StorageTarget, bool Available, string? Reason);
public sealed record DownloadAvailability(bool Available, string? Reason, IReadOnlyList<ReplicaDownloadAvailability> Replicas, string Checksum, long SizeBytes, string? Engine = DatabaseEngines.MsSql,
    string Format = BackupArtifact.PlainFormat);
/// <param name="Decrypt">Tenant administrators only: the serving agent verifies ciphertext and plaintext before direct delivery.</param>
public sealed record PrepareDownloadRequest(Guid? ReplicaId = null, bool Decrypt = false);
/// <param name="Checksum">What the client can verify: the plaintext SHA-256 for a decrypted download, otherwise the stored file's. <paramref name="SizeBytes"/> is always the stored size.</param>
public sealed record PreparedDownload(Guid SessionId, string DownloadUrl, string Filename, string Checksum, long SizeBytes, bool Decrypted = false, bool Direct = false);
public sealed record DownloadStatus(Guid SessionId, string State, string? FailureCode, long TransferredBytes);
public sealed class DownloadUnavailableException(string code) : Exception("The backup cannot be downloaded.") { public string Code { get; } = code; }

public sealed class DownloadService(IDownloadStore store, IBackupStorageResolver storage, IAgentDownloadSource agentSource,
    TimeProvider time, ILogger<DownloadService> logger, ITenantWrapKeyStore? wrapKeys = null)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);

    public async Task<DownloadAvailability?> GetAvailabilityAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken)
    {
        var artifact = await store.GetArtifactAsync(tenantId, artifactId, cancellationToken);
        if (artifact is null) return null;
        var replicas = new List<ReplicaDownloadAvailability>(artifact.Replicas.Count);
        foreach (var replica in artifact.Replicas)
        {
            var reason = await GetReasonAsync(artifact, replica, cancellationToken);
            replicas.Add(new ReplicaDownloadAvailability(replica.Id, replica.StorageProvider, replica.StorageTarget, reason is null, reason));
        }
        var available = replicas.Any(replica => replica.Available);
        return new DownloadAvailability(available, available ? null : replicas.FirstOrDefault()?.Reason ?? "replica_unavailable", replicas, artifact.ArtifactChecksum, artifact.SizeBytes,
            BackupArtifactEngine.Read(artifact.SourceMetadataJson), artifact.Format);
    }

    /// <summary>
    /// Starts a download. A support session can only ever download the stored bytes; a break-glass request always downloads decrypted; a tenant administrator may ask for either.
    /// </summary>
    /// <exception cref="BackupPlatform.Domain.Support.SupportAccessDeniedException">A support session was given and it can no longer authorize the download.</exception>
    /// <exception cref="BreakGlassDeniedException">A break-glass request was given and it cannot be used (not approved, expired, already spent, not the requester).</exception>
    public async Task<PreparedDownload?> PrepareAsync(TenantId tenantId, Guid artifactId, Guid userId, PrepareDownloadRequest request, CancellationToken cancellationToken,
        Guid? supportSessionId = null, Guid? breakGlassRequestId = null)
    {
        var decrypt = request.Decrypt || breakGlassRequestId is not null;
        if (decrypt && supportSessionId is not null) throw new DownloadUnavailableException("decrypt_not_permitted");
        var availability = await GetAvailabilityAsync(tenantId, artifactId, cancellationToken);
        if (availability is null) return null;
        BackupArtifact? artifact = null;
        if (decrypt)
        {
            if (availability.Format != BackupArtifact.BpeFormat) throw new DownloadUnavailableException("artifact_not_encrypted");
            artifact = await store.GetArtifactAsync(tenantId, artifactId, cancellationToken);
            if (artifact is null) return null;
            if (wrapKeys is null || !await wrapKeys.OwnsKeyAsync(tenantId, artifact.EncryptionKeyId!, artifact.EncryptionKeyVersion!.Value, cancellationToken))
                throw new DownloadUnavailableException("key_unavailable");
        }
        var selected = request.ReplicaId is { } requested
            ? availability.Replicas.SingleOrDefault(replica => replica.ReplicaId == requested)
            : availability.Replicas
                .Where(replica => replica.Available)
                .OrderBy(replica => ReplicaRepairSourceEvaluator.GetProviderPriority(replica.StorageProvider))
                .FirstOrDefault();
        if (selected is null) throw new DownloadUnavailableException(request.ReplicaId is null ? availability.Reason! : "replica_not_found");
        if (!selected.Available) throw new DownloadUnavailableException(selected.Reason!);
        artifact ??= await store.GetArtifactAsync(tenantId, artifactId, cancellationToken);
        if (artifact is null) return null;
        var replica = artifact.Replicas.Single(item => item.Id == selected.ReplicaId);
        var external = StorageProviderTypes.AgentExecuted(replica.StorageProvider);
        var directAgent = external ? await agentSource.SelectAgentAsync(tenantId, replica, cancellationToken) : null;
        if (external && directAgent is null) throw new DownloadUnavailableException("DirectPathUnavailable");
        var extension = availability.Engine switch { DatabaseEngines.PostgreSql => "dump", DatabaseEngines.MsSql => "bak", DatabaseEngines.FileSystem => "zip", _ => "bin" };
        var filename = $"backup-{artifactId:N}.{extension}" + (availability.Format == BackupArtifact.BpeFormat && !decrypt ? ".bpe" : string.Empty);
        var session = await store.CreateAsync(new DownloadCreateCommand(tenantId, artifactId, selected.ReplicaId, userId, filename, time.GetUtcNow(), SessionLifetime, supportSessionId,
            decrypt, breakGlassRequestId, directAgent), cancellationToken);
        if (session is null) throw new DownloadUnavailableException("replica_unavailable");
        var url = breakGlassRequestId is { } breakGlass
            ? $"/api/platform/artifact-break-glass/{breakGlass}/downloads/{session.Id}/content"
            : supportSessionId is { } support
                ? $"/api/platform/support-sessions/{support}/artifacts/{artifactId}/downloads/{session.Id}/content"
                : $"/api/tenants/{tenantId.Value}/backup-artifacts/{artifactId}/downloads/{session.Id}/content";
        return new PreparedDownload(session.Id, url, filename, decrypt ? artifact.SourceChecksum : session.Checksum, session.SizeBytes, decrypt, session.AgentId is not null);
    }

    /// <summary>
    /// The caller's own download of this artifact. A download belongs to the channel it was created through: an ordinary route never serves a support or break-glass download
    /// (even to the same user) and a support or break-glass route serves only the downloads of that session or request, so a claim cannot be carried across.
    /// </summary>
    public async Task<DownloadSession?> GetOwnedAsync(TenantId tenantId, Guid artifactId, Guid sessionId, Guid userId, CancellationToken cancellationToken, Guid? supportSessionId = null,
        Guid? breakGlassRequestId = null)
    {
        var session = await store.GetAsync(tenantId, sessionId, cancellationToken);
        if (session is null || session.ArtifactId != artifactId || session.UserId != userId || session.SupportSessionId != supportSessionId || session.BreakGlassRequestId != breakGlassRequestId) return null;
        if (!session.IsTerminal && !session.IsPinned(time.GetUtcNow()))
        {
            session = await store.ExpireSessionAsync(tenantId, sessionId, time.GetUtcNow(), cancellationToken);
        }
        return session;
    }

    public async Task<(Stream Stream, DownloadSession Session)> OpenReadWithFallbackAsync(DownloadSession session, CancellationToken cancellationToken)
    {
        if (session.AgentId is not null || StorageProviderTypes.AgentExecuted(session.StorageProvider))
            throw new DownloadUnavailableException("DirectPathUnavailable");
        if (!session.IsPinned(time.GetUtcNow())) throw new DownloadUnavailableException("download_interrupted");
        var artifact = await store.GetArtifactAsync(session.TenantId, session.ArtifactId, cancellationToken);
        if (artifact is null) throw new DownloadUnavailableException("artifact_not_found");

        var candidates = artifact.Replicas
            .Where(replica => replica.TenantId == session.TenantId && replica.ArtifactId == session.ArtifactId &&
                (replica.Status == BackupReplicaStatus.Available ||
                    replica.Id == session.ReplicaId && replica.Status == BackupReplicaStatus.DeletePending) &&
                replica.VerificationState == "Verified" && replica.Checksum == session.Checksum && replica.SizeBytes == session.SizeBytes &&
                (replica.Id == session.ReplicaId || replica.ExecutionLocation == BackupStorageExecutionLocation.Backend))
            .OrderBy(replica => replica.Id == session.ReplicaId ? 0 : 1)
            .ThenBy(replica => ReplicaRepairSourceEvaluator.GetProviderPriority(replica.StorageProvider))
            .ThenByDescending(replica => replica.CreatedAt);

        StorageException? integrityFailure = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Id != session.ReplicaId)
            {
                // Commit the new hold before opening bytes; a stale owner or lost selection must never stream an unpinned source.
                var switched = await store.SwitchReplicaAsync(session.Id, session.ReplicaId, session.LeaseOwner, candidate, cancellationToken);
                if (switched is null) continue;
                logger.LogInformation("Download {SessionId} fell back from replica {OldReplicaId} to {NewReplicaId}.",
                    session.Id, session.ReplicaId, candidate.Id);
                session = switched;
            }

            Stream? stream = null;
            try
            {
                if (candidate.ExecutionLocation == BackupStorageExecutionLocation.Agent)
                    stream = await agentSource.OpenReadAsync(session, cancellationToken);
                else
                {
                    var provider = storage.Resolve(session.TenantId, session.StorageProvider, session.StorageTarget);
                    if (provider is null || !provider.Capabilities.Supports(StorageCapabilityIds.Read)) continue;
                    stream = await provider.OpenReadAsync(new StorageObjectKey(session.Key), cancellationToken);
                }
                if (stream.CanSeek) await VerifyAsync(stream, session, cancellationToken);
                return (stream, session);
            }
            catch (Exception exception) when (exception is StorageException or IOException or UnauthorizedAccessException)
            {
                if (exception is StorageException { Kind: StorageFailureKind.IntegrityMismatch } mismatch) integrityFailure = mismatch;
                logger.LogWarning("Download read failed for replica {ReplicaId} with {ExceptionType}.", candidate.Id, exception.GetType().Name);
            }
            catch
            {
                if (stream is not null) await stream.DisposeAsync();
                throw;
            }
            if (stream is not null) await stream.DisposeAsync();
        }
        if (integrityFailure is not null) throw integrityFailure;
        throw new DownloadUnavailableException("replica_unavailable");
    }

    private static async Task VerifyAsync(Stream source, DownloadSession session, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            long length = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) break;
                length += read;
                if (length > session.SizeBytes) throw IntegrityFailure();
                hash.AppendData(buffer, 0, read);
            }
            if (length != session.SizeBytes || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), session.Checksum, StringComparison.Ordinal))
                throw IntegrityFailure();
            source.Position = 0;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static StorageException IntegrityFailure() => new(StorageFailureKind.IntegrityMismatch, "The stored backup failed integrity verification.");

    public Task ReleaseAsync(DownloadSession session, CancellationToken cancellationToken) => session.AgentId is null
        ? Task.CompletedTask : agentSource.ReleaseAsync(session.Id, cancellationToken);

    private async Task<string?> GetReasonAsync(BackupArtifact artifact, BackupReplica replica, CancellationToken cancellationToken)
    {
        if (replica.TenantId != artifact.TenantId || replica.ArtifactId != artifact.Id || replica.Status != BackupReplicaStatus.Available ||
            replica.VerificationState != "Verified" || replica.Checksum != artifact.ArtifactChecksum || replica.SizeBytes != artifact.SizeBytes ||
            artifact.ChecksumAlgorithm != "sha256") return "replica_unavailable";
        if (replica.ExecutionLocation == BackupStorageExecutionLocation.Agent || StorageProviderTypes.AgentExecuted(replica.StorageProvider))
            return await agentSource.GetUnavailableReasonAsync(artifact.TenantId, replica, cancellationToken);
        var provider = storage.Resolve(artifact.TenantId, replica.StorageProvider, replica.StorageTarget);
        if (provider is null) return "storage_not_configured";
        if (!provider.Capabilities.Supports(StorageCapabilityIds.Read)) return "storage_read_not_supported";
        try { return await provider.ExistsAsync(new StorageObjectKey(replica.Key), cancellationToken) ? null : "replica_unavailable"; }
        catch (StorageException exception)
        {
            logger.LogWarning("Download availability failed with {FailureKind} for artifact {ArtifactId}, replica {ReplicaId}", exception.Kind, artifact.Id, replica.Id);
            return "replica_unavailable";
        }
    }
}

