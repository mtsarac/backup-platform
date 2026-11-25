using BackupPlatform.Domain.Storage;
using BackupPlatform.Application.Lists;
using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Infrastructure.Storage;

/// <summary>Storage connections for platform administration and agent-driven lookups: unfiltered by tenant, a null scope means every tenant.</summary>
public sealed class PostgreSqlStorageConnectionPlatformStore(
    DbContextOptions<PlatformDbContext> options, TimeProvider time, ILogger<PostgreSqlStorageConnectionPlatformStore> logger) : IStorageConnectionPlatformStore
{
    private readonly StorageConnectionStoreCore core = new(_ => PlatformOperationsDbContext.Create(options), time, logger);

    public Task<IReadOnlyList<StorageConnectionRecord>?> ListAsync(TenantId tenantId, CancellationToken cancellationToken) => core.ListAsync(tenantId, cancellationToken);

    public Task<PagedResult<StorageConnectionRecord>> SearchAsync(StorageConnectionFilter filter, ListQuery query, CancellationToken cancellationToken) => core.SearchAsync(filter, query, cancellationToken);

    public Task<StorageConnectionRecord?> FindAsync(Guid id, TenantId? tenantScope, CancellationToken cancellationToken) => core.FindAsync(id, tenantScope, cancellationToken);

    public Task<StorageConnectionResult> CreateAsync(
        StorageConnection connection, int? maxPerTenant, StorageActor actor, IReadOnlyDictionary<string, object?> auditDetails, DateTimeOffset now, CancellationToken cancellationToken) =>
        core.CreateAsync(connection, maxPerTenant, actor, auditDetails, now, cancellationToken);

    public Task<StorageConnectionResult> MutateAsync(
        Guid id, TenantId? tenantScope, StorageMutation mutation, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        core.MutateAsync(id, tenantScope, mutation, actor, now, cancellationToken);

    public Task<StorageConnectionResult> DeleteAsync(Guid id, TenantId? tenantScope, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        core.DeleteAsync(id, tenantScope, actor, now, cancellationToken);
}
