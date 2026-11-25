using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Flags;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// Test seam over both run stores: tenant calls run through a tenant-bound store per tenant named by the call (as a request of that tenant would),
/// agent and worker calls through the platform-operations store.
/// </summary>
internal sealed class RunStoreFacade(DbContextOptions<PlatformDbContext> options, IBackupAdmissionPolicy admission, BackupExecutionOptions? limits = null, Func<double>? jitter = null,
    NotificationOptions? notifications = null, ILogger<PostgreSqlBackupRunPlatformStore>? log = null, FeatureFlags? flags = null) : IBackupRunStore, IBackupRunPlatformStore
{
    private readonly PostgreSqlBackupRunPlatformStore _platform = new(options, limits, jitter, notifications, log, flags);
    private IBackupRunStore For(TenantId tenantId) => new PostgreSqlBackupRunStore(options, new MutableTenantContext(tenantId), admission, flags);

    public Task<BackupJob?> CreateJobAsync(BackupJob job, CancellationToken cancellationToken, UserId? actor = null) => For(job.TenantId).CreateJobAsync(job, cancellationToken, actor);
    public Task<BackupJob?> FindJobByIdempotencyKeyAsync(TenantId tenantId, string idempotencyKey, CancellationToken cancellationToken) => For(tenantId).FindJobByIdempotencyKeyAsync(tenantId, idempotencyKey, cancellationToken);
    public Task<BackupJob?> GetJobAsync(TenantId tenantId, Guid jobId, CancellationToken cancellationToken) => For(tenantId).GetJobAsync(tenantId, jobId, cancellationToken);
    public Task<BackupJob?> UpdateJobAsync(TenantId tenantId, Guid jobId, BackupJobUpdate update, CancellationToken cancellationToken, UserId? actor = null, DateTimeOffset? now = null) => For(tenantId).UpdateJobAsync(tenantId, jobId, update, cancellationToken, actor, now);
    public Task<BackupStartResult> StartRunAsync(TenantId tenantId, Guid jobId, string idempotencyKey, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null) => For(tenantId).StartRunAsync(tenantId, jobId, idempotencyKey, now, cancellationToken, actor);
    public Task<IReadOnlyList<BackupRun>> ListRunsAsync(TenantId tenantId, Guid? jobId, int limit, CancellationToken cancellationToken) => For(tenantId).ListRunsAsync(tenantId, jobId, limit, cancellationToken);
    public Task<BackupRun?> GetRunAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken) => For(tenantId).GetRunAsync(tenantId, runId, cancellationToken);
    public Task<BackupRun?> RequestCancellationAsync(TenantId tenantId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null) => For(tenantId).RequestCancellationAsync(tenantId, runId, now, cancellationToken, actor);
    public Task<BackupRetryResult> RetryRunAsync(TenantId tenantId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null) => For(tenantId).RetryRunAsync(tenantId, runId, now, cancellationToken, actor);
    public Task<BackupMutationResult> SetPauseAsync(TenantId tenantId, Guid runId, bool resume, DateTimeOffset now, CancellationToken cancellationToken, UserId? actor = null) => For(tenantId).SetPauseAsync(tenantId, runId, resume, now, cancellationToken, actor);
    public Task<IReadOnlyList<TransferCheckpoint>> GetCheckpointsAsync(TenantId tenantId, Guid attemptId, CancellationToken cancellationToken) => _platform.GetCheckpointsAsync(tenantId, attemptId, cancellationToken);

    public Task<TenantId?> FindRunTenantAsync(Guid runId, CancellationToken cancellationToken) => _platform.FindRunTenantAsync(runId, cancellationToken);
    public Task<BackupClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => _platform.ClaimNextAsync(tenantId, agentId, now, leaseDuration, cancellationToken);
    public Task<BackupClaim?> GetAttemptAsync(TenantId tenantId, AgentId agentId, Guid attemptId, CancellationToken cancellationToken) => _platform.GetAttemptAsync(tenantId, agentId, attemptId, cancellationToken);
    public Task<BackupMutationResult> StartAttemptAsync(BackupLeaseCommand command, CancellationToken cancellationToken) => _platform.StartAttemptAsync(command, cancellationToken);
    public Task<BackupQuotaReservationResult> AdjustQuotaReservationAsync(BackupLeaseCommand command, long sizeBytes, CancellationToken cancellationToken) => _platform.AdjustQuotaReservationAsync(command, sizeBytes, cancellationToken);
    public Task<BackupLeaseResult?> RenewLeaseAsync(BackupLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken) => _platform.RenewLeaseAsync(command, leaseDuration, cancellationToken);
    public Task<BackupMutationResult> RecordTransferAsync(BackupLeaseCommand command, BackupTransferIntent transfer, CancellationToken cancellationToken) => _platform.RecordTransferAsync(command, transfer, cancellationToken);
    public Task<BackupMutationResult> ReportProgressAsync(BackupLeaseCommand command, BackupProgress progress, CancellationToken cancellationToken) => _platform.ReportProgressAsync(command, progress, cancellationToken);
    public Task<BackupMutationResult> CompleteAttemptAsync(BackupLeaseCommand command, BackupPlatform.Application.Backups.BackupCompletion completion, CancellationToken cancellationToken) => _platform.CompleteAttemptAsync(command, completion, cancellationToken);
    public Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken) => _platform.ExpireStaleAsync(now, cancellationToken);
    public Task<IReadOnlyList<BackupRetryStarted>> StartDueRetriesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) => _platform.StartDueRetriesAsync(now, limit, cancellationToken);
    public Task<IReadOnlyList<BackupRetryStarted>> ResumeDueNetworkPausesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) => _platform.ResumeDueNetworkPausesAsync(now, limit, cancellationToken);
}
