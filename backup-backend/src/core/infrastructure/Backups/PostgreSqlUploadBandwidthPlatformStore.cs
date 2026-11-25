using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Agent-driven upload grants on the unfiltered platform-operations context.</summary>
public sealed class PostgreSqlUploadBandwidthPlatformStore(DbContextOptions<PlatformDbContext> options) : IUploadBandwidthPlatformStore
{
    private readonly UploadBandwidthStoreCore core = new(_ => PlatformOperationsDbContext.Create(options));

    public Task<AttemptUploadLimits?> GrantAsync(BackupLeaseCommand lease, UploadSliceAction action, TimeSpan grantTtl, CancellationToken cancellationToken) => core.GrantAsync(lease, action, grantTtl, cancellationToken);
}
