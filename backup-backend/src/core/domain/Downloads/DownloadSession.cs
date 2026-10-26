using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Downloads;

public enum DownloadState { Preparing, Ready, Streaming, Completed, Failed, Cancelled }

/// <summary>A user-bound download and its expiring retention hold. Snapshots are never supplied by the browser.</summary>
public sealed class DownloadSession : ITenantOwned
{
    private DownloadSession() { TenantId = null!; StorageProvider = null!; StorageTarget = null!; Key = null!; Checksum = null!; Filename = null!; }
    public DownloadSession(Guid id, BackupArtifact artifact, BackupReplica replica, Guid userId, string filename, DateTimeOffset now, TimeSpan lifetime, Guid? supportSessionId = null,
        bool decrypted = false, Guid? breakGlassRequestId = null, Guid? directAgentId = null)
    {
        if (supportSessionId == Guid.Empty || breakGlassRequestId == Guid.Empty) throw new ArgumentException("A support session or break-glass id must not be empty.", nameof(supportSessionId));
        if (decrypted && (artifact.Format != BackupArtifact.BpeFormat || supportSessionId is not null) || breakGlassRequestId is not null && !decrypted)
            throw new ArgumentException("Only an encrypted artifact can be downloaded decrypted, never through a support session, and a break-glass download is always decrypted.");
        if (id == Guid.Empty || userId == Guid.Empty || lifetime <= TimeSpan.Zero) throw new ArgumentException("Invalid download identity or lifetime.");
        if (replica.TenantId != artifact.TenantId || replica.ArtifactId != artifact.Id || replica.Status != BackupReplicaStatus.Available ||
            replica.VerificationState != "Verified" || replica.Checksum != artifact.ArtifactChecksum || replica.SizeBytes != artifact.SizeBytes ||
            artifact.ChecksumAlgorithm != "sha256") throw new ArgumentException("The replica cannot be downloaded.");
        Id = id; TenantId = artifact.TenantId; ArtifactId = artifact.Id; ReplicaId = replica.Id; UserId = userId; SupportSessionId = supportSessionId;
        Decrypted = decrypted; BreakGlassRequestId = breakGlassRequestId;
        if (directAgentId == Guid.Empty || directAgentId is not null && !StorageProviderTypes.AgentExecuted(replica.StorageProvider))
            throw new ArgumentException("A direct executor is only valid for external storage.");
        AgentId = replica.ExecutionLocation == BackupStorageExecutionLocation.Agent ? replica.OwningAgentId : directAgentId;
        StorageProvider = replica.StorageProvider; StorageTarget = replica.StorageTarget; Key = replica.Key;
        Checksum = artifact.ArtifactChecksum; SizeBytes = artifact.SizeBytes; Filename = filename;
        CreatedAt = now.ToUniversalTime(); ExpiresAt = CreatedAt + lifetime;
        State = AgentId is null ? DownloadState.Ready : DownloadState.Preparing;
    }
    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid ArtifactId { get; private set; }
    public Guid ReplicaId { get; private set; }
    public Guid UserId { get; private set; }
    /// <summary>The support access session this download was authorized through; null for an ordinary tenant download. Fixed at creation.</summary>
    public Guid? SupportSessionId { get; private set; }
    /// <summary>The serving host verifies private ciphertext and plaintext staging before streaming; metadata still describes stored ciphertext.</summary>
    public bool Decrypted { get; private set; }
    /// <summary>The break-glass request this download spent; null otherwise. A download belongs to the channel it was created through. Fixed at creation.</summary>
    public Guid? BreakGlassRequestId { get; private set; }
    public Guid? AgentId { get; private set; }
    public string StorageProvider { get; private set; }
    public string StorageTarget { get; private set; }
    public string Key { get; private set; }
    public string Checksum { get; private set; }
    public long SizeBytes { get; private set; }
    public string Filename { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DownloadState State { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public long TransferredBytes { get; private set; }
    public string? FailureCode { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
    public bool IsTerminal => State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled;
    public bool IsPinned(DateTimeOffset now) => !IsTerminal && ExpiresAt > now && (LeaseExpiresAt is null || LeaseExpiresAt > now);

    public bool Transition(DownloadState expectedState, string? expectedOwner, DownloadState state, string? owner, DateTimeOffset now,
        TimeSpan? leaseDuration, long bytes, string? failureCode)
    {
        if (IsTerminal || State != expectedState || LeaseOwner != expectedOwner || ExpiresAt <= now || LeaseExpiresAt <= now) return false;
        if (bytes < TransferredBytes || bytes > SizeBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        var allowed = state is DownloadState.Failed or DownloadState.Cancelled ||
            (State == DownloadState.Preparing && state is DownloadState.Preparing or DownloadState.Ready) ||
            (State == DownloadState.Ready && state == DownloadState.Streaming) ||
            (State == DownloadState.Streaming && state is DownloadState.Streaming or DownloadState.Completed);
        if (!allowed || (state == DownloadState.Completed && bytes != SizeBytes)) return false;
        if ((state is DownloadState.Preparing or DownloadState.Streaming) && (owner is null || leaseDuration is null || leaseDuration <= TimeSpan.Zero)) return false;
        State = state; TransferredBytes = bytes; FailureCode = failureCode;
        LeaseOwner = state == DownloadState.Ready || IsTerminal ? null : owner;
        LeaseExpiresAt = LeaseOwner is null ? null : now + leaseDuration!.Value;
        if (BreakGlassRequestId is not null && LeaseExpiresAt > ExpiresAt) LeaseExpiresAt = ExpiresAt;
        if (BreakGlassRequestId is null && LeaseExpiresAt is { } renewed && renewed > ExpiresAt) ExpiresAt = renewed;
        if (BreakGlassRequestId is null && state == DownloadState.Ready && leaseDuration is { } idleLifetime && idleLifetime > TimeSpan.Zero && now + idleLifetime > ExpiresAt)
            ExpiresAt = now + idleLifetime;
        if (IsTerminal) ReleasedAt = now;
        return true;
    }

    public bool SwitchReplica(BackupReplica newReplica, string? expectedOwner, DateTimeOffset now)
    {
        if (!IsPinned(now) || LeaseOwner != expectedOwner || TransferredBytes != 0 ||
            (State != DownloadState.Ready && State != DownloadState.Streaming) || newReplica.ExecutionLocation != BackupStorageExecutionLocation.Backend) return false;
        if (newReplica.TenantId != TenantId || newReplica.ArtifactId != ArtifactId || newReplica.Status != BackupReplicaStatus.Available ||
            newReplica.VerificationState != "Verified" || newReplica.Checksum != Checksum || newReplica.SizeBytes != SizeBytes) return false;
        ReplicaId = newReplica.Id;
        AgentId = newReplica.ExecutionLocation == BackupStorageExecutionLocation.Agent ? newReplica.OwningAgentId : null;
        StorageProvider = newReplica.StorageProvider;
        StorageTarget = newReplica.StorageTarget;
        Key = newReplica.Key;
        return true;
    }

    /// <summary>
    /// Ends a not yet terminal download because the support session that authorized it was closed or revoked. A transfer in progress sees the
    /// state change on its next lease renewal and stops. False (no change) when already terminal.
    /// </summary>
    public bool CancelForSupport(DateTimeOffset now, string failureCode)
    {
        if (SupportSessionId is null) throw new InvalidOperationException("Only a support download can be cancelled by its support session.");
        if (IsTerminal) return false;
        State = DownloadState.Cancelled; FailureCode = failureCode;
        ReleasedAt = now; LeaseOwner = null; LeaseExpiresAt = null;
        return true;
    }

    public bool Expire(DateTimeOffset now)
    {
        if (IsTerminal || (ExpiresAt > now && (LeaseExpiresAt is null || LeaseExpiresAt > now))) return false;
        State = DownloadState.Failed; FailureCode = LeaseOwner is null ? "download_expired" : "download_interrupted";
        ReleasedAt = now; LeaseOwner = null; LeaseExpiresAt = null;
        return true;
    }
}
