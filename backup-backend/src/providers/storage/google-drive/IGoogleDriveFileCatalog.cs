using System.Collections.Concurrent;

namespace BackupPlatform.StorageProviders.GoogleDrive;

public sealed record GoogleDriveFileRecord(
    string FileId,
    string ParentFolderId,
    string FileName,
    long SizeBytes,
    string? Checksum,
    string? Md5Checksum);

public interface IGoogleDriveFileCatalog
{
    Task<GoogleDriveFileRecord?> FindAsync(Guid tenantId, Guid connectionId, string storageKey, CancellationToken cancellationToken);

    Task SaveAsync(Guid tenantId, Guid connectionId, string storageKey, GoogleDriveFileRecord record, CancellationToken cancellationToken);

    Task DeleteAsync(Guid tenantId, Guid connectionId, string storageKey, CancellationToken cancellationToken);
}

public sealed class InMemoryGoogleDriveFileCatalog : IGoogleDriveFileCatalog
{
    private readonly ConcurrentDictionary<(Guid TenantId, Guid ConnectionId, string Key), GoogleDriveFileRecord> _records = new();

    public Task<GoogleDriveFileRecord?> FindAsync(Guid tenantId, Guid connectionId, string storageKey, CancellationToken cancellationToken)
    {
        _records.TryGetValue((tenantId, connectionId, storageKey), out var record);
        return Task.FromResult(record);
    }

    public Task SaveAsync(Guid tenantId, Guid connectionId, string storageKey, GoogleDriveFileRecord record, CancellationToken cancellationToken)
    {
        _records[(tenantId, connectionId, storageKey)] = record;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid tenantId, Guid connectionId, string storageKey, CancellationToken cancellationToken)
    {
        _records.TryRemove((tenantId, connectionId, storageKey), out _);
        return Task.CompletedTask;
    }
}
