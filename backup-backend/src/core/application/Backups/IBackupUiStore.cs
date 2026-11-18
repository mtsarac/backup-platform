using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

public sealed record BackupRunQuery(int Page = 1, int PageSize = 25, Guid? JobId = null, Guid? AgentId = null, string? Status = null, DateTimeOffset? From = null, DateTimeOffset? To = null, string? Trigger = null);
public sealed record BackupRunPage(IReadOnlyList<BackupRunView> Items, long TotalCount, int Page, int PageSize);
public sealed record DashboardJobCounts(int Total, int Enabled, int Scheduled);
public sealed record DashboardRunCounts(int Active, int FailedLast24Hours, int SucceededLast24Hours, int Running, int RetryPending, int CancellationRequested, int StorageFailedLast24Hours, int ReplicationInsufficientLast24Hours = 0);
public sealed record DashboardArtifactCounts(int Total, long SizeBytes, int DeletePending, int DeleteFailed);
public sealed record BackupDashboardView(DashboardJobCounts Jobs, DashboardRunCounts Runs, DashboardArtifactCounts Artifacts, IReadOnlyList<BackupRunView> RecentRuns, IReadOnlyList<BackupJobView> UpcomingJobs);

/// <summary>Tenant-qualified projections for browser history; never returns credentials, leases or tracked entities.</summary>
public interface IBackupUiStore
{
    /// <summary>Archived jobs are left out unless asked for: they only matter as a name to filter history by.</summary>
    Task<IReadOnlyList<BackupJobView>> ListJobsAsync(TenantId tenantId, DateTimeOffset now, bool includeArchived, CancellationToken cancellationToken);
    /// <summary>An archived job is still readable: its history is reached through it.</summary>
    Task<BackupJobView?> GetJobAsync(TenantId tenantId, Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<BackupRunPage> ListRunsAsync(TenantId tenantId, BackupRunQuery query, CancellationToken cancellationToken);
    Task<BackupRunView?> GetRunAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken);
    Task<BackupDashboardView> GetDashboardAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>The last 14 local days of every non-archived job, cut at the given zone's midnights.</summary>
    Task<BackupHealthTimelineView> GetHealthTimelineAsync(TenantId tenantId, TimeZoneInfo zone, DateTimeOffset now, CancellationToken cancellationToken);
}
