using System.Buffers;
using System.Security.Cryptography;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Downloads;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Api.Downloads;

public sealed class AgentDownloadSource(IDownloadStore store, AgentConnectivityQueryService agents, AgentDownloadSpool spool, TimeProvider time) : IAgentDownloadSource
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);

    public async Task<string?> GetUnavailableReasonAsync(TenantId tenantId, BackupReplica replica, CancellationToken cancellationToken)
    {
        if (replica.TenantId != tenantId || replica.ExecutionLocation != BackupStorageExecutionLocation.Agent
            || replica.StorageProvider != "local" || replica.OwningAgentId is not { } agentId)
        {
            return DownloadAgentFailureCodes.SourceInaccessible;
        }
        var status = await agents.GetAsync(tenantId, new AgentId(agentId), cancellationToken);
        if (status?.Status != AgentOperationalStatus.Online) return DownloadAgentFailureCodes.AgentUnavailable;
        return status.Capabilities?.Capabilities.Any(capability => capability.Identifier == DownloadTaskTypes.Capability
            && capability.Support == CapabilitySupport.Supported) == true ? null : DownloadAgentFailureCodes.SourceInaccessible;
    }

    public Task<Stream> OpenReadAsync(DownloadSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(spool.OpenRead(session));
    }

    public Task ReleaseAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        spool.Release(sessionId);
        return Task.CompletedTask;
    }

    public async Task<DownloadTaskSnapshot?> ClaimAsync(AgentIdentity agent, CancellationToken cancellationToken)
    {
        var sessions = await store.ListPreparingAsync(agent.TenantId, agent.AgentId.Value, cancellationToken);
        if (sessions.Any(session => session.LeaseOwner is not null && session.LeaseExpiresAt > time.GetUtcNow())) return null;
        foreach (var session in sessions)
        {
            if (session.LeaseOwner is not null || await ValidateAsync(agent, session, cancellationToken) is null) continue;
            var owner = Guid.NewGuid();
            var claimed = await store.TransitionAsync(session.Id, new DownloadTransition(DownloadState.Preparing, DownloadState.Preparing,
                null, owner.ToString("D"), time.GetUtcNow(), LeaseDuration), cancellationToken);
            if (claimed is null) continue;
            return new DownloadTaskSnapshot
            {
                SessionId = claimed.Id, TenantId = claimed.TenantId.Value, ArtifactId = claimed.ArtifactId, ReplicaId = claimed.ReplicaId,
                LeaseToken = owner, LeaseExpiresAt = claimed.LeaseExpiresAt!.Value, StorageProvider = claimed.StorageProvider,
                StorageTarget = claimed.StorageTarget, StorageKey = claimed.Key, SizeBytes = claimed.SizeBytes, Checksum = claimed.Checksum
            };
        }
        return null;
    }

    public async Task<BackupTaskLeaseResponse?> RenewAsync(AgentIdentity agent, Guid sessionId, Guid leaseToken, CancellationToken cancellationToken)
    {
        var session = await GetFencedAsync(agent, sessionId, leaseToken, cancellationToken);
        if (session is null) return null;
        var renewed = await store.TransitionAsync(sessionId, new DownloadTransition(DownloadState.Preparing, DownloadState.Preparing,
            session.LeaseOwner, session.LeaseOwner, time.GetUtcNow(), LeaseDuration, session.TransferredBytes), cancellationToken);
        return renewed is null ? null : new BackupTaskLeaseResponse { LeaseExpiresAt = renewed.LeaseExpiresAt!.Value };
    }

    public async Task<bool> CompleteAsync(AgentIdentity agent, Guid sessionId, DownloadTaskCompletionRequest request, CancellationToken cancellationToken)
    {
        var session = await GetFencedAsync(agent, sessionId, request.LeaseToken, cancellationToken);
        if (session is null) return false;
        var failure = request.Status == "Cancelled" ? DownloadAgentFailureCodes.Cancelled : SafeFailure(request.FailureCode);
        var state = request.Status == "Cancelled" ? DownloadState.Cancelled : DownloadState.Failed;
        var completed = await store.TransitionAsync(sessionId, new DownloadTransition(DownloadState.Preparing, state,
            session.LeaseOwner, null, time.GetUtcNow(), Bytes: session.TransferredBytes, FailureCode: failure), cancellationToken);
        if (completed is not null) spool.Release(sessionId);
        return completed is not null;
    }

    public async Task<bool> ReceiveAsync(AgentIdentity agent, Guid sessionId, Guid leaseToken, HttpRequest request, CancellationToken cancellationToken)
    {
        var session = await GetFencedAsync(agent, sessionId, leaseToken, cancellationToken);
        if (session is null || !spool.TryBeginUpload(sessionId)) return false;
        FileStream? file = null;
        var published = false;
        var readyForBrowser = false;
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? monitor = null;
        try
        {
            if (request.ContentLength != session.SizeBytes || request.ContentType != "application/octet-stream")
            {
                await FailAsync(session, DownloadAgentFailureCodes.IntegrityMismatch, cancellationToken);
                return false;
            }
            file = spool.CreateFile();
            monitor = MonitorAsync(agent, session, transfer, monitoring.Token);
            await CopyVerifiedAsync(request.Body, file, session, transfer.Token);
            await monitoring.CancelAsync();
            await monitor;
            transfer.Token.ThrowIfCancellationRequested();
            await file.FlushAsync(transfer.Token);
            var current = await GetFencedAsync(agent, sessionId, leaseToken, cancellationToken);
            if (current is null) return false;
            session = current;
            if (!spool.Publish(session, file)) return false;
            published = true;
            var ready = await store.TransitionAsync(sessionId, new DownloadTransition(DownloadState.Preparing, DownloadState.Ready,
                session.LeaseOwner, null, time.GetUtcNow(), DownloadService.SessionLifetime), cancellationToken);
            readyForBrowser = ready is not null;
            if (ready is not null) spool.RefreshExpiry(ready);
            return readyForBrowser;
        }
        catch (OperationCanceledException) when (transfer.IsCancellationRequested)
        {
            await monitoring.CancelAsync();
            if (monitor is not null) await monitor;
            await FailAsync(session, DownloadAgentFailureCodes.Cancelled, CancellationToken.None);
            return false;
        }
        catch (StorageException exception)
        {
            await monitoring.CancelAsync();
            if (monitor is not null) await monitor;
            await FailAsync(session, exception.Kind == StorageFailureKind.IntegrityMismatch
                ? DownloadAgentFailureCodes.IntegrityMismatch : DownloadAgentFailureCodes.SourceInaccessible, CancellationToken.None);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await monitoring.CancelAsync();
            if (monitor is not null) await monitor;
            await FailAsync(session, DownloadAgentFailureCodes.SourceInaccessible, CancellationToken.None);
            return false;
        }
        finally
        {
            await monitoring.CancelAsync();
            if (monitor is not null) await monitor;
            if (published && !readyForBrowser) spool.Release(sessionId);
            if (!published && file is not null) await file.DisposeAsync();
            spool.EndUpload(sessionId);
        }
    }

    private async Task MonitorAsync(AgentIdentity agent, DownloadSession session, CancellationTokenSource transfer, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), time, cancellationToken);
                if (await GetFencedAsync(agent, session.Id, Guid.Parse(session.LeaseOwner!), cancellationToken) is not null) continue;
                await transfer.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            await transfer.CancelAsync();
        }
    }

    private async Task FailAsync(DownloadSession session, string failure, CancellationToken cancellationToken)
    {
        await store.TransitionAsync(session.Id, new DownloadTransition(DownloadState.Preparing, DownloadState.Failed,
            session.LeaseOwner, null, time.GetUtcNow(), Bytes: session.TransferredBytes, FailureCode: failure), cancellationToken);
        spool.Release(session.Id);
    }

    private async Task<DownloadSession?> GetFencedAsync(AgentIdentity agent, Guid sessionId, Guid leaseToken, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty || leaseToken == Guid.Empty) return null;
        var session = await store.GetAsync(agent.TenantId, sessionId, cancellationToken);
        if (session is null || session.State != DownloadState.Preparing || session.LeaseOwner != leaseToken.ToString("D")
            || session.LeaseExpiresAt <= time.GetUtcNow() || session.ExpiresAt <= time.GetUtcNow()) return null;
        return await ValidateAsync(agent, session, cancellationToken);
    }

    private async Task<DownloadSession?> ValidateAsync(AgentIdentity agent, DownloadSession session, CancellationToken cancellationToken)
    {
        if (session.TenantId != agent.TenantId || session.AgentId != agent.AgentId.Value || session.StorageProvider != "local") return null;
        var artifact = await store.GetArtifactAsync(agent.TenantId, session.ArtifactId, cancellationToken);
        var replica = artifact?.Replicas.SingleOrDefault(replica => replica.Id == session.ReplicaId);
        return artifact is not null && replica is not null && replica.TenantId == session.TenantId && replica.ArtifactId == session.ArtifactId
            && replica.OwningAgentId == session.AgentId && replica.ExecutionLocation == BackupStorageExecutionLocation.Agent
            && replica.Status is not (BackupReplicaStatus.Deleting or BackupReplicaStatus.Deleted)
            && replica.VerificationState == "Verified" && replica.StorageProvider == session.StorageProvider
            && replica.StorageTarget == session.StorageTarget && replica.Key == session.Key && replica.Checksum == session.Checksum
            && replica.SizeBytes == session.SizeBytes && artifact.ArtifactChecksum == session.Checksum && artifact.SizeBytes == session.SizeBytes
            && replica.ChecksumAlgorithm == "sha256" && artifact.ChecksumAlgorithm == "sha256" ? session : null;
    }

    private static async Task CopyVerifiedAsync(Stream source, Stream target, DownloadSession session, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, 64 * 1024), cancellationToken)) != 0)
            {
                total = checked(total + read);
                if (total > session.SizeBytes) throw new StorageException(StorageFailureKind.IntegrityMismatch, "The download length does not match.");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (total != session.SizeBytes || ContentChecksum.Sha256(hash.GetHashAndReset()).Hex != session.Checksum)
                throw new StorageException(StorageFailureKind.IntegrityMismatch, "The download checksum or length does not match.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string SafeFailure(string? failure) => failure switch
    {
        DownloadAgentFailureCodes.InvalidConfiguration or DownloadAgentFailureCodes.SourceMissing or DownloadAgentFailureCodes.SourceInaccessible
            or DownloadAgentFailureCodes.IntegrityMismatch or DownloadAgentFailureCodes.AgentUnavailable => failure,
        _ => DownloadAgentFailureCodes.InternalError
    };
}
