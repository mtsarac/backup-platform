using System.Text.Json;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

public sealed class BackupArtifact : ITenantOwned
{
    private IReadOnlyList<BackupReplica>? _replicaView;
    private readonly List<BackupReplica> _replicas = [];
    private bool _sealed;
    private BackupArtifact() { TenantId = null!; SourceChecksum = null!; ArtifactChecksum = null!; ChecksumAlgorithm = null!; SourceMetadataJson = null!; AppliedOptionsJson = null!; }
    public BackupArtifact(Guid id, TenantId tenantId, Guid attemptId, long sizeBytes, string sourceChecksum,
        string artifactChecksum, string checksumAlgorithm, DateTimeOffset createdAt, string sourceMetadataJson, string appliedOptionsJson)
    {
        BackupGuard.Id(id);
        BackupGuard.Id(attemptId);
        ArgumentNullException.ThrowIfNull(tenantId);
        if (sizeBytes <= 0) throw new ArgumentException("An artifact must contain bytes.");
        BackupGuard.Checksum(sourceChecksum, checksumAlgorithm);
        BackupGuard.Checksum(artifactChecksum, checksumAlgorithm);
        ValidateMetadata(sourceMetadataJson);
        ValidateMetadata(appliedOptionsJson);
        Id = id;
        TenantId = tenantId;
        AttemptId = attemptId;
        SizeBytes = sizeBytes;
        SourceChecksum = sourceChecksum.ToLowerInvariant();
        ArtifactChecksum = artifactChecksum.ToLowerInvariant();
        ChecksumAlgorithm = checksumAlgorithm;
        CreatedAt = BackupGuard.Timestamp(createdAt);
        SourceMetadataJson = sourceMetadataJson;
        AppliedOptionsJson = appliedOptionsJson;
    }
    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public string BackupType { get; private set; } = "Full";
    public Guid AttemptId { get; private set; }
    public long SizeBytes { get; private set; }
    public string SourceChecksum { get; private set; }
    public string ArtifactChecksum { get; private set; }
    public string ChecksumAlgorithm { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string SourceMetadataJson { get; private set; }
    public string AppliedOptionsJson { get; private set; }
    public IReadOnlyList<BackupReplica> Replicas => _replicaView ??= _replicas.AsReadOnly();
    public void AddReplica(BackupReplica replica)
    {
        ArgumentNullException.ThrowIfNull(replica);
        if (_sealed) throw new InvalidOperationException("Terminal artifact metadata is immutable.");
        if (replica.ArtifactId != Id || replica.TenantId != TenantId || replica.SizeBytes != SizeBytes ||
            replica.Checksum != ArtifactChecksum || replica.ChecksumAlgorithm != ChecksumAlgorithm ||
            _replicas.Any(existing => existing.StorageProvider == replica.StorageProvider && existing.StorageTarget == replica.StorageTarget && existing.Key == replica.Key) ||
            // One artifact holds at most one live verified copy per destination (a later repair replaces a Deleted one, it never doubles a live one).
            replica.VerificationState == "Verified" && replica.StorageConnectionId != Guid.Empty && _replicas.Any(existing => existing.VerificationState == "Verified" && existing.Status != BackupReplicaStatus.Deleted &&
                existing.StorageProvider == replica.StorageProvider && existing.StorageConnectionId == replica.StorageConnectionId))
            throw new ArgumentException("Replica does not match its artifact.");
        _replicas.Add(replica);
    }
    /// <summary>
    /// Publishes the artifact once the backend accepts the attempt's completion. <paramref name="acceptedAt"/> is the BACKEND's clock at that
    /// moment, never a timestamp the agent reported, and it is the only basis of retention: not the run's creation, not the attempt's
    /// start, not the agent's idea of when the backup finished. The policy is the run's snapshot, so the expiration is fixed here.
    /// </summary>
    internal void Seal(DateTimeOffset acceptedAt, BackupRetentionPolicy retention)
    {
        ArgumentNullException.ThrowIfNull(retention);
        if (!_replicas.Any(replica => replica.VerificationState == "Verified"))
            throw new InvalidOperationException("A successful artifact requires a verified replica.");
        retention.Validate();
        AcceptedAt = BackupGuard.Timestamp(acceptedAt);
        RetentionExpiresAt = retention.ExpiresAt(AcceptedAt.Value);
        RetentionGracePeriod = retention.DeleteGracePeriod;
        _sealed = true;
    }

    /// <summary>When the backend accepted the completion that published this artifact (UTC). Null only for artifacts that predate retention.</summary>
    public DateTimeOffset? AcceptedAt { get; private set; }
    /// <summary>
    /// When this backup expires: <see cref="AcceptedAt"/> plus the run's KeepFor. Null means it does not expire (retention was disabled,
    /// or the artifact predates retention). Fixed when the artifact is published; a later change of the job's policy never moves it.
    /// </summary>
    public DateTimeOffset? RetentionExpiresAt { get; private set; }
    /// <summary>The run's DeleteGracePeriod, snapshotted with the expiration: how long any deletion of this artifact stays cancellable.</summary>
    public TimeSpan RetentionGracePeriod { get; private set; } = TimeSpan.FromHours(24);
    /// <summary>Set when an administrator cancelled the deletion of an artifact that had already expired: automatic expiration no longer applies to it.</summary>
    public DateTimeOffset? RetentionPinnedAt { get; private set; }
    /// <summary>When automatic retention last acted on this artifact. Lets the retention scan skip artifacts it is already done with.</summary>
    public DateTimeOffset? RetentionAppliedAt { get; private set; }

    public bool HasExpired(DateTimeOffset now) => RetentionExpiresAt is { } expires && expires <= now;

    /// <summary>
    /// The logical state, derived from the replicas so it cannot drift: Available while any replica is still Available, Deleted once every
    /// replica is Deleted, DeletePending in between (nothing usable is guaranteed to remain, but removal is not finished). Requires the
    /// replicas to be loaded.
    /// </summary>
    public BackupArtifactState State =>
        // A copy that never verified (an orphan kept only so its bytes stay accounted for) never makes the artifact Available.
        _replicas.Any(replica => replica.Status == BackupReplicaStatus.Available && replica.VerificationState == "Verified") ? BackupArtifactState.Available
        : _replicas.Count > 0 && _replicas.All(replica => replica.Status == BackupReplicaStatus.Deleted) ? BackupArtifactState.Deleted
        : BackupArtifactState.DeletePending;

    /// <summary>
    /// Automatic retention: once the artifact has expired (and nobody pinned it) every Available replica enters DeletePending with the
    /// snapshotted grace period. Idempotent. Returns the replicas moved.
    /// </summary>
    public int ApplyRetention(DateTimeOffset now)
    {
        if (!HasExpired(now) || RetentionPinnedAt is not null) return 0;
        var moved = _replicas.Count(replica => replica.RequestDelete(now, BackupDeleteReason.RetentionExpired, RetentionGracePeriod, null));
        RetentionAppliedAt ??= BackupGuard.Timestamp(now);
        return moved;
    }

    /// <summary>An administrator's delete: never physical, always the same soft delete with the artifact's grace period.</summary>
    public BackupArtifactDeleteOutcome RequestDelete(DateTimeOffset now, Guid? requestedBy)
    {
        var moved = _replicas.Count(replica => replica.RequestDelete(now, BackupDeleteReason.Manual, RetentionGracePeriod, requestedBy));
        if (moved > 0) return BackupArtifactDeleteOutcome.Requested;
        return _replicas.Count > 0 && _replicas.All(replica => replica.Status == BackupReplicaStatus.Deleted)
            ? BackupArtifactDeleteOutcome.AlreadyDeleted
            : BackupArtifactDeleteOutcome.AlreadyInProgress;
    }

    /// <summary>
    /// Cancels a pending deletion: every replica still in its grace period becomes Available again. A replica whose physical delete has begun
    /// is never restored. If the artifact had already expired it is pinned, otherwise the very next retention scan would delete it again.
    /// Returns the replicas restored (0: there was nothing to cancel).
    /// </summary>
    public int CancelDelete(DateTimeOffset now)
    {
        var restored = _replicas.Count(replica => replica.CancelDelete());
        if (restored > 0 && HasExpired(now)) RetentionPinnedAt ??= BackupGuard.Timestamp(now);
        return restored;
    }
    internal bool SameResult(BackupArtifact other) => SizeBytes == other.SizeBytes && SourceChecksum == other.SourceChecksum &&
        ArtifactChecksum == other.ArtifactChecksum && ChecksumAlgorithm == other.ChecksumAlgorithm && CreatedAt == other.CreatedAt &&
        SameMetadata(SourceMetadataJson, other.SourceMetadataJson) && SameMetadata(AppliedOptionsJson, other.AppliedOptionsJson) &&
        _replicas.Count == other._replicas.Count && _replicas.All(replica => other._replicas.Any(replica.SameResult));
    private static bool SameMetadata(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.Ordinal)) return true;
        using var left = JsonDocument.Parse(first);
        using var right = JsonDocument.Parse(second);
        return JsonElement.DeepEquals(left.RootElement, right.RootElement);
    }
    private static void ValidateMetadata(string metadata)
    {
        if (metadata.Length > 16384) throw new ArgumentException("Backup metadata is too large.");
        using var document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Backup metadata must be an object.");
    }
}

/// <summary>The logical state of an artifact, derived from its replicas.</summary>
public enum BackupArtifactState
{
    Available = 1,
    DeletePending,
    Deleted
}

public enum BackupArtifactDeleteOutcome
{
    /// <summary>At least one replica entered its grace period.</summary>
    Requested = 1,
    /// <summary>Nothing was Available: a deletion is already waiting, running or retrying.</summary>
    AlreadyInProgress,
    /// <summary>Every replica is already gone.</summary>
    AlreadyDeleted
}
