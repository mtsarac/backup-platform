using System.Collections.Concurrent;

namespace BackupPlatform.StorageProviders.GoogleDrive;

public sealed record GoogleDriveFileRecord(
    string FileId,
    string ParentFolderId,
    string FileName,
    long SizeBytes,
    string? Checksum,
    string? Md5Checksum);

public sealed record GoogleDriveCatalogEntry(string StorageKey, GoogleDriveFileRecord Record);

public interface IGoogleDriveFileCatalog
{
    Task<GoogleDriveFileRecord?> FindAsync(Guid tenantId, Guid connectionId, string storageKey, CancellationToken cancellationToken);

    Task SaveAsync(Guid tenantId, Guid connectionId, string storageKey, GoogleDriveFileRecord record, CancellationToken cancellationToken);

    Task DeleteAsync(Guid tenantId, Guid connectionId, string storageKey, string expectedFileId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GoogleDriveCatalogEntry>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    Task ReportFindingAsync(Guid tenantId, Guid connectionId, GoogleDriveReconciliationFinding finding, CancellationToken cancellationToken);
}

public sealed class InMemoryGoogleDriveFileCatalog : IGoogleDriveFileCatalog
{
    private readonly ConcurrentDictionary<(Guid TenantId, Guid ConnectionId, string Key), GoogleDriveFileRecord> _records = new();
    public ConcurrentQueue<GoogleDriveReconciliationFinding> Findings { get; } = new();

    public Task<GoogleDriveFileRecord?> FindAsync(Guid tenantId, Guid connectionId, string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _records.TryGetValue((tenantId, connectionId, storageKey), out var record);
        return Task.FromResult(record);
    }

    public Task SaveAsync(Guid tenantId, Guid connectionId, string storageKey, GoogleDriveFileRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _records[(tenantId, connectionId, storageKey)] = record;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid tenantId, Guid connectionId, string storageKey, string expectedFileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = (tenantId, connectionId, storageKey);
        if (_records.TryGetValue(key, out var record) && record.FileId == expectedFileId)
            _records.TryRemove(new KeyValuePair<(Guid, Guid, string), GoogleDriveFileRecord>(key, record));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GoogleDriveCatalogEntry>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<GoogleDriveCatalogEntry> entries = _records
            .Where(r => r.Key.TenantId == tenantId && r.Key.ConnectionId == connectionId)
            .Select(r => new GoogleDriveCatalogEntry(r.Key.Key, r.Value)).ToArray();
        return Task.FromResult(entries);
    }

    public Task ReportFindingAsync(Guid tenantId, Guid connectionId, GoogleDriveReconciliationFinding finding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Findings.Enqueue(finding);
        return Task.CompletedTask;
    }
}
