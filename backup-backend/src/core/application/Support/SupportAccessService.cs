using System.Security.Cryptography;
using System.Text;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Support;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Application.Support;

/// <summary>
/// Opens, ends and authorizes support access sessions. The caller must already have established that the actor is a PlatformAdmin: that is only
/// the right to ask for a session, never a right to any customer data. Time always comes from the server clock.
/// </summary>
public sealed class SupportAccessService(ISupportAccessStore store, ISupportQueryStore queries, ISupportApprovalPolicy approval, TimeProvider time,
    ILogger<SupportAccessService> logger)
{
    public const int MaximumDurationMinutes = 240;
    private const int MaximumPageSize = 100;

    /// <exception cref="InvalidSupportRequestException">A request field is invalid.</exception>
    public async Task<SupportOpenResult> OpenAsync(TenantId tenantId, UserId admin, string? reason, IReadOnlyCollection<string>? permissionNames, int? durationMinutes,
        string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(admin);
        if (!BackupInputValidation.IsValidKey(idempotencyKey)) throw new InvalidSupportRequestException("Idempotency-Key", "invalid_value");
        string trimmedReason;
        try { trimmedReason = SupportAccessSession.ValidateReason(reason); }
        catch (ArgumentException)
        {
            throw new InvalidSupportRequestException("reason", string.IsNullOrWhiteSpace(reason) ? "required" : reason.Trim().Length > SupportAccessSession.MaximumReasonLength ? "too_long" : "invalid_value");
        }
        if (permissionNames is null || permissionNames.Count == 0) throw new InvalidSupportRequestException("permissions", "required");
        var permissions = SupportPermissionNames.TryParse(permissionNames) ?? throw new InvalidSupportRequestException("permissions", "invalid_value");
        if (permissions != SupportPermissions.ViewBackupMetadata && !permissions.HasFlag(SupportPermissions.ViewBackupMetadata))
            throw new InvalidSupportRequestException("permissions", "view_required");
        var minutes = durationMinutes ?? (int)SupportAccessSession.DefaultDuration.TotalMinutes;
        if (minutes < 1) throw new InvalidSupportRequestException("durationMinutes", "invalid_value");
        if (minutes > MaximumDurationMinutes) throw new InvalidSupportRequestException("durationMinutes", "too_long");
        if (approval.RequiresTenantApproval(tenantId)) return new SupportOpenResult(SupportOpenOutcome.ApprovalRequired);

        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"support-open-v1:{tenantId.Value:D}:{trimmedReason}:{(int)permissions}:{minutes}")));
        return await store.OpenAsync(new SupportOpenCommand(Guid.NewGuid(), tenantId, admin, trimmedReason, permissions, TimeSpan.FromMinutes(minutes),
            idempotencyKey!, fingerprint, time.GetUtcNow()), cancellationToken);
    }

    /// <summary>The server clock, for views that report an effective status.</summary>
    public DateTimeOffset Now => time.GetUtcNow();

    public Task<SupportSessionRow?> GetAsync(Guid sessionId, CancellationToken cancellationToken) => queries.GetAsync(sessionId, cancellationToken);

    public Task<SupportPage<SupportSessionRow>> ListAsync(TenantId? tenantId, SupportSessionStatus? status, UserId? admin, int page, int pageSize, CancellationToken cancellationToken) =>
        queries.ListAsync(new SupportSessionFilter(tenantId, status, admin, Math.Max(1, page), Math.Clamp(pageSize, 1, MaximumPageSize)), time.GetUtcNow(), cancellationToken);

    public Task<SupportPage<SupportAuditRow>> ListAuditAsync(Guid sessionId, int page, int pageSize, CancellationToken cancellationToken) =>
        queries.ListAuditAsync(sessionId, Math.Max(1, page), Math.Clamp(pageSize, 1, MaximumPageSize), cancellationToken);

    public Task<SupportEndResult> CloseAsync(Guid sessionId, UserId by, CancellationToken cancellationToken) =>
        store.CloseAsync(sessionId, by, time.GetUtcNow(), cancellationToken);

    /// <exception cref="InvalidSupportRequestException">The reason is blank or too long.</exception>
    public Task<SupportEndResult> RevokeAsync(Guid sessionId, UserId by, string? reason, CancellationToken cancellationToken)
    {
        string trimmed;
        try { trimmed = SupportAccessSession.ValidateReason(reason); }
        catch (ArgumentException)
        {
            throw new InvalidSupportRequestException("reason", string.IsNullOrWhiteSpace(reason) ? "required" : "too_long");
        }
        return store.RevokeAsync(sessionId, by, trimmed, time.GetUtcNow(), cancellationToken);
    }

    /// <summary>
    /// The one door to a support operation. Allowed: the audit of the operation (when <paramref name="audit"/> is given) has been durably written, in the
    /// same transaction as the decision, and a <see cref="SupportAccessContext"/> is returned. An audit that cannot be written throws: fail-closed, the
    /// caller must not proceed. Refused: the refusal is audited best-effort (it grants nothing either way) and the reason returned.
    /// </summary>
    public async Task<SupportAuthorizationResult> AuthorizeAsync(Guid sessionId, UserId actor, SupportPermissions required, SupportAuditSpec? audit, string attemptedAction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var now = time.GetUtcNow();
        var result = await store.AuthorizeAsync(new SupportAuthorizationCommand(sessionId, actor, required, now, audit), cancellationToken);
        if (result.Session is { } session) return new SupportAuthorizationResult(new SupportAccessContext(session), null);
        if (result.Denial is not { } denial)
        {
            logger.LogWarning("Support access to unknown session {SessionId} was refused", sessionId);
            return new SupportAuthorizationResult(null, null);
        }
        await RecordDenialAsync(sessionId, actor, result.DeniedTenantId, denial, required, audit?.TargetType, audit?.TargetId, attemptedAction, now, cancellationToken);
        return new SupportAuthorizationResult(null, denial);
    }

    /// <summary>Records a refusal the database decided in a race (the session ended between the endpoint's check and the write).</summary>
    public Task RecordDenialAsync(SupportAccessContext context, SupportAccessDenial denial, SupportPermissions attempted, string targetType, string targetId, string attemptedAction,
        CancellationToken cancellationToken) =>
        RecordDenialAsync(context.SessionId, context.ActorId, context.TenantId.Value, denial, attempted, targetType, targetId, attemptedAction, time.GetUtcNow(), cancellationToken);

    private async Task RecordDenialAsync(Guid sessionId, UserId actor, Guid? tenantId, SupportAccessDenial denial, SupportPermissions attempted, string? targetType, string? targetId,
        string attemptedAction, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var details = new Dictionary<string, object?>
            {
                ["supportSessionId"] = sessionId,
                ["result"] = "denied",
                ["reason"] = SupportAccessDenials.Code(denial),
                ["attemptedAction"] = attemptedAction,
                ["permission"] = attempted == SupportPermissions.None ? null : string.Join(',', SupportPermissionNames.ToNames(attempted))
            };
            await store.AppendAuditAsync(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, SupportAuditActions.AccessDenied, tenantId is { } id ? new TenantId(id) : null,
                targetType ?? SupportAuditActions.SessionTargetType, targetId ?? sessionId.ToString("D"), details, sessionId), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A refusal grants nothing, so a failed audit of it must not turn into an error that hides the refusal; it is still an operational signal.
            logger.LogError("Support access refusal ({Denial}) for session {SessionId} could not be audited: {ExceptionType}", SupportAccessDenials.Code(denial), sessionId, exception.GetType().Name);
        }
    }
}
