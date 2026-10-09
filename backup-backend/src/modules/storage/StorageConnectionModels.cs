using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;

namespace BackupPlatform.Storage;

/// <summary>Audit action names of the connection lifecycle. All carry the tenant; none carries a secret value.</summary>
public static class StorageConnectionAuditActions
{
    public const string Created = "storage_connection.created";
    public const string Updated = "storage_connection.updated";
    public const string Enabled = "storage_connection.enabled";
    public const string Disabled = "storage_connection.disabled";
    public const string Deleted = "storage_connection.deleted";
    public const string Tested = "storage_connection.tested";
    public const string SecretRotated = "storage_connection.secret_rotated";
    public const string SecretCleared = "storage_connection.secret_cleared";
    public const string QuotaScopeChanged = "storage_connection.quota_scope_changed";

    public const string TargetType = "storage_connection";
}

/// <summary>Machine-readable validation failure naming the offending field; never carries the rejected value.</summary>
public sealed class InvalidStorageConnectionException(string field, string code)
    : Exception($"Invalid storage connection field '{field}' ({code}).")
{
    public string Field { get; } = field;
    public string Code { get; } = code;
}

/// <summary>
/// Who is asking and from where. <see cref="TenantId"/> is set for the tenant routes (the caller is a member of exactly that tenant, and every
/// lookup is bound to it) and null for the platform routes. <see cref="CanManage"/> says whether the caller may change things at all (a tenant
/// Admin/Owner, or a PlatformAdmin); it only shapes the permissions a view reports, the routes' policies are the actual gate.
/// </summary>
public sealed record StorageScope(TenantId? TenantId, bool CanManage)
{
    public bool IsPlatform => TenantId is null;

    public static StorageScope Tenant(TenantId tenantId, bool canManage) => new(tenantId, canManage);

    public static StorageScope Platform { get; } = new(null, true);

    public AuditActorKind ActorKind => IsPlatform ? AuditActorKind.PlatformAdmin : AuditActorKind.TenantAdministrator;
}

public sealed record StorageActor(UserId UserId, AuditActorKind Kind);

/// <summary>A connection with what the store read next to it. Never contains a secret value (only that one is referenced).</summary>
public sealed record StorageConnectionRecord(StorageConnection Connection, string? TenantName, int ReplicaCount, long StoredBytes);

public sealed record StorageConnectionPermissions(bool CanEdit, bool CanSetQuotaScope, bool CanToggle, bool CanDelete, bool CanManageSecret, bool CanTest);

public sealed record StorageTestSummary(StorageTestStatus Status, string? Code, string Message, DateTimeOffset TestedAt);

public sealed record StorageConnectionView(
    Guid Id, Guid TenantId, string? TenantName, string Name, string ProviderType, string ProviderDisplayName, StorageProviderAvailability Availability,
    StorageOwnership Ownership, string QuotaScope, bool Enabled, bool IsDefault, IReadOnlyList<string> Capabilities,
    IReadOnlyDictionary<string, string> Configuration, bool HasSecret, IReadOnlyList<string> SecretFieldNames, DateTimeOffset? SecretUpdatedAt,
    StorageTestSummary? LastTest, int ReplicaCount, long StoredBytes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? StateChangedAt,
    StorageConnectionPermissions Permissions, bool AdministrativelyDisabled, bool EffectiveEnabled);

public sealed record StorageTestResult(StorageTestStatus Status, string? Code, string Message);

public sealed record StorageTestOutcome(StorageConnectionView Connection, StorageTestSummary Test, double DurationMilliseconds);

public sealed record StorageConnectionFilter(StorageOwnership? Ownership, string? ProviderType, TenantId? TenantId, bool? Enabled, string? Search);

public enum StorageConnectionOutcome
{
    Changed = 1,
    /// <summary>The request matched the stored state; nothing was written and no audit event was recorded.</summary>
    Unchanged = 2,
    NotFound = 3,
    TenantNotFound = 4,
    NameTaken = 5,
    LimitReached = 6,
    /// <summary>Replicas, jobs or runs still reference the connection.</summary>
    InUse = 7,
    ConcurrentUpdate = 8,
    /// <summary>The mutation refused itself; <see cref="StorageConnectionResult.Code"/> says why.</summary>
    Rejected = 9
}

public sealed record StorageConnectionResult(StorageConnectionOutcome Outcome, StorageConnectionRecord? Record = null, string? Code = null);

public sealed record StorageAuditEntry(string Action, IReadOnlyDictionary<string, object?> Details);

/// <summary>What a mutation did to the connection it was handed, so the store can audit it and decide whether usage must be recomputed.</summary>
public sealed record StorageMutationResult(
    bool Changed, IReadOnlyList<StorageAuditEntry>? Audit = null, bool AffectsQuota = false, string? RejectionCode = null)
{
    public static StorageMutationResult NoChange { get; } = new(false);

    public static StorageMutationResult Rejected(string code) => new(false, RejectionCode: code);
}

public delegate StorageMutationResult StorageMutation(StorageConnection connection, DateTimeOffset now);

/// <summary>
/// Connection persistence. Every write takes the tenant lock, applies the change, writes its audit event in the same transaction and, when
/// the change alters what quota counts (the quota scope label), recomputes the tenant's usage before committing. The implementation is bound to
/// explicit platform operations; the endpoints are the authorization boundary and always pass the scope they authorized.
/// </summary>
public interface IStorageConnectionStore
{
    /// <summary>The tenant's connections, name order. The system default connections are materialized first when missing. Null when the tenant does not exist.</summary>
    Task<IReadOnlyList<StorageConnectionRecord>?> ListAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task<PagedResult<StorageConnectionRecord>> SearchAsync(StorageConnectionFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>One connection. With <paramref name="tenantScope"/> it is found only inside that tenant, so another tenant's id is simply not found.</summary>
    Task<StorageConnectionRecord?> FindAsync(Guid id, TenantId? tenantScope, CancellationToken cancellationToken);

    /// <param name="maxPerTenant">Connections of this provider type one tenant may hold; null for no limit.</param>
    Task<StorageConnectionResult> CreateAsync(
        StorageConnection connection, int? maxPerTenant, StorageActor actor, IReadOnlyDictionary<string, object?> auditDetails, DateTimeOffset now, CancellationToken cancellationToken);

    Task<StorageConnectionResult> MutateAsync(
        Guid id, TenantId? tenantScope, StorageMutation mutation, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Removes a connection nothing references. <see cref="StorageConnectionOutcome.InUse"/> otherwise.</summary>
    Task<StorageConnectionResult> DeleteAsync(Guid id, TenantId? tenantScope, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken);
}
