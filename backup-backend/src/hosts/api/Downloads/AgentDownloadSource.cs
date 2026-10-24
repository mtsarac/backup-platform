using BackupPlatform.Domain.Storage;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Downloads;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Api.Downloads;

public sealed class AgentDownloadSource(IDownloadStore store, AgentConnectivityQueryService agents, DirectDownloadSignals signals, TimeProvider time) : IAgentDownloadSource
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);

    public async Task<Guid?> SelectAgentAsync(TenantId tenantId, BackupReplica replica, CancellationToken cancellationToken)
    {
        bool Eligible(AgentStatusView agent) => agent.Status == AgentOperationalStatus.Online
            && agent.Capabilities?.Capabilities.Any(item => item.Identifier == DownloadTaskTypes.Capability && item.Support == CapabilitySupport.Supported) == true
            && (replica.StorageProvider == StorageProviderTypes.Local || agent.Capabilities.Capabilities.Any(item => item.Identifier == TaskStorageProtocol.DirectCapability && item.Support == CapabilitySupport.Supported));
        if (replica.StorageProvider == StorageProviderTypes.Local)
        {
            if (replica.OwningAgentId is not { } owner) return null;
            var status = await agents.GetAsync(tenantId, new AgentId(owner), cancellationToken);
            return status is not null && Eligible(status) ? owner : null;
        }
        if (!StorageProviderTypes.AgentExecuted(replica.StorageProvider)) return null;
        return (await agents.ListAsync(tenantId, AgentConnectivityFilter.Online, cancellationToken)).FirstOrDefault(Eligible)?.Agent.Id.Value;
    }

    public async Task<string?> GetUnavailableReasonAsync(TenantId tenantId, BackupReplica replica, CancellationToken cancellationToken) =>
        replica.TenantId == tenantId && await SelectAgentAsync(tenantId, replica, cancellationToken) is not null ? null : DownloadAgentFailureCodes.DirectPathUnavailable;

    public Task<Stream> OpenReadAsync(DownloadSession session, CancellationToken cancellationToken) => throw new DownloadUnavailableException(DownloadAgentFailureCodes.DirectPathUnavailable);
    public Task ReleaseAsync(Guid sessionId, CancellationToken cancellationToken) { signals.Release(sessionId); return Task.CompletedTask; }

    public async Task<DownloadTaskSnapshot?> ClaimAsync(AgentIdentity agent, CancellationToken cancellationToken)
    {
        var sessions = await store.ListPreparingAsync(agent.TenantId, agent.AgentId.Value, cancellationToken);
        if (sessions.Any(session => session.LeaseOwner is not null && session.LeaseExpiresAt > time.GetUtcNow())) return null;
        foreach (var session in sessions)
        {
            if (session.LeaseOwner is not null || signals.Offer(session.Id) is null || await ValidateAsync(agent, session, cancellationToken) is null) continue;
            var owner = Guid.NewGuid();
            var claimed = await store.TransitionAsync(session.Id, new DownloadTransition(DownloadState.Preparing, DownloadState.Preparing,
                null, owner.ToString("D"), time.GetUtcNow(), LeaseDuration), cancellationToken);
            if (claimed is null) continue;
            var artifact = (await store.GetArtifactAsync(agent.TenantId, session.ArtifactId, cancellationToken))!;
            var replica = artifact.Replicas.Single(item => item.Id == session.ReplicaId);
            return new DownloadTaskSnapshot
            {
                SessionId = claimed.Id, TenantId = claimed.TenantId.Value, ArtifactId = claimed.ArtifactId, ReplicaId = claimed.ReplicaId,
                LeaseToken = owner, LeaseExpiresAt = claimed.LeaseExpiresAt!.Value, StorageProvider = claimed.StorageProvider,
                StorageTarget = claimed.StorageTarget, StorageKey = claimed.Key, SizeBytes = claimed.SizeBytes, Checksum = claimed.Checksum,
                ConnectionId = replica.StorageConnectionId, Decrypt = claimed.Decrypted, SourceChecksum = claimed.Decrypted ? artifact.SourceChecksum : null
            };
        }
        return null;
    }

    public async Task<BackupTaskLeaseResponse?> RenewAsync(AgentIdentity agent, Guid sessionId, Guid leaseToken, CancellationToken cancellationToken)
    {
        var session = await GetFencedAsync(agent, sessionId, leaseToken, cancellationToken);
        if (session is null) return null;
        var renewed = await store.TransitionAsync(sessionId, new DownloadTransition(session.State, session.State,
            session.LeaseOwner, session.LeaseOwner, time.GetUtcNow(), LeaseDuration, session.TransferredBytes), cancellationToken);
        return renewed is null ? null : new BackupTaskLeaseResponse { LeaseExpiresAt = renewed.LeaseExpiresAt!.Value };
    }

    public async Task<DownloadSignal?> SignalAsync(AgentIdentity agent, Guid sessionId, DownloadSignalRequest request, CancellationToken cancellationToken)
    {
        var session = await GetFencedAsync(agent, sessionId, request.LeaseToken, cancellationToken);
        if (session is null) return null;
        if (request.Sdp is null) return new DownloadSignal(signals.Offer(sessionId));
        if (session.State != DownloadState.Preparing || !DirectDownloadSignals.ValidSdp(request.Sdp)) return null;
        // Re-enter the existing streaming gate: tenant role and support-session revocation are checked before peer bytes can flow.
        var ready = await store.TransitionAsync(sessionId, new DownloadTransition(DownloadState.Preparing, DownloadState.Ready,
            session.LeaseOwner, null, time.GetUtcNow()), cancellationToken);
        if (ready is null) return null;
        var streaming = await store.TransitionAsync(sessionId, new DownloadTransition(DownloadState.Ready, DownloadState.Streaming,
            null, session.LeaseOwner, time.GetUtcNow(), LeaseDuration), cancellationToken);
        return streaming is not null && signals.Answer(sessionId, request.Sdp) ? new DownloadSignal(null) : null;
    }

    public async Task<bool> CompleteAsync(AgentIdentity agent, Guid sessionId, DownloadTaskCompletionRequest request, CancellationToken cancellationToken)
    {
        var session = await GetFencedAsync(agent, sessionId, request.LeaseToken, cancellationToken);
        if (session is null) return false;
        var state = request.Status switch { "Completed" => DownloadState.Completed, "Cancelled" => DownloadState.Cancelled, _ => DownloadState.Failed };
        if (state == DownloadState.Completed)
        {
            var artifact = await store.GetArtifactAsync(agent.TenantId, session.ArtifactId, cancellationToken);
            if (session.State != DownloadState.Streaming || request.TransferredBytes != session.SizeBytes
                || request.Checksum != (session.Decrypted ? artifact?.SourceChecksum : session.Checksum)) return false;
        }
        var completed = await store.TransitionAsync(sessionId, new DownloadTransition(session.State, state,
            session.LeaseOwner, null, time.GetUtcNow(), Bytes: state == DownloadState.Completed ? session.SizeBytes : session.TransferredBytes,
            FailureCode: state == DownloadState.Completed ? null : SafeFailure(request.FailureCode)), cancellationToken);
        if (completed is not null) signals.Release(sessionId);
        return completed is not null;
    }

    public async Task<DownloadSession?> GetFencedAsync(AgentIdentity agent, Guid sessionId, Guid leaseToken, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty || leaseToken == Guid.Empty) return null;
        var session = await store.GetAsync(agent.TenantId, sessionId, cancellationToken);
        if (session is null || session.State is not (DownloadState.Preparing or DownloadState.Streaming) || session.LeaseOwner != leaseToken.ToString("D")
            || !session.IsPinned(time.GetUtcNow())) return null;
        return await ValidateAsync(agent, session, cancellationToken);
    }

    private async Task<DownloadSession?> ValidateAsync(AgentIdentity agent, DownloadSession session, CancellationToken cancellationToken)
    {
        if (session.TenantId != agent.TenantId || session.AgentId != agent.AgentId.Value) return null;
        var artifact = await store.GetArtifactAsync(agent.TenantId, session.ArtifactId, cancellationToken);
        var replica = artifact?.Replicas.SingleOrDefault(item => item.Id == session.ReplicaId);
        return artifact is not null && replica is not null && replica.TenantId == session.TenantId && replica.ArtifactId == session.ArtifactId
            && (StorageProviderTypes.AgentExecuted(replica.StorageProvider) || replica.OwningAgentId == session.AgentId && replica.ExecutionLocation == BackupStorageExecutionLocation.Agent)
            && replica.Status is not (BackupReplicaStatus.Deleting or BackupReplicaStatus.Deleted)
            && replica.VerificationState == "Verified" && replica.StorageProvider == session.StorageProvider
            && replica.StorageTarget == session.StorageTarget && replica.Key == session.Key && replica.Checksum == session.Checksum
            && replica.SizeBytes == session.SizeBytes && artifact.ArtifactChecksum == session.Checksum && artifact.SizeBytes == session.SizeBytes
            && replica.ChecksumAlgorithm == "sha256" && artifact.ChecksumAlgorithm == "sha256" ? session : null;
    }

    private static string SafeFailure(string? failure) => failure switch
    {
        DownloadAgentFailureCodes.InvalidConfiguration or DownloadAgentFailureCodes.SourceMissing or DownloadAgentFailureCodes.SourceInaccessible
            or DownloadAgentFailureCodes.IntegrityMismatch or DownloadAgentFailureCodes.AgentUnavailable or DownloadAgentFailureCodes.Cancelled
            or DownloadAgentFailureCodes.DirectPathUnavailable => failure,
        _ => DownloadAgentFailureCodes.InternalError
    };
}
