using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Application.Backups;

public sealed record CreateBackupShareRequest(
    Guid? ArtifactId = null,
    int? DurationHours = null,
    TimeSpan? Duration = null,
    string? Reason = null);

public sealed record CreatePlatformBackupShareRequest(
    Guid TenantId,
    Guid ArtifactId,
    int? DurationHours = null,
    TimeSpan? Duration = null,
    string? Reason = null);

public sealed record ExtendBackupShareRequest(
    DateTimeOffset? ExpiresAtUtc = null,
    int? DurationHours = null,
    int? AdditionalHours = null,
    TimeSpan? Duration = null,
    TimeSpan? Extension = null);

public sealed record RevokeBackupShareRequest(
    string? Reason = null);

public enum BackupShareModifyOutcome
{
    Success = 1,
    NotFound,
    Forbidden,
    Expired,
    AlreadyRevoked,
    InvalidExpiry
}

public sealed record BackupShareModifyResult(
    BackupShareModifyOutcome Outcome,
    BackupShareView? Share = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);
public enum BackupShareCreateOutcome
{
    Success = 1,
    ArtifactNotFound,
    NoDownloadableReplica,
    Forbidden
}

public sealed record BackupShareCreateResult(
    BackupShareCreateOutcome Outcome,
    BackupShareView? Share = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record BackupShareView(
    Guid Id,
    Guid TenantId,
    Guid ArtifactId,
    Guid CreatedByUserId,
    string CreatedByRole,
    string? Reason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    Guid? RevokedByUserId,
    string Status,
    string? ArtifactName = null,
    string? CreatedByDisplayName = null,
    string? TenantName = null);

public sealed record DownloadAttemptFilter(
    Guid? ShareId = null,
    TenantId? TenantId = null,
    Guid? ArtifactId = null,
    Guid? InitiatingUserId = null,
    string? Result = null);

public sealed record DownloadAttemptView(
    Guid Id,
    Guid? ShareId,
    Guid TenantId,
    string? TenantName,
    Guid ArtifactId,
    string? ArtifactName,
    Guid InitiatingUserId,
    string? InitiatingUserDisplayName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string Result,
    long BytesTransferred,
    string? FailureReason,
    bool Decrypted)
{
    public Guid UserId => InitiatingUserId;
    public Guid InitiatingUser => InitiatingUserId;
    public string? UserDisplayName => InitiatingUserDisplayName;
    public DateTimeOffset StartTime => StartedAtUtc;
    public DateTimeOffset? EndTime => EndedAtUtc;
    public string Status => Result;
    public long TransferredBytes => BytesTransferred;
}

public sealed record AuditRetentionSettingsView(
    int RetentionDays,
    int DefaultRetentionDays = PlatformAuditRetentionSettings.DefaultRetentionDays,
    int MinRetentionDays = PlatformAuditRetentionSettings.MinimumRetentionDays,
    int MaxRetentionDays = PlatformAuditRetentionSettings.MaximumRetentionDays,
    DateTimeOffset? UpdatedAtUtc = null,
    Guid? UpdatedByUserId = null)
{
    public DateTimeOffset? UpdatedAt => UpdatedAtUtc;
}

public sealed record UpdateAuditRetentionRequest(
    int? RetentionDays = null,
    int? Days = null);

public enum SaveAuditRetentionOutcome
{
    Success = 1,
    Forbidden,
    InvalidRetentionDays
}

public sealed record SaveAuditRetentionResult(
    SaveAuditRetentionOutcome Outcome,
    AuditRetentionSettingsView? Settings = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record AuditHistoryPruneResult(
    int AuditEventsRemoved,
    int DownloadSessionsRemoved,
    int SharesRemoved)
{
    public int TotalRemoved => AuditEventsRemoved + DownloadSessionsRemoved + SharesRemoved;
}

public interface IBackupShareStore
{
    Task<BackupShareCreateResult> CreateAsync(
        TenantId tenantId,
        Guid artifactId,
        UserId createdByUserId,
        UserRole createdByRole,
        TimeSpan duration,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<BackupShareModifyResult> ExtendAsync(
        TenantId tenantId,
        Guid shareId,
        UserId actor,
        UserRole actorRole,
        ExtendBackupShareRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<BackupShareModifyResult> RevokeAsync(
        TenantId tenantId,
        Guid shareId,
        UserId actor,
        UserRole actorRole,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<BackupShareView?> GetAsync(
        TenantId tenantId,
        Guid shareId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BackupShareView>> ListAsync(
        TenantId tenantId,
        Guid? artifactId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BackupShareView>> ListPlatformAsync(
        TenantId? tenantId,
        BackupShareStatus? status,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<BackupShareView?> GetPlatformAsync(
        Guid shareId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadAttemptView>> ListDownloadAttemptsAsync(
        DownloadAttemptFilter filter,
        bool tenantScoped,
        CancellationToken cancellationToken);

    Task<AuditRetentionSettingsView> GetAuditRetentionAsync(
        CancellationToken cancellationToken);

    Task<AuditRetentionSettingsView> SaveAuditRetentionAsync(
        int retentionDays,
        UserId actor,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<AuditHistoryPruneResult> PruneHistoryAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken,
        int? retentionDaysOverride = null);
}
