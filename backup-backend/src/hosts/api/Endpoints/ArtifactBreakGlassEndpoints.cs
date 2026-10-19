using BackupPlatform.Api.Downloads;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Api.Endpoints;

public sealed class OpenBreakGlassRequest
{
    public Guid? TenantId { get; init; }
    public Guid? ArtifactId { get; init; }
    public string? Reason { get; init; }
    public int? TtlMinutes { get; init; }
}

public sealed class DenyBreakGlassRequest
{
    public string? Reason { get; init; }
}

/// <summary>
/// Break-glass online decrypt: a PlatformAdmin asks, a DIFFERENT PlatformAdmin approves, the requester then downloads the plaintext once, within the TTL. Every route needs the
/// PlatformAdmin policy; there is no tenant-scoped route that decrypts for a platform admin. The download itself is the normal download engine (same holds, relays and
/// verification) with private ciphertext and plaintext staging: the full SourceChecksum must match before any plaintext response byte.
/// </summary>
public static class ArtifactBreakGlassEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaximumPageSize = 100;

    public static IEndpointRouteBuilder MapArtifactBreakGlassEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/artifact-break-glass").RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        group.MapPost("", OpenAsync);
        group.MapGet("", ListAsync);
        group.MapGet("/{requestId:guid}", GetAsync);
        group.MapPost("/{requestId:guid}/approve", ApproveAsync);
        group.MapPost("/{requestId:guid}/deny", DenyAsync);
        group.MapPost("/{requestId:guid}/download", PrepareDownloadAsync);
        group.MapGet("/{requestId:guid}/downloads/{downloadId:guid}", DownloadStatusAsync);
        group.MapGet("/{requestId:guid}/downloads/{downloadId:guid}/content", DownloadContentAsync);

        // The tenant's own view: who asked to read which of its artifacts, why, who approved, whether it was used. Read-only.
        endpoints.MapGet("/api/tenants/{tenantId:guid}/artifact-break-glass", TenantListAsync).WithMetadata(new TenantScopedEndpoint())
            .RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        return endpoints;
    }

    private static IResult Invalid(string field, string code) => Results.BadRequest(new { error = "invalid_request", field, code });

    private static IResult NotFound() => Results.NotFound(new { error = "break_glass_request_not_found" });

    private static IResult Unavailable() => Results.Json(new { error = "break_glass_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static bool TryPaging(int? page, int? pageSize, out int p, out int size)
    {
        p = page ?? 1;
        size = pageSize ?? DefaultPageSize;
        return p is >= 1 and <= 1_000_000 && size is >= 1 and <= MaximumPageSize;
    }

    /// <summary>The refusal as HTTP: self-approval is a 403 (it is about who the caller is), everything else is the request's state, a 409.</summary>
    private static IResult Refused(BreakGlassDenial denial)
    {
        var body = new { error = BreakGlassDenials.Code(denial) };
        return denial is BreakGlassDenial.SelfApproval or BreakGlassDenial.NotRequester ? Results.Json(body, statusCode: StatusCodes.Status403Forbidden) : Results.Conflict(body);
    }

    private static async Task<IResult> OpenAsync(OpenBreakGlassRequest? request, ICurrentUser user, ArtifactBreakGlassService service, ILogger<ArtifactBreakGlassService> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.OpenAsync(request?.TenantId, request?.ArtifactId, user.UserId!, request?.Reason, request?.TtlMinutes, cancellationToken);
            return result.Outcome switch
            {
                BreakGlassOpenOutcome.Opened => Results.Json(service.ToView((await service.GetAsync(result.Request!.Id, cancellationToken))!, user.UserId!), statusCode: StatusCodes.Status201Created),
                BreakGlassOpenOutcome.ArtifactNotFound => Results.NotFound(new { error = "artifact_not_found" }),
                BreakGlassOpenOutcome.NotEncrypted => Results.Conflict(new { error = "artifact_not_encrypted" }),
                _ => Results.Conflict(new { error = "key_unavailable" })
            };
        }
        catch (InvalidBreakGlassRequestException exception)
        {
            return Invalid(exception.Field, exception.Code);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Opening a break-glass request failed with {ExceptionType}; nothing was opened", exception.GetType().Name);
            return Unavailable();
        }
    }

    private static async Task<IResult> ListAsync(Guid? tenantId, string? status, bool? mine, int? page, int? pageSize, ICurrentUser user, ArtifactBreakGlassService service,
        CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size)) return Invalid("page", "invalid_value");
        BreakGlassStatus? parsed = null;
        if (status is not null)
        {
            var name = Enum.GetNames<BreakGlassStatus>().FirstOrDefault(candidate => string.Equals(candidate, status, StringComparison.OrdinalIgnoreCase));
            if (name is null) return Invalid("status", "invalid_value");
            parsed = Enum.Parse<BreakGlassStatus>(name);
        }
        var result = await service.ListAsync(tenantId is { } id && id != Guid.Empty ? new TenantId(id) : null, parsed, mine == true ? user.UserId : null, p, size, cancellationToken);
        return Results.Ok(new { items = result.Items.Select(row => service.ToView(row, user.UserId!)).ToList(), totalCount = result.TotalCount, page = result.Page, pageSize = result.PageSize });
    }

    private static async Task<IResult> GetAsync(Guid requestId, ICurrentUser user, ArtifactBreakGlassService service, CancellationToken cancellationToken)
    {
        var row = await service.GetAsync(requestId, cancellationToken);
        return row is null ? NotFound() : Results.Ok(service.ToView(row, user.UserId!));
    }

    private static async Task<IResult> ApproveAsync(Guid requestId, ICurrentUser user, ArtifactBreakGlassService service, ILogger<ArtifactBreakGlassService> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.ApproveAsync(requestId, user.UserId!, cancellationToken);
            return await DecisionAsync(result, requestId, user, service, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Approving break-glass request {RequestId} failed with {ExceptionType}", requestId, exception.GetType().Name);
            return Unavailable();
        }
    }

    private static async Task<IResult> DenyAsync(Guid requestId, DenyBreakGlassRequest? request, ICurrentUser user, ArtifactBreakGlassService service, ILogger<ArtifactBreakGlassService> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.DenyAsync(requestId, user.UserId!, request?.Reason, cancellationToken);
            return await DecisionAsync(result, requestId, user, service, cancellationToken);
        }
        catch (InvalidBreakGlassRequestException exception)
        {
            return Invalid(exception.Field, exception.Code);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Denying break-glass request {RequestId} failed with {ExceptionType}", requestId, exception.GetType().Name);
            return Unavailable();
        }
    }

    private static async Task<IResult> DecisionAsync(BreakGlassDecisionResult result, Guid requestId, ICurrentUser user, ArtifactBreakGlassService service, CancellationToken cancellationToken) =>
        result.Outcome switch
        {
            BreakGlassDecisionOutcome.NotFound => NotFound(),
            BreakGlassDecisionOutcome.Refused => Refused(result.Denial!.Value),
            _ => Results.Ok(service.ToView((await service.GetAsync(requestId, cancellationToken))!, user.UserId!))
        };

    private static async Task<IResult> PrepareDownloadAsync(Guid requestId, PrepareDownloadRequest? request, ICurrentUser user, ArtifactBreakGlassService service, DownloadService downloads,
        ILogger<DownloadService> logger, CancellationToken cancellationToken)
    {
        var row = await service.GetAsync(requestId, cancellationToken);
        if (row is null) return NotFound();
        try
        {
            // The grant is spent inside the store's create transaction, which re-judges it under the request's row lock (the requester, Approved, not expired, not used)
            // and records a refusal in the audit trail.
            var prepared = await downloads.PrepareAsync(row.Request.TenantId, row.Request.ArtifactId, user.UserId!.Value, new PrepareDownloadRequest(request?.ReplicaId), cancellationToken,
                breakGlassRequestId: requestId);
            return prepared is null ? Results.NotFound() : Results.Ok(prepared);
        }
        catch (BreakGlassDeniedException exception)
        {
            return Refused(exception.Denial);
        }
        catch (DownloadUnavailableException exception)
        {
            return exception.Code == "replica_not_found" ? Results.NotFound(new { error = exception.Code }) : Results.Conflict(new { error = exception.Code });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Break-glass download request {RequestId} failed with {ExceptionType}", requestId, exception.GetType().Name);
            return Results.Json(new { error = "download_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> DownloadStatusAsync(Guid requestId, Guid downloadId, ICurrentUser user, ArtifactBreakGlassService service, DownloadService downloads,
        ILogger<DownloadService> logger, CancellationToken cancellationToken)
    {
        var row = await service.GetAsync(requestId, cancellationToken);
        if (row is null) return NotFound();
        try
        {
            var session = await downloads.GetOwnedAsync(row.Request.TenantId, row.Request.ArtifactId, downloadId, user.UserId!.Value, cancellationToken, breakGlassRequestId: requestId);
            return session is null ? Results.NotFound() : Results.Ok(new DownloadStatus(session.Id, session.State.ToString(), session.FailureCode, session.TransferredBytes));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Break-glass download {DownloadId} status failed with {ExceptionType}", downloadId, exception.GetType().Name);
            return Results.Json(new { error = "download_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> DownloadContentAsync(Guid requestId, Guid downloadId, ICurrentUser user, ArtifactBreakGlassService service, DownloadService downloads,
        DownloadStreamer streamer, ILogger<DownloadService> logger, CancellationToken cancellationToken)
    {
        var row = await service.GetAsync(requestId, cancellationToken);
        if (row is null) return NotFound();
        try
        {
            var session = await downloads.GetOwnedAsync(row.Request.TenantId, row.Request.ArtifactId, downloadId, user.UserId!.Value, cancellationToken, breakGlassRequestId: requestId);
            if (session is null) return Results.NotFound();
            if (session.State != DownloadState.Ready) return Results.Conflict(new { error = session.FailureCode ?? "download_not_ready" });
            return new StreamResult(session, streamer);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Break-glass download {DownloadId} authorization failed with {ExceptionType}", downloadId, exception.GetType().Name);
            return Results.Json(new { error = "download_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> TenantListAsync(int? page, int? pageSize, ITenantContext tenant, ArtifactBreakGlassService service, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size)) return Invalid("page", "invalid_value");
        var result = await service.ListAsync(tenant.TenantId!, null, null, p, size, cancellationToken);
        return Results.Ok(new { items = result.Items.Select(service.ToTenantView).ToList(), totalCount = result.TotalCount, page = result.Page, pageSize = result.PageSize });
    }

    private sealed class StreamResult(DownloadSession session, DownloadStreamer streamer) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => streamer.StreamAsync(httpContext, session);
    }
}
