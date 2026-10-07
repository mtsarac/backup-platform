using System.Text.Json;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Domain.Audit;

/// <summary>
/// Which kind of authority the acting user exercised. A PlatformAdmin grant is independent of tenant membership, so
/// the kind is recorded explicitly instead of being inferred from roles at read time.
/// </summary>
public enum AuditActorKind
{
    PlatformAdmin = 1,
    TenantAdministrator = 2
}

public static class AuditActions
{
    public const string TenantCreated = "tenant.created";
    public const string TenantRenamed = "tenant.renamed";
    public const string MembershipAdded = "membership.added";
    public const string MembershipRoleChanged = "membership.role_changed";
    public const string MembershipRemoved = "membership.removed";
    public const string EnrollmentPolicyChanged = "agent_enrollment.policy_changed";
    public const string UserCreated = "user.created";
}

/// <summary>
/// An immutable, durable record of an administrative change. Written in the same transaction as the change it
/// describes; never updated or deleted by the application. Details carry identifiers and values, never secrets.
/// </summary>
public sealed class AuditEvent
{
    public const int MaximumDetailsLength = 4096;

    private AuditEvent()
    {
        ActorUserId = null!;
        Action = null!;
        TargetType = null!;
        TargetId = null!;
        DetailsJson = null!;
    }

    private AuditEvent(
        Guid id, DateTimeOffset at, UserId actorUserId, AuditActorKind actorKind, string action,
        TenantId? tenantId, string targetType, string targetId, string detailsJson)
    {
        Id = id;
        At = at.ToUniversalTime();
        ActorUserId = actorUserId;
        ActorKind = actorKind;
        Action = action;
        TenantId = tenantId;
        TargetType = targetType;
        TargetId = targetId;
        DetailsJson = detailsJson;
    }

    public Guid Id { get; private set; }
    public DateTimeOffset At { get; private set; }
    public UserId ActorUserId { get; private set; }
    public AuditActorKind ActorKind { get; private set; }
    public string Action { get; private set; }
    /// <summary>The tenant the change concerns; null for changes that belong to no tenant (for example user creation).</summary>
    public TenantId? TenantId { get; private set; }
    public string TargetType { get; private set; }
    public string TargetId { get; private set; }
    /// <summary>A JSON object of scalar values.</summary>
    public string DetailsJson { get; private set; }

    public static AuditEvent Record(
        DateTimeOffset at, UserId actor, AuditActorKind actorKind, string action, TenantId? tenantId,
        string targetType, string targetId, IReadOnlyDictionary<string, object?>? details = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetType);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (!Enum.IsDefined(actorKind))
        {
            throw new ArgumentOutOfRangeException(nameof(actorKind));
        }
        var json = JsonSerializer.Serialize(details ?? new Dictionary<string, object?>());
        if (json.Length > MaximumDetailsLength)
        {
            throw new ArgumentException("Audit details are too large.", nameof(details));
        }
        return new AuditEvent(Guid.NewGuid(), at, actor, actorKind, action, tenantId, targetType, targetId, json);
    }
}
