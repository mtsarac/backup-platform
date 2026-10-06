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
        return endpoints;
    }

    private static async Task<IResult> ListAsync(Guid? jobId, int? limit, ITenantContext tenant, BackupArtifactService service, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) return Results.BadRequest(new { error = "invalid_request", message = "Limit must be between 1 and 100." });
        return Results.Ok(await service.ListAsync(tenant.TenantId!, jobId, limit ?? 50, cancellationToken));
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
}
