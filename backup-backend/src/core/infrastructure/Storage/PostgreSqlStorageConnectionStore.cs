using BackupPlatform.Domain.Storage;
using BackupPlatform.Application.Lists;
using BackupPlatform.Application.Platform;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Infrastructure.Storage;

/// <summary>Storage connections of the request's tenant. Reads run on the filtered context; changes go through the platform-operations companion of their transaction.</summary>
public sealed class PostgreSqlStorageConnectionStore(
    DbContextOptions<PlatformDbContext> options, ITenantContext tenant, TimeProvider time, ILogger<PostgreSqlStorageConnectionStore> logger) : IStorageConnectionStore
{
    private readonly StorageConnectionStoreCore core = new(tenantId => PlatformDbContext.ForTenant(options, tenant, tenantId), time, logger);

    // A tenant never operates without a tenant scope: null would widen the lookup to every tenant.
    private static TenantId Scoped(TenantId? tenantScope) => tenantScope ?? throw new ArgumentNullException(nameof(tenantScope), "A tenant request names its tenant.");

    public Task<IReadOnlyList<StorageConnectionRecord>?> ListAsync(TenantId tenantId, CancellationToken cancellationToken) => core.ListAsync(tenantId, cancellationToken);

    public Task<StorageConnectionRecord?> FindAsync(Guid id, TenantId? tenantScope, CancellationToken cancellationToken) => core.FindAsync(id, Scoped(tenantScope), cancellationToken);

    public Task<StorageConnectionResult> CreateAsync(
        StorageConnection connection, int? maxPerTenant, StorageActor actor, IReadOnlyDictionary<string, object?> auditDetails, DateTimeOffset now, CancellationToken cancellationToken) =>
        core.CreateAsync(connection, maxPerTenant, actor, auditDetails, now, cancellationToken);

    public Task<StorageConnectionResult> MutateAsync(
        Guid id, TenantId? tenantScope, StorageMutation mutation, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        core.MutateAsync(id, Scoped(tenantScope), mutation, actor, now, cancellationToken);

    public Task<StorageConnectionResult> DeleteAsync(Guid id, TenantId? tenantScope, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        core.DeleteAsync(id, Scoped(tenantScope), actor, now, cancellationToken);
}
