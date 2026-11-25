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

namespace BackupPlatform.Infrastructure.Restores;

/// <summary>Agent-driven restore execution and the restore sweeps, on the unfiltered platform-operations context.</summary>
public sealed class PostgreSqlRestorePlatformStore(DbContextOptions<PlatformDbContext> options, IBackupStorageResolver? storage = null) : IRestorePlatformStore
{
    private readonly RestoreStoreCore core = new(_ => PlatformOperationsDbContext.Create(options), options, storage);

    public Task<RestoreOperation?> GetAsync(TenantId tenantId, Guid restoreId, CancellationToken cancellationToken) => core.GetAsync(tenantId, restoreId, cancellationToken);
    public Task<RestoreClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, TimeSpan unclaimedExpiry, CancellationToken cancellationToken) => core.ClaimNextAsync(tenantId, agentId, now, leaseDuration, unclaimedExpiry, cancellationToken);
    public Task<RestoreLeaseOutcome> StartAsync(RestoreLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken) => core.StartAsync(command, leaseDuration, cancellationToken);
    public Task<RestoreLeaseOutcome> RenewLeaseAsync(RestoreLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken) => core.RenewLeaseAsync(command, leaseDuration, cancellationToken);
    public Task<BackupMutationResult> ReportProgressAsync(RestoreLeaseCommand command, RestoreProgress progress, CancellationToken cancellationToken) => core.ReportProgressAsync(command, progress, cancellationToken);
    public Task<BackupMutationResult> CompleteAsync(RestoreLeaseCommand command, RestoreCompletion completion, CancellationToken cancellationToken) => core.CompleteAsync(command, completion, cancellationToken);
    public Task<RestoreCleanupClaim?> ClaimCleanupAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => core.ClaimCleanupAsync(tenantId, agentId, now, leaseDuration, cancellationToken);
    public Task<BackupMutationResult> CompleteCleanupAsync(TenantId tenantId, AgentId agentId, Guid operationId, Guid leaseToken, bool removed, string? failureCode, DateTimeOffset now, CancellationToken cancellationToken) => core.CompleteCleanupAsync(tenantId, agentId, operationId, leaseToken, removed, failureCode, now, cancellationToken);
    public Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken) => core.ExpireStaleAsync(now, cancellationToken);
    public Task<int> ExpireUnclaimedAsync(DateTimeOffset now, TimeSpan maximumWait, CancellationToken cancellationToken) => core.ExpireUnclaimedAsync(now, maximumWait, cancellationToken);
}
