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
        long minStagingFreeBytes = DefaultMinStagingFreeBytes,
        int maxRetryAttempts = 5,
        TimeSpan? maxRetryElapsed = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? retryBaseDelay = null,
        TimeSpan? maxRetryDelay = null,
        TimeSpan? uploadTimeout = null,
        int maxSessionRestarts = 2)
        : this(clientId, clientSecret, tenantId, connectionId, folderId, stagingDirectory, chunkSizeBytes, minStagingFreeBytes, maxRetryAttempts, maxRetryElapsed, requestTimeout, retryBaseDelay, maxRetryDelay, uploadTimeout, maxSessionRestarts, requireClientSecrets: true)
    {
    }

    public GoogleDriveStorageOptions(
        Guid tenantId,
        Guid connectionId,
        string? folderId = null,
        string? stagingDirectory = null,
        int chunkSizeBytes = DefaultChunkSize,
        long minStagingFreeBytes = DefaultMinStagingFreeBytes,
        int maxRetryAttempts = 5,
        TimeSpan? maxRetryElapsed = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? retryBaseDelay = null,
        TimeSpan? maxRetryDelay = null,
        TimeSpan? uploadTimeout = null,
        int maxSessionRestarts = 2)
        : this(string.Empty, string.Empty, tenantId, connectionId, folderId, stagingDirectory, chunkSizeBytes, minStagingFreeBytes, maxRetryAttempts, maxRetryElapsed, requestTimeout, retryBaseDelay, maxRetryDelay, uploadTimeout, maxSessionRestarts, requireClientSecrets: false)
    {
    }

    private GoogleDriveStorageOptions(
        string? clientId,
        string? clientSecret,
        Guid tenantId,
        Guid connectionId,
        string? folderId,
        string? stagingDirectory,
        int chunkSizeBytes,
        long minStagingFreeBytes,
        int maxRetryAttempts,
        TimeSpan? maxRetryElapsed,
        TimeSpan? requestTimeout,
        TimeSpan? retryBaseDelay,
        TimeSpan? maxRetryDelay,
        TimeSpan? uploadTimeout,
        int maxSessionRestarts,
        bool requireClientSecrets)
    {
        if (requireClientSecrets)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
            ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        }
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID must not be empty.", nameof(tenantId));
        if (connectionId == Guid.Empty) throw new ArgumentException("Connection ID must not be empty.", nameof(connectionId));
        if (chunkSizeBytes <= 0 || chunkSizeBytes % (256 * 1024) != 0)
            throw new ArgumentException("Chunk size must be a positive multiple of 256 KiB.", nameof(chunkSizeBytes));
        if (minStagingFreeBytes < 0) throw new ArgumentOutOfRangeException(nameof(minStagingFreeBytes));
        if (maxRetryAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxRetryAttempts));
        MaxRetryAttempts = maxRetryAttempts;
        MaxRetryElapsed = maxRetryElapsed ?? TimeSpan.FromMinutes(2);
        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        RetryBaseDelay = retryBaseDelay ?? TimeSpan.FromMilliseconds(500);
        MaxRetryDelay = maxRetryDelay ?? TimeSpan.FromSeconds(30);
        UploadTimeout = uploadTimeout ?? TimeSpan.FromHours(1);
        MaxSessionRestarts = maxSessionRestarts;
        if (MaxRetryElapsed <= TimeSpan.Zero || RequestTimeout <= TimeSpan.Zero
            || RetryBaseDelay < TimeSpan.Zero || MaxRetryDelay < RetryBaseDelay
            || UploadTimeout <= TimeSpan.Zero || MaxSessionRestarts < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetryElapsed), "Retry and timeout limits must be positive and ordered.");

        ClientId = clientId ?? string.Empty;
        ClientSecret = clientSecret ?? string.Empty;
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
    public int MaxRetryAttempts { get; }
    public TimeSpan MaxRetryElapsed { get; }
    public TimeSpan RequestTimeout { get; }
    public TimeSpan RetryBaseDelay { get; }
    public TimeSpan MaxRetryDelay { get; }
    public TimeSpan UploadTimeout { get; }
    public int MaxSessionRestarts { get; }
}

public sealed record GoogleDriveCredential(string RefreshToken);
