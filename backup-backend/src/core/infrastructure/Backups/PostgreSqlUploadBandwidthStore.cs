using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using BackupPlatform.Application.Tenancy;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Upload limits and bandwidth profiles of the request's tenant on the filtered context.</summary>
public sealed class PostgreSqlUploadBandwidthStore(DbContextOptions<PlatformDbContext> options, ITenantContext tenant) : IUploadBandwidthStore
{
    private readonly UploadBandwidthStoreCore core = new(tenantId => PlatformDbContext.ForTenant(options, tenant, tenantId));

    public Task<UploadBandwidthSettings> GetAsync(TenantId tenantId, CancellationToken cancellationToken) => core.GetAsync(tenantId, cancellationToken);
    public Task<bool> SetAsync(TenantId tenantId, UploadBandwidthScope scope, Guid scopeId, long? bytesPerSecond, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) => core.SetAsync(tenantId, scope, scopeId, bytesPerSecond, actor, now, cancellationToken);
    public Task<IReadOnlyList<BandwidthProfileView>> ListProfilesAsync(TenantId tenantId, CancellationToken cancellationToken) => core.ListProfilesAsync(tenantId, cancellationToken);
    public Task<BandwidthProfileView?> GetProfileAsync(TenantId tenantId, Guid profileId, CancellationToken cancellationToken) => core.GetProfileAsync(tenantId, profileId, cancellationToken);
    public Task<BandwidthProfileView> SaveProfileAsync(TenantId tenantId, Guid? profileId, string name, string timeZoneId, IEnumerable<TransferScheduleWindowDto> windows, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) => core.SaveProfileAsync(tenantId, profileId, name, timeZoneId, windows, actor, now, cancellationToken);
    public Task<bool> DeleteProfileAsync(TenantId tenantId, Guid profileId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) => core.DeleteProfileAsync(tenantId, profileId, actor, now, cancellationToken);
    public Task<bool> AssignProfileAsync(TenantId tenantId, UploadBandwidthScope scope, Guid scopeId, Guid? profileId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) => core.AssignProfileAsync(tenantId, scope, scopeId, profileId, actor, now, cancellationToken);
}
