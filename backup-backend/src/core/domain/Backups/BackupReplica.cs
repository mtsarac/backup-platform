using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

public sealed class BackupReplica : ITenantOwned
{
    private BackupReplica() { TenantId = null!; StorageProvider = null!; StorageTarget = null!; Key = null!; Checksum = null!; ChecksumAlgorithm = null!; VerificationState = null!; VerificationMethod = null!; }
    public BackupReplica(Guid id, TenantId tenantId, Guid artifactId, string storageProvider, string storageTarget,
        string key, long sizeBytes, string checksum, string checksumAlgorithm, string verificationState,
        string verificationMethod, DateTimeOffset createdAt, DateTimeOffset? verifiedAt)
    {
        BackupGuard.Id(id);
        BackupGuard.Id(artifactId);
        ArgumentNullException.ThrowIfNull(tenantId);
        BackupGuard.Reference(storageProvider);
        BackupGuard.Text(storageTarget, 512);
        BackupGuard.RelativeKey(key);
        BackupGuard.Checksum(checksum, checksumAlgorithm);
        BackupGuard.Text(verificationMethod, 64);
        if (sizeBytes <= 0 || verificationState is not ("Verified" or "Unverified" or "Failed"))
            throw new ArgumentException("Invalid replica metadata.");
        if ((verificationState == "Verified") != verifiedAt.HasValue || verifiedAt < createdAt)
            throw new ArgumentException("Invalid replica verification timestamp.");
        Id = id;
        TenantId = tenantId;
        ArtifactId = artifactId;
        StorageProvider = storageProvider;
        StorageTarget = storageTarget;
        Key = key;
        SizeBytes = sizeBytes;
        Checksum = checksum.ToLowerInvariant();
        ChecksumAlgorithm = checksumAlgorithm;
        VerificationState = verificationState;
        VerificationMethod = verificationMethod;
        CreatedAt = BackupGuard.Timestamp(createdAt);
        VerifiedAt = verifiedAt is { } verified ? BackupGuard.Timestamp(verified) : null;
    }
    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid ArtifactId { get; private set; }
    public string StorageProvider { get; private set; }
    public string Status { get; private set; } = "Available";
    public string StorageTarget { get; private set; }
    public string Key { get; private set; }
    public long SizeBytes { get; private set; }
    public string Checksum { get; private set; }
    public string ChecksumAlgorithm { get; private set; }
    public string VerificationState { get; private set; }
    public string VerificationMethod { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    internal bool SameResult(BackupReplica other) => StorageProvider == other.StorageProvider && StorageTarget == other.StorageTarget &&
        Key == other.Key && SizeBytes == other.SizeBytes && Checksum == other.Checksum && ChecksumAlgorithm == other.ChecksumAlgorithm &&
        VerificationState == other.VerificationState && VerificationMethod == other.VerificationMethod && CreatedAt == other.CreatedAt && VerifiedAt == other.VerifiedAt;
}
