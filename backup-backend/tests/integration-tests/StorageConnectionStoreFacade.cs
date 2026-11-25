using BackupPlatform.Domain.Storage;
using BackupPlatform.Application.Lists;
using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Storage;
using BackupPlatform.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests;

/// <summary>Test seam over both connection stores: a call with a tenant scope runs bound to that tenant, a call without one on the platform-operations store.</summary>
internal sealed class StorageConnectionStoreFacade(DbContextOptions<PlatformDbContext> options, TimeProvider? time = null) : IStorageConnectionStore, IStorageConnectionPlatformStore
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly PostgreSqlStorageConnectionPlatformStore _platform = new(options, time ?? TimeProvider.System, NullLogger<PostgreSqlStorageConnectionPlatformStore>.Instance);
    private IStorageConnectionOperations For(TenantId? tenantScope) => tenantScope is { } tenantId
        ? new PostgreSqlStorageConnectionStore(options, new MutableTenantContext(tenantId), _time, NullLogger<PostgreSqlStorageConnectionStore>.Instance)
        : _platform;

    public Task<IReadOnlyList<StorageConnectionRecord>?> ListAsync(TenantId tenantId, CancellationToken cancellationToken) => For(tenantId).ListAsync(tenantId, cancellationToken);
    public Task<PagedResult<StorageConnectionRecord>> SearchAsync(StorageConnectionFilter filter, ListQuery query, CancellationToken cancellationToken) => _platform.SearchAsync(filter, query, cancellationToken);
    public Task<StorageConnectionRecord?> FindAsync(Guid id, TenantId? tenantScope, CancellationToken cancellationToken) => For(tenantScope).FindAsync(id, tenantScope, cancellationToken);
    public Task<StorageConnectionResult> CreateAsync(StorageConnection connection, int? maxPerTenant, StorageActor actor, IReadOnlyDictionary<string, object?> auditDetails, DateTimeOffset now, CancellationToken cancellationToken) =>
        For(connection.TenantId).CreateAsync(connection, maxPerTenant, actor, auditDetails, now, cancellationToken);
    public Task<StorageConnectionResult> MutateAsync(Guid id, TenantId? tenantScope, StorageMutation mutation, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        For(tenantScope).MutateAsync(id, tenantScope, mutation, actor, now, cancellationToken);
    public Task<StorageConnectionResult> DeleteAsync(Guid id, TenantId? tenantScope, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        For(tenantScope).DeleteAsync(id, tenantScope, actor, now, cancellationToken);
}
