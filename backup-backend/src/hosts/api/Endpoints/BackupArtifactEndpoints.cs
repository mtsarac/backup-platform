using BackupPlatform.Domain.Backups;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;

namespace BackupPlatform.Api.Endpoints;

/// <summary>
/// Stored backups and their deletion. Deleting is destructive, so only tenant administrators may request or cancel it, and a request never
/// deletes anything itself: it starts the grace period, and the retention worker removes the replicas once it is over. No route accepts a path or a storage key.
/// </summary>
public static class BackupArtifactEndpoints
{
    public static IEndpointRouteBuilder MapBackupArtifactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}").WithMetadata(new TenantScopedEndpoint());
        tenant.MapGet("/backup-artifacts", ListAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/backup-artifacts/{artifactId:guid}", GetAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapDelete("/backup-artifacts/{artifactId:guid}", DeleteAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPost("/backup-artifacts/{artifactId:guid}/cancel-delete", CancelDeleteAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPost("/backup-artifacts/{artifactId:guid}/repairs", StartRepairAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapGet("/backup-artifacts/{artifactId:guid}/repairs/{repairId:guid}", GetRepairAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapPost("/backup-artifacts/{artifactId:guid}/repairs/{repairId:guid}/cancel", CancelRepairAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapGet("/backup-artifacts/{artifactId:guid}/repairs", ListRepairsAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(Guid? jobId, Guid? agentId, string? state, DateTimeOffset? from, DateTimeOffset? to,
        int? page, int? pageSize, ITenantContext tenant, BackupArtifactService service, CancellationToken cancellationToken)
    {
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100)
            return Results.BadRequest(new { error = "invalid_request", message = "Page must be positive and page size must be between 1 and 100." });
        if (state is not null && state is not ("Available" or "DeletePending" or "Deleting" or "DeleteFailed" or "Deleted"))
            return Results.BadRequest(new { error = "invalid_request", message = "Unknown artifact state." });
        if (from > to)
            return Results.BadRequest(new { error = "invalid_request", message = "From must not be later than to." });
        var query = new BackupArtifactQuery(jobId, agentId, state, from?.ToUniversalTime(), to?.ToUniversalTime(), page ?? 1, pageSize ?? 25);
        return Results.Ok(await service.QueryAsync(tenant.TenantId!, query, cancellationToken));
    }

    private static async Task<IResult> GetAsync(Guid artifactId, ITenantContext tenant, BackupArtifactService service, CancellationToken cancellationToken)
    {
        var artifact = await service.GetAsync(tenant.TenantId!, artifactId, cancellationToken);
        return artifact is null ? Results.NotFound() : Results.Ok(artifact);
    }

    private static async Task<IResult> DeleteAsync(Guid artifactId, ITenantContext tenant, ICurrentUser user, BackupArtifactService service, CancellationToken cancellationToken)
    {
        var response = await service.RequestDeleteAsync(tenant.TenantId!, artifactId, user.UserId?.Value, cancellationToken);
        return response.Kind switch
        {
            // Asking again while the delete is already pending or under way is the same request: idempotent, not an error.
            BackupArtifactDeleteKind.Requested or BackupArtifactDeleteKind.AlreadyInProgress =>
                Results.Accepted($"/api/tenants/{tenant.TenantId!.Value}/backup-artifacts/{artifactId}", response.Artifact),
            BackupArtifactDeleteKind.AlreadyDeleted =>
                Results.Conflict(new { error = "already_deleted", message = "Every replica of this backup has already been deleted." }),
            _ => Results.NotFound()
        };
    }

    private static async Task<IResult> CancelDeleteAsync(Guid artifactId, ITenantContext tenant, BackupArtifactService service, CancellationToken cancellationToken)
    {
        var response = await service.CancelDeleteAsync(tenant.TenantId!, artifactId, cancellationToken);
        return response.Kind switch
        {
            BackupArtifactCancelKind.Restored => Results.Ok(response.Artifact),
            BackupArtifactCancelKind.NothingToCancel =>
                Results.Conflict(new { error = "not_cancellable", message = "The backup has no pending deletion to cancel, or its physical deletion has already started." }),
            _ => Results.NotFound()
        };
    }

    private static async Task<IResult> StartRepairAsync(
        Guid artifactId,
        StartReplicaRepairRequest request,
        ITenantContext tenant,
        ICurrentUser user,
        ReplicaRepairService service,
        CancellationToken cancellationToken)
    {
        var result = await service.StartRepairAsync(tenant.TenantId!, artifactId, request, user.UserId?.Value, cancellationToken);
        if (!result.Success)
        {
            return result.Code switch
            {
                "artifact_not_found" or "storage_connection_not_found" =>
                    Results.NotFound(new { error = result.Code, message = result.Message }),
                ReplicaRepairFailureCodes.AlreadyVerified or "repair_already_in_progress" or ReplicaRepairFailureCodes.ArtifactExpired or "artifact_deleted" =>
                    Results.Conflict(new { error = result.Code, message = result.Message }),
                "unauthorized_destination" or "storage_connection_disabled" =>
                    Results.BadRequest(new { error = result.Code, message = result.Message }),
                ReplicaRepairFailureCodes.NoVerifiedSourceAvailable or ReplicaRepairFailureCodes.QuotaExceeded =>
                    Results.UnprocessableEntity(new { error = result.Code, message = result.Message }),
                _ => Results.BadRequest(new { error = result.Code ?? "repair_failed", message = result.Message ?? "Failed to start replica repair." })
            };
        }

        return Results.Accepted($"/api/tenants/{tenant.TenantId!.Value}/backup-artifacts/{artifactId}/repairs/{result.Operation!.Id}", result.Operation);
    }

    private static async Task<IResult> GetRepairAsync(
        Guid artifactId,
        Guid repairId,
        ITenantContext tenant,
        ReplicaRepairService service,
        CancellationToken cancellationToken)
    {
        var operation = await service.GetRepairAsync(tenant.TenantId!, artifactId, repairId, cancellationToken);
        return operation is null ? Results.NotFound() : Results.Ok(operation);
    }

    private static async Task<IResult> CancelRepairAsync(
        Guid artifactId,
        Guid repairId,
        ITenantContext tenant,
        ReplicaRepairService service,
        CancellationToken cancellationToken)
    {
        var success = await service.CancelRepairAsync(tenant.TenantId!, artifactId, repairId, cancellationToken);
        if (!success)
            return Results.NotFound(new { error = "repair_not_found", message = "Repair operation not found or cannot be cancelled." });

        var operation = await service.GetRepairAsync(tenant.TenantId!, artifactId, repairId, cancellationToken);
        return Results.Ok(operation);
    }

    private static async Task<IResult> ListRepairsAsync(
        Guid artifactId,
        ITenantContext tenant,
        ReplicaRepairService service,
        CancellationToken cancellationToken)
    {
        var list = await service.ListRepairsAsync(tenant.TenantId!, artifactId, cancellationToken);
        return Results.Ok(list);
    }
}
