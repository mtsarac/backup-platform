using BackupPlatform.Domain.Downloads;
using BackupPlatform.Api.Security;
using Microsoft.AspNetCore.Mvc;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Api.Endpoints;

public static class BackupShareEndpoints
{
    public static IEndpointRouteBuilder MapBackupShareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}").WithMetadata(new TenantScopedEndpoint());

        tenant.MapPost("/backup-artifacts/{artifactId:guid}/shares", CreateShareForArtifactAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapPost("/backup-shares", CreateShareAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/backup-shares", ListSharesAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/backup-artifacts/{artifactId:guid}/shares", ListSharesForArtifactAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/backup-shares/{shareId:guid}", GetShareAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapPost("/backup-shares/{shareId:guid}/extend", ExtendShareAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapPost("/backup-shares/{shareId:guid}/revoke", RevokeShareAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapPost("/backup-artifacts/{artifactId:guid}/shares/{shareId:guid}/extend", ExtendShareForArtifactAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapPost("/backup-artifacts/{artifactId:guid}/shares/{shareId:guid}/revoke", RevokeShareForArtifactAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/backup-shares/download-attempts", ListTenantDownloadAttemptsAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);

        var platform = endpoints.MapGroup("/api/platform/backup-shares")
            .RequireAuthorization(AuthorizationPolicies.PlatformAdmin);

        platform.MapPost("", CreatePlatformShareAsync);
        platform.MapGet("", ListPlatformSharesAsync);
        platform.MapGet("/{shareId:guid}", GetPlatformShareAsync);

        var platformRoot = endpoints.MapGroup("/api/platform")
            .RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        platformRoot.MapGet("/download-attempts", ListPlatformDownloadAttemptsAsync);
        platformRoot.MapGet("/settings/audit-retention", GetAuditRetentionAsync);
        platformRoot.MapPut("/settings/audit-retention", UpdateAuditRetentionAsync);

        return endpoints;
    }

    private static async Task<IResult> CreateShareForArtifactAsync(
        Guid artifactId,
        CreateBackupShareRequest request,
        ITenantContext tenant,
        ICurrentUser user,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        return await HandleCreateAsync(tenant.TenantId!, artifactId, request, user, service, cancellationToken);
    }

    private static async Task<IResult> CreateShareAsync(
        CreateBackupShareRequest request,
        ITenantContext tenant,
        ICurrentUser user,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        if (!request.ArtifactId.HasValue || request.ArtifactId.Value == Guid.Empty)
        {
            return Results.BadRequest(new { error = "invalid_request", message = "ArtifactId is required." });
        }

        return await HandleCreateAsync(tenant.TenantId!, request.ArtifactId.Value, request, user, service, cancellationToken);
    }

    private static async Task<IResult> HandleCreateAsync(
        TenantId tenantId,
        Guid artifactId,
        CreateBackupShareRequest request,
        ICurrentUser user,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        if (user.UserId is not { } actor)
        {
            return Results.Unauthorized();
        }

        TimeSpan duration;
        if (request.DurationHours.HasValue)
        {
            if (request.DurationHours.Value <= 0 || request.DurationHours.Value > 72)
            {
                return Results.BadRequest(new { error = "invalid_duration", message = "Requested duration must be positive and not exceed 72 hours." });
            }

            duration = TimeSpan.FromHours(request.DurationHours.Value);
        }
        else if (request.Duration.HasValue)
        {
            if (request.Duration.Value <= TimeSpan.Zero || request.Duration.Value > BackupShare.MaximumDuration)
            {
                return Results.BadRequest(new { error = "invalid_duration", message = "Requested duration must be positive and not exceed 72 hours." });
            }

            duration = request.Duration.Value;
        }
        else
        {
            duration = BackupShare.DefaultDuration;
        }

        var result = await service.CreateShareAsync(
            tenantId,
            artifactId,
            actor,
            duration,
            request.Reason,
            cancellationToken);

        return result.Outcome switch
        {
            BackupShareCreateOutcome.Success =>
                Results.Created($"/api/tenants/{tenantId.Value}/backup-shares/{result.Share!.Id}", result.Share),
            BackupShareCreateOutcome.ArtifactNotFound =>
                Results.NotFound(new { error = "artifact_not_found", message = "The artifact was not found." }),
            BackupShareCreateOutcome.NoDownloadableReplica =>
                Results.UnprocessableEntity(new { error = "no_downloadable_replica", message = "A share can be created only when the artifact has at least one downloadable replica that is Verified and Available." }),
            BackupShareCreateOutcome.Forbidden =>
                Results.Forbid(),
            _ => Results.BadRequest(new { error = result.ErrorCode ?? "create_share_failed", message = result.ErrorMessage ?? "Failed to create backup share." })
        };
    }

    private static async Task<IResult> ListSharesAsync(
        Guid? artifactId,
        ITenantContext tenant,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        var shares = await service.ListSharesAsync(tenant.TenantId!, artifactId, cancellationToken);
        return Results.Ok(shares);
    }

    private static async Task<IResult> ListSharesForArtifactAsync(
        Guid artifactId,
        ITenantContext tenant,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        var shares = await service.ListSharesAsync(tenant.TenantId!, artifactId, cancellationToken);
        return Results.Ok(shares);
    }

    private static async Task<IResult> GetShareAsync(
        Guid shareId,
        ITenantContext tenant,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        var share = await service.GetShareAsync(tenant.TenantId!, shareId, cancellationToken);
        return share is null
            ? Results.NotFound(new { error = "share_not_found", message = "Backup share not found." })
            : Results.Ok(share);
    }

    private static async Task<IResult> CreatePlatformShareAsync(
        CreatePlatformBackupShareRequest request,
        ICurrentUser user,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        if (user.UserId is not { } actor)
        {
            return Results.Unauthorized();
        }

        if (request.TenantId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "invalid_tenant", message = "TenantId is required." });
        }

        if (request.ArtifactId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "invalid_artifact", message = "ArtifactId is required." });
        }

        TimeSpan duration;
        if (request.DurationHours.HasValue)
        {
            if (request.DurationHours.Value <= 0 || request.DurationHours.Value > 72)
            {
                return Results.BadRequest(new { error = "invalid_duration", message = "Requested duration must be positive and not exceed 72 hours." });
            }

            duration = TimeSpan.FromHours(request.DurationHours.Value);
        }
        else if (request.Duration.HasValue)
        {
            if (request.Duration.Value <= TimeSpan.Zero || request.Duration.Value > BackupShare.MaximumDuration)
            {
                return Results.BadRequest(new { error = "invalid_duration", message = "Requested duration must be positive and not exceed 72 hours." });
            }

            duration = request.Duration.Value;
        }
        else
        {
            duration = BackupShare.DefaultDuration;
        }

        var result = await service.CreatePlatformShareAsync(
            new TenantId(request.TenantId),
            request.ArtifactId,
            actor,
            duration,
            request.Reason,
            cancellationToken);

        return result.Outcome switch
        {
            BackupShareCreateOutcome.Success =>
                Results.Created($"/api/platform/backup-shares/{result.Share!.Id}", result.Share),
            BackupShareCreateOutcome.ArtifactNotFound =>
                Results.NotFound(new { error = "artifact_not_found", message = "The artifact was not found." }),
            BackupShareCreateOutcome.NoDownloadableReplica =>
                Results.UnprocessableEntity(new { error = "no_downloadable_replica", message = "A share can be created only when the artifact has at least one downloadable replica that is Verified and Available." }),
            BackupShareCreateOutcome.Forbidden =>
                Results.Forbid(),
            _ => Results.BadRequest(new { error = result.ErrorCode ?? "create_share_failed", message = result.ErrorMessage ?? "Failed to create backup share." })
        };
    }

    private static async Task<IResult> ListPlatformSharesAsync(
        [FromQuery] Guid? tenantId,
        [FromQuery] string? status,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        BackupShareStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (Enum.TryParse<BackupShareStatus>(status, ignoreCase: true, out var parsed))
            {
                parsedStatus = parsed;
            }
            else
            {
                return Results.BadRequest(new { error = "invalid_status", message = $"Invalid status filter: {status}." });
            }
        }

        var shares = await service.ListPlatformSharesAsync(
            tenantId.HasValue ? new TenantId(tenantId.Value) : null,
            parsedStatus,
            cancellationToken);

        return Results.Ok(shares);
    }

    private static async Task<IResult> GetPlatformShareAsync(
        Guid shareId,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        var share = await service.GetPlatformShareAsync(shareId, cancellationToken);
        return share is not null
            ? Results.Ok(share)
            : Results.NotFound(new { error = "share_not_found", message = "Backup share not found." });
    }

    private static async Task<IResult> ExtendShareAsync(
        Guid shareId,
        ExtendBackupShareRequest request,
        ITenantContext tenant,
        ICurrentUser user,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        if (user.UserId is not { } actor)
        {
            return Results.Unauthorized();
        }

        var result = await service.ExtendShareAsync(tenant.TenantId!, shareId, actor, request, cancellationToken);
        return MapModifyResult(result);
    }

    private static async Task<IResult> ExtendShareForArtifactAsync(
        Guid artifactId,
        Guid shareId,
        ExtendBackupShareRequest request,
        ITenantContext tenant,
        ICurrentUser user,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        return await ExtendShareAsync(shareId, request, tenant, user, service, cancellationToken);
    }

    private static async Task<IResult> RevokeShareAsync(
        Guid shareId,
        RevokeBackupShareRequest? request,
        ITenantContext tenant,
        ICurrentUser user,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        if (user.UserId is not { } actor)
        {
            return Results.Unauthorized();
        }

        var result = await service.RevokeShareAsync(tenant.TenantId!, shareId, actor, request ?? new RevokeBackupShareRequest(), cancellationToken);
        return MapModifyResult(result);
    }

    private static async Task<IResult> RevokeShareForArtifactAsync(
        Guid artifactId,
        Guid shareId,
        RevokeBackupShareRequest? request,
        ITenantContext tenant,
        ICurrentUser user,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        return await RevokeShareAsync(shareId, request, tenant, user, service, cancellationToken);
    }

    private static IResult MapModifyResult(BackupShareModifyResult result)
    {
        return result.Outcome switch
        {
            BackupShareModifyOutcome.Success => Results.Ok(result.Share),
            BackupShareModifyOutcome.NotFound => Results.NotFound(new { error = "share_not_found", message = result.ErrorMessage ?? "Backup share not found." }),
            BackupShareModifyOutcome.Forbidden => Results.Forbid(),
            BackupShareModifyOutcome.AlreadyRevoked => Results.Conflict(new { error = result.ErrorCode ?? "already_revoked", message = result.ErrorMessage ?? "Share is already revoked." }),
            BackupShareModifyOutcome.Expired => Results.Conflict(new { error = result.ErrorCode ?? "share_expired", message = result.ErrorMessage ?? "Share is expired." }),
            BackupShareModifyOutcome.InvalidExpiry => Results.BadRequest(new { error = result.ErrorCode ?? "invalid_expiry", message = result.ErrorMessage ?? "Invalid expiry date/time." }),
            _ => Results.BadRequest(new { error = result.ErrorCode ?? "modify_failed", message = result.ErrorMessage ?? "Failed to modify backup share." })
        };
    }

    private static async Task<IResult> ListTenantDownloadAttemptsAsync(
        [FromQuery] Guid? shareId, [FromQuery] Guid? artifactId, [FromQuery] Guid? userId, [FromQuery] string? result, [FromQuery] string? cursor, [FromQuery] int? limit,
        ITenantContext tenant, BackupShareService service, CancellationToken cancellationToken)
    {
        if (!TryPage(result, cursor, limit, out var after, out var size, out var problem)) return problem!;
        return Results.Ok(await service.ListTenantDownloadAttemptsAsync(
            tenant.TenantId!, new DownloadAttemptFilter(shareId, tenant.TenantId!, artifactId, userId, result), after, size, cancellationToken));
    }

    private static async Task<IResult> ListPlatformDownloadAttemptsAsync(
        [FromQuery] Guid? shareId, [FromQuery] Guid? tenantId, [FromQuery] Guid? artifactId, [FromQuery] Guid? userId, [FromQuery] string? result, [FromQuery] string? cursor, [FromQuery] int? limit,
        BackupShareService service, CancellationToken cancellationToken)
    {
        if (!TryPage(result, cursor, limit, out var after, out var size, out var problem)) return problem!;
        return Results.Ok(await service.ListPlatformDownloadAttemptsAsync(
            new DownloadAttemptFilter(shareId, tenantId is { } id ? new TenantId(id) : null, artifactId, userId, result), after, size, cancellationToken));
    }

    private static bool TryPage(string? result, string? cursor, int? limit, out DownloadAttemptCursor? after, out int size, out IResult? problem)
    {
        after = null;
        size = limit ?? DownloadAttemptPage.DefaultLimit;
        problem = !IsValidResultFilter(result) ? Results.BadRequest(new { error = "invalid_result" })
            : size is < 1 or > DownloadAttemptPage.MaximumLimit ? Results.BadRequest(new { error = "invalid_limit" })
            : cursor is not null && !DownloadAttemptCursor.TryParse(cursor, out after) ? Results.BadRequest(new { error = "invalid_cursor" })
            : null;
        return problem is null;
    }

    private static async Task<IResult> GetAuditRetentionAsync(BackupShareService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.GetAuditRetentionAsync(cancellationToken));

    private static async Task<IResult> UpdateAuditRetentionAsync(
        UpdateAuditRetentionRequest? request, ICurrentUser user, BackupShareService service, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } actor) return Results.Unauthorized();
        return request?.RetentionDays is { } days && await service.UpdateAuditRetentionAsync(actor, days, cancellationToken) is { } settings
            ? Results.Ok(settings)
            : Results.BadRequest(new { error = "invalid_retention_days" });
    }

    private static bool IsValidResultFilter(string? result) =>
        string.IsNullOrWhiteSpace(result)
        || string.Equals(result, "Refused", StringComparison.OrdinalIgnoreCase)
        || (Enum.TryParse<DownloadState>(result, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && result.All(char.IsAsciiLetter));
}
