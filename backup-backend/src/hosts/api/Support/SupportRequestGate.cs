using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Support;
using BackupPlatform.Domain.Support;

namespace BackupPlatform.Api.Support;

/// <summary>The outcome of asking the support gate: either the verified context to proceed with, or the response that ends the request.</summary>
internal readonly record struct SupportGateResult(SupportAccessContext? Context, IResult? Failure);

/// <summary>
/// Turns <see cref="SupportAccessService.AuthorizeAsync"/> into HTTP. Reaching a support data route needs the PlatformAdmin policy AND this gate: an active
/// session owned by the caller that grants the permission. Fail-closed: when the audit of an allowed operation cannot be persisted the request is a 503 and
/// nothing is read or written.
/// </summary>
internal sealed class SupportRequestGate(SupportAccessService access, ICurrentUser user, ILogger<SupportRequestGate> logger)
{
    public async Task<SupportGateResult> RequireAsync(Guid sessionId, SupportPermissions permission, SupportAuditSpec? audit, string attemptedAction, CancellationToken cancellationToken)
    {
        try
        {
            var result = await access.AuthorizeAsync(sessionId, user.UserId!, permission, audit, attemptedAction, cancellationToken);
            if (result.Context is { } context) return new SupportGateResult(context, null);
            return new SupportGateResult(null, result.Denial is { } denial ? Denied(denial, permission) : NotFound());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Support operation {Action} was refused because its audit or authorization could not be persisted ({ExceptionType})", attemptedAction, exception.GetType().Name);
            return new SupportGateResult(null, Unavailable());
        }
    }

    public async Task<IResult> DeniedAsync(SupportAccessContext context, SupportAccessDenial denial, SupportPermissions permission, string targetType, string targetId, string attemptedAction,
        CancellationToken cancellationToken)
    {
        await access.RecordDenialAsync(context, denial, permission, targetType, targetId, attemptedAction, cancellationToken);
        return Denied(denial, permission);
    }

    public static IResult Denied(SupportAccessDenial denial, SupportPermissions permission) => Results.Json(new
    {
        error = "support_access_denied",
        reason = SupportAccessDenials.Code(denial),
        permission = denial == SupportAccessDenial.PermissionMissing && permission != SupportPermissions.None ? SupportPermissionNames.ToNames(permission).FirstOrDefault() : null
    }, statusCode: StatusCodes.Status403Forbidden);

    public static IResult NotFound() => Results.NotFound(new { error = "support_session_not_found" });

    public static IResult Unavailable() => Results.Json(new { error = "support_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}
