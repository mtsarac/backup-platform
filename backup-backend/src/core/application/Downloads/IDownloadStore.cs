using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Downloads;

public sealed record DownloadCreateCommand(TenantId TenantId, Guid ArtifactId, Guid ReplicaId, Guid UserId, string Filename, DateTimeOffset Now, TimeSpan Lifetime,
    bool Decrypted = false, Guid? DirectAgentId = null, Guid? ShareId = null);
public sealed record DownloadRefusalCommand(TenantId TenantId, Guid ArtifactId, Guid UserId, Guid? ShareId, string Reason, DateTimeOffset Now, bool IsPlatformAdmin = false);
public sealed record DownloadTransition(DownloadState ExpectedState, DownloadState State, string? ExpectedOwner, string? Owner,
    DateTimeOffset Now, TimeSpan? LeaseDuration = null, long Bytes = 0, string? FailureCode = null);

/// <summary>Every lifecycle write is atomic with its audit. Create locks artifact then replica; transitions fence the session row.</summary>
public interface IDownloadStore
{
    Task<BackupArtifact?> GetArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken);
    Task<DownloadSession?> CreateAsync(DownloadCreateCommand command, CancellationToken cancellationToken);
    Task<DownloadSession?> GetAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DownloadSession>> ListPreparingAsync(TenantId tenantId, Guid agentId, CancellationToken cancellationToken);
    Task<DownloadSession?> TransitionAsync(Guid sessionId, DownloadTransition transition, CancellationToken cancellationToken);
    Task<int> ExpireAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<DownloadSession?> ExpireSessionAsync(TenantId tenantId, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<DownloadSession?> SwitchReplicaAsync(Guid sessionId, Guid currentReplicaId, string? expectedOwner, BackupReplica newReplica, CancellationToken cancellationToken);
    Task RecordRefusalAsync(DownloadRefusalCommand command, CancellationToken cancellationToken);
    Task<bool> IsShareRevokedAsync(TenantId tenantId, Guid shareId, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>Agent availability and control-plane selection. Direct browser sessions never expose a backend payload stream.</summary>
public interface IAgentDownloadSource
{
    Task<string?> GetUnavailableReasonAsync(TenantId tenantId, BackupReplica replica, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(DownloadSession session, CancellationToken cancellationToken);
    Task ReleaseAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<Guid?> SelectAgentAsync(TenantId tenantId, BackupReplica replica, CancellationToken cancellationToken) => Task.FromResult(replica.OwningAgentId);
}
