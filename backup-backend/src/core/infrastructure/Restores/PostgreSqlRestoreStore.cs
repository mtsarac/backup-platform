using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Backups;
using System.Linq.Expressions;
using System.Text.Json.Nodes;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Lists;
using BackupPlatform.Application.Restores;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Lists;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.EntityFrameworkCore;
using BackupPlatform.Application.Tenancy;

namespace BackupPlatform.Infrastructure.Restores;

/// <summary>Restores of the request's tenant. Every context it opens is the filtered one of that tenant.</summary>
public sealed class PostgreSqlRestoreStore(DbContextOptions<PlatformDbContext> options, ITenantContext tenant, IBackupStorageResolver? storage = null) : IRestoreStore
{
    private readonly RestoreStoreCore core = new(tenantId => PlatformDbContext.ForTenant(options, tenant, tenantId), options, storage);

    public Task<RestoreCreateResult> CreateAsync(RestoreCommand command, CancellationToken cancellationToken) => core.CreateAsync(command, cancellationToken);
    public Task<RestoreOperation?> GetAsync(TenantId tenantId, Guid restoreId, CancellationToken cancellationToken) => core.GetAsync(tenantId, restoreId, cancellationToken);
    public Task<RestorePage> ListAsync(TenantId tenantId, RestoreQuery query, CancellationToken cancellationToken) => core.ListAsync(tenantId, query, cancellationToken);
    public Task<RestoreOperation?> RequestCancellationAsync(TenantId tenantId, Guid restoreId, DateTimeOffset now, CancellationToken cancellationToken, Guid? requestedByUserId = null) => core.RequestCancellationAsync(tenantId, restoreId, now, cancellationToken, requestedByUserId);
    public Task<RestoreCleanupRequestResult> RequestCleanupAsync(TenantId tenantId, Guid restoreId, DateTimeOffset now, TimeSpan rollbackRetention, Guid? requestedByUserId, CancellationToken cancellationToken) => core.RequestCleanupAsync(tenantId, restoreId, now, rollbackRetention, requestedByUserId, cancellationToken);
}
