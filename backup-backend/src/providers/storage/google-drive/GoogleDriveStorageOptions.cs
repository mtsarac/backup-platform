namespace BackupPlatform.StorageProviders.GoogleDrive;

public sealed class GoogleDriveStorageOptions
{
    public const int DefaultChunkSize = 1024 * 1024; // 1 MiB (multiple of 256 KiB)
    public const long DefaultMinStagingFreeBytes = 100 * 1024 * 1024; // 100 MB

    public GoogleDriveStorageOptions(
        string clientId,
        string clientSecret,
        Guid tenantId,
        Guid connectionId,
        string? folderId = null,
        string? stagingDirectory = null,
        int chunkSizeBytes = DefaultChunkSize,
        long minStagingFreeBytes = DefaultMinStagingFreeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID must not be empty.", nameof(tenantId));
        if (connectionId == Guid.Empty) throw new ArgumentException("Connection ID must not be empty.", nameof(connectionId));
        if (chunkSizeBytes <= 0 || chunkSizeBytes % (256 * 1024) != 0)
            throw new ArgumentException("Chunk size must be a positive multiple of 256 KiB.", nameof(chunkSizeBytes));
        if (minStagingFreeBytes < 0) throw new ArgumentOutOfRangeException(nameof(minStagingFreeBytes));

        ClientId = clientId;
        ClientSecret = clientSecret;
        TenantId = tenantId;
        ConnectionId = connectionId;
        FolderId = folderId;
        StagingDirectory = stagingDirectory ?? Path.Combine(Path.GetTempPath(), "backup-platform-staging");
        ChunkSizeBytes = chunkSizeBytes;
        MinStagingFreeBytes = minStagingFreeBytes;
    }

    public string ClientId { get; }
    public string ClientSecret { get; }
    public Guid TenantId { get; }
    public Guid ConnectionId { get; }
    public string? FolderId { get; }
    public string StagingDirectory { get; }
    public int ChunkSizeBytes { get; }
    public long MinStagingFreeBytes { get; }
}

public sealed record GoogleDriveCredential(string RefreshToken);
