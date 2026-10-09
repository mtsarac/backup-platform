using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Storage;

public sealed class GoogleDriveFileMetadata : ITenantOwned
{
    private GoogleDriveFileMetadata()
    {
        TenantId = null!;
        StorageKey = null!;
        FileId = null!;
        ParentFolderId = null!;
        FileName = null!;
    }

    public GoogleDriveFileMetadata(
        Guid id,
        TenantId tenantId,
        Guid connectionId,
        string storageKey,
        string fileId,
        string parentFolderId,
        string fileName,
        long sizeBytes,
        string? checksum,
        string? md5Checksum,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("ID must not be empty.", nameof(id));
        ArgumentNullException.ThrowIfNull(tenantId);
        if (connectionId == Guid.Empty) throw new ArgumentException("Connection ID must not be empty.", nameof(connectionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentFolderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (sizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));

        Id = id;
        TenantId = tenantId;
        ConnectionId = connectionId;
        StorageKey = storageKey;
        FileId = fileId;
        ParentFolderId = parentFolderId;
        FileName = fileName;
        SizeBytes = sizeBytes;
        Checksum = checksum?.ToLowerInvariant();
        Md5Checksum = md5Checksum?.ToLowerInvariant();
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public string StorageKey { get; private set; }
    public string FileId { get; private set; }
    public string ParentFolderId { get; private set; }
    public string FileName { get; private set; }
    public long SizeBytes { get; private set; }
    public string? Checksum { get; private set; }
    public string? Md5Checksum { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(string fileId, string parentFolderId, string fileName, long sizeBytes, string? checksum, string? md5Checksum, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentFolderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (sizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        FileId = fileId;
        ParentFolderId = parentFolderId;
        FileName = fileName;
        SizeBytes = sizeBytes;
        Checksum = checksum?.ToLowerInvariant();
        Md5Checksum = md5Checksum?.ToLowerInvariant();
        UpdatedAt = now.ToUniversalTime();
    }
}
