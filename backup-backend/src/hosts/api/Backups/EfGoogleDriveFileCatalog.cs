using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.StorageProviders.GoogleDrive;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Api.Backups;

public sealed class EfGoogleDriveFileCatalog(DbContextOptions<PlatformDbContext> options, TimeProvider time) : IGoogleDriveFileCatalog
{
    public async Task<GoogleDriveFileRecord?> FindAsync(Guid tenantId, Guid connectionId, string storageKey, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var meta = await context.GoogleDriveFileMetadata.AsNoTracking()
            .SingleOrDefaultAsync(m => m.TenantId == new TenantId(tenantId) && m.ConnectionId == connectionId && m.StorageKey == storageKey, cancellationToken);
        return meta is null ? null : new GoogleDriveFileRecord(meta.FileId, meta.ParentFolderId, meta.FileName, meta.SizeBytes, meta.Checksum, meta.Md5Checksum);
    }

    public async Task SaveAsync(Guid tenantId, Guid connectionId, string storageKey, GoogleDriveFileRecord record, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var identity = $"{tenantId:N}:{connectionId:N}:{storageKey}";
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))", cancellationToken);
        var existing = await context.GoogleDriveFileMetadata
            .SingleOrDefaultAsync(m => m.TenantId == new TenantId(tenantId) && m.ConnectionId == connectionId && m.StorageKey == storageKey, cancellationToken);
        if (existing is not null)
        {
            existing.Update(record.FileId, record.ParentFolderId, record.FileName, record.SizeBytes, record.Checksum, record.Md5Checksum, time.GetUtcNow());
        }
        else
        {
            context.GoogleDriveFileMetadata.Add(new GoogleDriveFileMetadata(
                Guid.NewGuid(), new TenantId(tenantId), connectionId, storageKey,
                record.FileId, record.ParentFolderId, record.FileName, record.SizeBytes,
                record.Checksum, record.Md5Checksum, time.GetUtcNow()));
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid tenantId, Guid connectionId, string storageKey, string expectedFileId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await context.GoogleDriveFileMetadata.Where(m =>
            m.TenantId == new TenantId(tenantId) && m.ConnectionId == connectionId
            && m.StorageKey == storageKey && m.FileId == expectedFileId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GoogleDriveCatalogEntry>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var rows = await context.GoogleDriveFileMetadata.AsNoTracking()
            .Where(m => m.TenantId == new TenantId(tenantId) && m.ConnectionId == connectionId)
            .OrderBy(m => m.StorageKey).ToListAsync(cancellationToken);
        return rows.Select(m => new GoogleDriveCatalogEntry(m.StorageKey,
            new GoogleDriveFileRecord(m.FileId, m.ParentFolderId, m.FileName, m.SizeBytes, m.Checksum, m.Md5Checksum))).ToArray();
    }

    public async Task ReportFindingAsync(Guid tenantId, Guid connectionId, GoogleDriveReconciliationFinding finding, CancellationToken cancellationToken)
    {
        if (finding.FileId is null || finding.Kind is not ("missing" or "ownership_conflict" or "integrity_mismatch")) return;
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var rows = await context.GoogleDriveFileMetadata.Where(m =>
            m.TenantId == new TenantId(tenantId) && m.ConnectionId == connectionId && m.FileId == finding.FileId).ToListAsync(cancellationToken);
        foreach (var row in rows) row.RecordDrift(finding.Kind, time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken);
    }
}
