using BackupPlatform.Domain.Backups;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

/// <summary>Test seam over both UploadBandwidthStore stores: tenant calls run bound to the tenant the call names, agent and worker calls on the platform-operations store.</summary>
internal sealed class UploadBandwidthStoreFacade(DbContextOptions<PlatformDbContext> options) : IUploadBandwidthStore, IUploadBandwidthPlatformStore
{
    private readonly PostgreSqlUploadBandwidthPlatformStore _platform = new(options);
    private IUploadBandwidthStore For(TenantId tenantId) => new PostgreSqlUploadBandwidthStore(options, new MutableTenantContext(tenantId));

    public Task<UploadBandwidthSettings> GetAsync(TenantId tenantId, CancellationToken cancellationToken) => For(tenantId).GetAsync(tenantId, cancellationToken);
    public Task<bool> SetAsync(TenantId tenantId, UploadBandwidthScope scope, Guid scopeId, long? bytesPerSecond, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).SetAsync(tenantId, scope, scopeId, bytesPerSecond, actor, now, cancellationToken);
    public Task<IReadOnlyList<BandwidthProfileView>> ListProfilesAsync(TenantId tenantId, CancellationToken cancellationToken) => For(tenantId).ListProfilesAsync(tenantId, cancellationToken);
    public Task<BandwidthProfileView?> GetProfileAsync(TenantId tenantId, Guid profileId, CancellationToken cancellationToken) => For(tenantId).GetProfileAsync(tenantId, profileId, cancellationToken);
    public Task<BandwidthProfileView> SaveProfileAsync(TenantId tenantId, Guid? profileId, string name, string timeZoneId, IEnumerable<TransferScheduleWindowDto> windows, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).SaveProfileAsync(tenantId, profileId, name, timeZoneId, windows, actor, now, cancellationToken);
    public Task<bool> DeleteProfileAsync(TenantId tenantId, Guid profileId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).DeleteProfileAsync(tenantId, profileId, actor, now, cancellationToken);
    public Task<bool> AssignProfileAsync(TenantId tenantId, UploadBandwidthScope scope, Guid scopeId, Guid? profileId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).AssignProfileAsync(tenantId, scope, scopeId, profileId, actor, now, cancellationToken);
    public Task<AttemptUploadLimits?> GrantAsync(BackupLeaseCommand lease, UploadSliceAction action, TimeSpan grantTtl, CancellationToken cancellationToken) => _platform.GrantAsync(lease, action, grantTtl, cancellationToken);
}
