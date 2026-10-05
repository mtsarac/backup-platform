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
            _replicas.Any(existing => existing.StorageProvider == replica.StorageProvider && existing.StorageTarget == replica.StorageTarget && existing.Key == replica.Key))
            throw new ArgumentException("Replica does not match its artifact.");
        _replicas.Add(replica);
    }
    internal void Seal()
    {
        if (!_replicas.Any(replica => replica.VerificationState == "Verified"))
            throw new InvalidOperationException("A successful artifact requires a verified replica.");
        _sealed = true;
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
