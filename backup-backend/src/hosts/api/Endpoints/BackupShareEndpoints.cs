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
        tenant.MapGet("/download-attempts", ListTenantDownloadAttemptsAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/backup-shares/download-attempts", ListTenantDownloadAttemptsAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/backup-shares/{shareId:guid}/download-attempts", ListTenantShareDownloadAttemptsAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/backup-shares/{shareId:guid}/attempts", ListTenantShareDownloadAttemptsAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/backup-artifacts/{artifactId:guid}/download-attempts", ListTenantArtifactDownloadAttemptsAsync)
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);

        var platform = endpoints.MapGroup("/api/platform/backup-shares")
            .RequireAuthorization(AuthorizationPolicies.PlatformAdmin);

        platform.MapPost("", CreatePlatformShareAsync);
        platform.MapGet("", ListPlatformSharesAsync);
        platform.MapGet("/{shareId:guid}", GetPlatformShareAsync);
        platform.MapGet("/download-attempts", ListPlatformDownloadAttemptsAsync);
        platform.MapGet("/{shareId:guid}/download-attempts", ListPlatformShareDownloadAttemptsAsync);
        platform.MapGet("/{shareId:guid}/attempts", ListPlatformShareDownloadAttemptsAsync);
        platform.MapGet("/retention", GetAuditRetentionAsync);
        platform.MapPut("/retention", UpdateAuditRetentionAsync);

        var platformRoot = endpoints.MapGroup("/api/platform")
            .RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        platformRoot.MapGet("/download-attempts", ListPlatformDownloadAttemptsAsync);
        platformRoot.MapGet("/settings/audit-retention", GetAuditRetentionAsync);
        platformRoot.MapPut("/settings/audit-retention", UpdateAuditRetentionAsync);
        platformRoot.MapPost("/settings/audit-retention/prune", PruneAuditHistoryAsync);
        platformRoot.MapGet("/audit-retention", GetAuditRetentionAsync);
        platformRoot.MapPut("/audit-retention", UpdateAuditRetentionAsync);
        platformRoot.MapPost("/audit-retention/prune", PruneAuditHistoryAsync);

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
        [FromQuery] Guid? shareId,
        [FromQuery] Guid? tenantId,
        [FromQuery] Guid? artifactId,
        [FromQuery] Guid? userId,
        [FromQuery] Guid? initiatingUserId,
        [FromQuery] Guid? initiatingUser,
        [FromQuery] string? result,
        [FromQuery] string? status,
        ITenantContext tenant,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        var effectiveResult = result ?? status;
        if (!IsValidResultFilter(effectiveResult))
        {
            return Results.BadRequest(new { error = "invalid_result", message = $"Invalid result filter: {effectiveResult}." });
        }

        var filter = new DownloadAttemptFilter(
            ShareId: shareId,
            TenantId: tenantId.HasValue ? new TenantId(tenantId.Value) : null,
            ArtifactId: artifactId,
            InitiatingUserId: userId ?? initiatingUserId ?? initiatingUser,
            Result: effectiveResult);

        var attempts = await service.ListTenantDownloadAttemptsAsync(tenant.TenantId!, filter, cancellationToken);
        return Results.Ok(attempts);
    }

    private static async Task<IResult> ListTenantShareDownloadAttemptsAsync(
        Guid shareId,
        [FromQuery] Guid? userId,
        [FromQuery] Guid? initiatingUserId,
        [FromQuery] Guid? initiatingUser,
        [FromQuery] string? result,
        [FromQuery] string? status,
        ITenantContext tenant,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        var effectiveResult = result ?? status;
        if (!IsValidResultFilter(effectiveResult))
        {
            return Results.BadRequest(new { error = "invalid_result", message = $"Invalid result filter: {effectiveResult}." });
        }

        var filter = new DownloadAttemptFilter(
            ShareId: shareId,
            TenantId: tenant.TenantId!,
            InitiatingUserId: userId ?? initiatingUserId ?? initiatingUser,
            Result: effectiveResult);

        var attempts = await service.ListTenantDownloadAttemptsAsync(tenant.TenantId!, filter, cancellationToken);
        return Results.Ok(attempts);
    }

    private static async Task<IResult> ListTenantArtifactDownloadAttemptsAsync(
        Guid artifactId,
        [FromQuery] Guid? shareId,
        [FromQuery] Guid? userId,
        [FromQuery] Guid? initiatingUserId,
        [FromQuery] Guid? initiatingUser,
        [FromQuery] string? result,
        [FromQuery] string? status,
        ITenantContext tenant,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        var effectiveResult = result ?? status;
        if (!IsValidResultFilter(effectiveResult))
        {
            return Results.BadRequest(new { error = "invalid_result", message = $"Invalid result filter: {effectiveResult}." });
        }

        var filter = new DownloadAttemptFilter(
            ShareId: shareId,
            TenantId: tenant.TenantId!,
            ArtifactId: artifactId,
            InitiatingUserId: userId ?? initiatingUserId ?? initiatingUser,
            Result: effectiveResult);

        var attempts = await service.ListTenantDownloadAttemptsAsync(tenant.TenantId!, filter, cancellationToken);
        return Results.Ok(attempts);
    }

    private static async Task<IResult> ListPlatformDownloadAttemptsAsync(
        [FromQuery] Guid? shareId,
        [FromQuery] Guid? tenantId,
        [FromQuery] Guid? artifactId,
        [FromQuery] Guid? userId,
        [FromQuery] Guid? initiatingUserId,
        [FromQuery] Guid? initiatingUser,
        [FromQuery] string? result,
        [FromQuery] string? status,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        var effectiveResult = result ?? status;
        if (!IsValidResultFilter(effectiveResult))
        {
            return Results.BadRequest(new { error = "invalid_result", message = $"Invalid result filter: {effectiveResult}." });
        }

        var filter = new DownloadAttemptFilter(
            ShareId: shareId,
            TenantId: tenantId.HasValue ? new TenantId(tenantId.Value) : null,
            ArtifactId: artifactId,
            InitiatingUserId: userId ?? initiatingUserId ?? initiatingUser,
            Result: effectiveResult);

        var attempts = await service.ListPlatformDownloadAttemptsAsync(filter, cancellationToken);
        return Results.Ok(attempts);
    }

    private static async Task<IResult> ListPlatformShareDownloadAttemptsAsync(
        Guid shareId,
        [FromQuery] Guid? tenantId,
        [FromQuery] Guid? artifactId,
        [FromQuery] Guid? userId,
        [FromQuery] Guid? initiatingUserId,
        [FromQuery] Guid? initiatingUser,
        [FromQuery] string? result,
        [FromQuery] string? status,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        var effectiveResult = result ?? status;
        if (!IsValidResultFilter(effectiveResult))
        {
            return Results.BadRequest(new { error = "invalid_result", message = $"Invalid result filter: {effectiveResult}." });
        }

        var filter = new DownloadAttemptFilter(
            ShareId: shareId,
            TenantId: tenantId.HasValue ? new TenantId(tenantId.Value) : null,
            ArtifactId: artifactId,
            InitiatingUserId: userId ?? initiatingUserId ?? initiatingUser,
            Result: effectiveResult);

        var attempts = await service.ListPlatformDownloadAttemptsAsync(filter, cancellationToken);
        return Results.Ok(attempts);
    }

    private static async Task<IResult> GetAuditRetentionAsync(
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        var retention = await service.GetAuditRetentionAsync(cancellationToken);
        return Results.Ok(retention);
    }

    private static async Task<IResult> UpdateAuditRetentionAsync(
        UpdateAuditRetentionRequest? request,
        ICurrentUser user,
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        if (user.UserId is not { } actor)
        {
            return Results.Unauthorized();
        }

        var days = request?.RetentionDays ?? request?.Days;
        if (days is null)
        {
            return Results.BadRequest(new { error = "invalid_retention_days", message = "RetentionDays is required." });
        }

        var result = await service.UpdateAuditRetentionAsync(actor, days.Value, cancellationToken);
        return result.Outcome switch
        {
            SaveAuditRetentionOutcome.Success => Results.Ok(result.Settings),
            SaveAuditRetentionOutcome.Forbidden => Results.Forbid(),
            _ => Results.BadRequest(new { error = result.ErrorCode ?? "invalid_retention_days", message = result.ErrorMessage ?? "Invalid retention days." })
        };
    }

    private static async Task<IResult> PruneAuditHistoryAsync(
        BackupShareService service,
        CancellationToken cancellationToken)
    {
        var result = await service.PruneHistoryAsync(cancellationToken);
        return Results.Ok(result);
    }

    private static bool IsValidResultFilter(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return true;
        }

        var trimmed = result.Trim();
        if (string.Equals(trimmed, "Refused", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Enum.TryParse<DownloadState>(trimmed, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            && !trimmed.All(char.IsDigit);
    }
}
