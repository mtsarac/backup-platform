using BackupPlatform.Domain.Restores;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Lists;
using BackupPlatform.Application.Restores;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Restores;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

/// <summary>Test seam over both RestoreStore stores: tenant calls run bound to the tenant the call names, agent and worker calls on the platform-operations store.</summary>
internal sealed class RestoreStoreFacade(DbContextOptions<PlatformDbContext> options, IBackupStorageResolver? storage = null) : IRestoreStore, IRestorePlatformStore
{
    private readonly PostgreSqlRestorePlatformStore _platform = new(options, storage);
    private IRestoreStore For(TenantId tenantId) => new PostgreSqlRestoreStore(options, new MutableTenantContext(tenantId), storage);

    public Task<RestoreCreateResult> CreateAsync(RestoreCommand command, CancellationToken cancellationToken) => For(command.TenantId).CreateAsync(command, cancellationToken);
    public Task<RestoreOperation?> GetAsync(TenantId tenantId, Guid restoreId, CancellationToken cancellationToken) => For(tenantId).GetAsync(tenantId, restoreId, cancellationToken);
    public Task<RestorePage> ListAsync(TenantId tenantId, RestoreQuery query, CancellationToken cancellationToken) => For(tenantId).ListAsync(tenantId, query, cancellationToken);
    public Task<RestoreOperation?> RequestCancellationAsync(TenantId tenantId, Guid restoreId, DateTimeOffset now, CancellationToken cancellationToken, Guid? requestedByUserId = null) => For(tenantId).RequestCancellationAsync(tenantId, restoreId, now, cancellationToken, requestedByUserId);
    public Task<RestoreCleanupRequestResult> RequestCleanupAsync(TenantId tenantId, Guid restoreId, DateTimeOffset now, TimeSpan rollbackRetention, Guid? requestedByUserId, CancellationToken cancellationToken) => For(tenantId).RequestCleanupAsync(tenantId, restoreId, now, rollbackRetention, requestedByUserId, cancellationToken);
    public Task<RestoreClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, TimeSpan unclaimedExpiry, CancellationToken cancellationToken) => _platform.ClaimNextAsync(tenantId, agentId, now, leaseDuration, unclaimedExpiry, cancellationToken);
    public Task<RestoreLeaseOutcome> StartAsync(RestoreLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken) => _platform.StartAsync(command, leaseDuration, cancellationToken);
    public Task<RestoreLeaseOutcome> RenewLeaseAsync(RestoreLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken) => _platform.RenewLeaseAsync(command, leaseDuration, cancellationToken);
    public Task<BackupMutationResult> ReportProgressAsync(RestoreLeaseCommand command, RestoreProgress progress, CancellationToken cancellationToken) => _platform.ReportProgressAsync(command, progress, cancellationToken);
    public Task<BackupMutationResult> CompleteAsync(RestoreLeaseCommand command, RestoreCompletion completion, CancellationToken cancellationToken) => _platform.CompleteAsync(command, completion, cancellationToken);
    public Task<RestoreCleanupClaim?> ClaimCleanupAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => _platform.ClaimCleanupAsync(tenantId, agentId, now, leaseDuration, cancellationToken);
    public Task<BackupMutationResult> CompleteCleanupAsync(TenantId tenantId, AgentId agentId, Guid operationId, Guid leaseToken, bool removed, string? failureCode, DateTimeOffset now, CancellationToken cancellationToken) => _platform.CompleteCleanupAsync(tenantId, agentId, operationId, leaseToken, removed, failureCode, now, cancellationToken);
    public Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken) => _platform.ExpireStaleAsync(now, cancellationToken);
    public Task<int> ExpireUnclaimedAsync(DateTimeOffset now, TimeSpan maximumWait, CancellationToken cancellationToken) => _platform.ExpireUnclaimedAsync(now, maximumWait, cancellationToken);
}
