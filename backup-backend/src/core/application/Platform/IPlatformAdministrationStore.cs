using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Application.Platform;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, long TotalCount, int Page, int PageSize);

public sealed record PlatformTenantItem(TenantId TenantId, string Name, int MemberCount, int AgentCount, bool EnrollmentEnabled);

public sealed record PlatformTenantDetail(
    TenantId TenantId, string Name, int MemberCount, int EnabledBackupJobs, bool EnrollmentEnabled, DateTimeOffset? EnrollmentChangedAt);

public sealed record PlatformUserItem(UserId UserId, string DisplayName, string? LoginIdentifier, bool IsPlatformAdmin, int MembershipCount);

public sealed record PlatformMembershipItem(UserId UserId, string DisplayName, string? LoginIdentifier, TenantRole Role);

public sealed record PlatformAuditItem(
    Guid Id, DateTimeOffset At, UserId ActorUserId, string? ActorDisplayName, AuditActorKind ActorKind, string Action,
    TenantId? TenantId, string? TenantName, string TargetType, string TargetId, string DetailsJson);

/// <summary>Counts that need no agent liveness knowledge; agent counts are assembled by the Agents module.</summary>
public sealed record PlatformCounts(long Tenants, long Users, long EnabledBackupJobs, long FailedRunsLast24Hours);

public enum PlatformChangeOutcome
{
    Changed = 1,
    /// <summary>The request matched the stored state; nothing was written and no audit event was recorded.</summary>
    Unchanged = 2,
    TenantNotFound = 3,
    UserNotFound = 4,
    MembershipNotFound = 5,
    AlreadyMember = 6,
    LoginTaken = 7
}

public sealed record PlatformChange<T>(PlatformChangeOutcome Outcome, T? Value = default) where T : class;

/// <summary>
/// Cross-tenant administration reads and writes for platform administrators. Every write records its audit event in
/// the same transaction. The caller must already have authorized a PlatformAdmin; implementations use the explicit
/// platform operations context and never a tenant-scoped one.
/// </summary>
public interface IPlatformAdministrationStore
{
    Task<PlatformCounts> GetCountsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task<PagedResult<PlatformTenantItem>> ListTenantsAsync(string? search, int page, int pageSize, CancellationToken cancellationToken);
    Task<PlatformTenantDetail?> GetTenantAsync(TenantId tenantId, CancellationToken cancellationToken);
    Task<PlatformTenantItem> CreateTenantAsync(string name, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);
    Task<PlatformChange<PlatformTenantItem>> RenameTenantAsync(TenantId tenantId, string name, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    Task<PagedResult<PlatformUserItem>> ListUsersAsync(string? search, int page, int pageSize, CancellationToken cancellationToken);
    Task<PlatformChange<PlatformUserItem>> CreateUserAsync(
        UserId userId, string displayName, string loginIdentifier, string passwordHash, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Null when the tenant does not exist.</summary>
    Task<PagedResult<PlatformMembershipItem>?> ListMembershipsAsync(TenantId tenantId, int page, int pageSize, CancellationToken cancellationToken);
    Task<PlatformChange<PlatformMembershipItem>> AddMembershipAsync(
        TenantId tenantId, UserId userId, TenantRole role, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);
    Task<PlatformChange<PlatformMembershipItem>> ChangeMembershipRoleAsync(
        TenantId tenantId, UserId userId, TenantRole role, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);
    Task<PlatformChange<PlatformMembershipItem>> RemoveMembershipAsync(
        TenantId tenantId, UserId userId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    Task<PagedResult<PlatformAuditItem>> ListAuditAsync(TenantId? tenantId, int page, int pageSize, CancellationToken cancellationToken);
}
