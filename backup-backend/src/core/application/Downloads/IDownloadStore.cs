using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Downloads;

/// <summary>
/// <paramref name="SupportSessionId"/> set means the download is authorized by a support session instead of tenant membership: the store judges that session (owner, active, DownloadBackup) under its row lock.
/// <paramref name="BreakGlassRequestId"/> spends that approved single-use grant in the same transaction; decryption happens on the authorized direct agent for external storage.
/// </summary>
public sealed record DownloadCreateCommand(TenantId TenantId, Guid ArtifactId, Guid ReplicaId, Guid UserId, string Filename, DateTimeOffset Now, TimeSpan Lifetime, Guid? SupportSessionId = null,
    bool Decrypted = false, Guid? BreakGlassRequestId = null, Guid? DirectAgentId = null);
public sealed record DownloadTransition(DownloadState ExpectedState, DownloadState State, string? ExpectedOwner, string? Owner,
    DateTimeOffset Now, TimeSpan? LeaseDuration = null, long Bytes = 0, string? FailureCode = null);

/// <summary>Every lifecycle write is atomic with its audit. Create locks (support session shared when there is one) artifact then replica; transitions fence the session row.</summary>
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
}

/// <summary>Agent availability and control-plane selection. Direct browser sessions never expose a backend payload stream.</summary>
public interface IAgentDownloadSource
{
    Task<string?> GetUnavailableReasonAsync(TenantId tenantId, BackupReplica replica, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(DownloadSession session, CancellationToken cancellationToken);
    Task ReleaseAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<Guid?> SelectAgentAsync(TenantId tenantId, BackupReplica replica, CancellationToken cancellationToken) => Task.FromResult(replica.OwningAgentId);
}
