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
    }

    public async Task DeleteAsync(Guid tenantId, Guid connectionId, string storageKey, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var existing = await context.GoogleDriveFileMetadata
            .SingleOrDefaultAsync(m => m.TenantId == new TenantId(tenantId) && m.ConnectionId == connectionId && m.StorageKey == storageKey, cancellationToken);
        if (existing is not null)
        {
            context.GoogleDriveFileMetadata.Remove(existing);
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
