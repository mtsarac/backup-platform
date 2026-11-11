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
}
