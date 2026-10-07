using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Restores;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;

namespace BackupPlatform.Api.Endpoints;

/// <summary>
/// Restoring a stored backup into a SQL Server. Starting and cancelling a restore change a target database, so they need the operator role; reading needs only the
/// viewer role. A restore is asynchronous work for the target agent: starting one answers 202 with the restore to poll, and an agent never receives anything from these
/// routes: it gets the work from its own poll. Nothing here accepts a secret, a path or a storage key: the target names a credential the agent holds.
/// </summary>
public static class RestoreEndpoints
{
    public static IEndpointRouteBuilder MapRestoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}").WithMetadata(new TenantScopedEndpoint());
        tenant.MapPost("/backup-artifacts/{artifactId:guid}/restore", StartAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapGet("/restores", ListAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/restores/{restoreId:guid}", GetAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapPost("/restores/{restoreId:guid}/cancel", CancelAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        return endpoints;
    }

    private static async Task<IResult> StartAsync(Guid artifactId, StartRestoreRequest request, HttpContext context, ITenantContext tenant, ICurrentUser user, RestoreService service,
        IAgentPushDispatcher push, ILogger<RestoreService> logger, CancellationToken cancellationToken)
    {
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !BackupInputValidation.IsValidKey(keys[0]))
            return Results.BadRequest(new { error = "invalid_request", field = "Idempotency-Key", code = "invalid_value", message = "A valid Idempotency-Key is required (1-128 printable ASCII characters)." });
        RestoreStartResult result;
        try { result = await service.StartAsync(tenant.TenantId!, artifactId, keys[0]!, user.UserId?.Value, request, cancellationToken); }
        catch (InvalidRestoreRequestException exception)
        {
            return Results.BadRequest(new { error = "invalid_request", field = exception.Field, code = exception.Code, message = "Invalid restore request field." });
        }
        switch (result.Outcome)
        {
            case RestoreStartOutcome.Started:
                await HintAsync(push, new AgentId(result.Restore!.AgentId), "WorkAvailable", logger);
                return Results.Accepted(Location(tenant, result.Restore.Id), result.Restore);
            case RestoreStartOutcome.Replayed:
                // The same request again: the original restore, nothing new and no new hint.
                context.Response.Headers.Location = Location(tenant, result.Restore!.Id);
                return Results.Ok(result.Restore);
            case RestoreStartOutcome.ArtifactNotFound:
                return Results.NotFound(new { error = "artifact_not_found", message = "The backup does not exist." });
            case RestoreStartOutcome.AgentNotFound:
                return Results.NotFound(new { error = "agent_not_found", message = "The agent does not exist." });
            case RestoreStartOutcome.IdempotencyConflict:
                return Results.Conflict(new { error = result.Code, message = result.Message });
            case RestoreStartOutcome.AgentNotActive or RestoreStartOutcome.AgentCannotRestore:
                return Results.Json(new { error = result.Code, reason = result.Detail, message = result.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
            default:
                // A replica that cannot be restored from any more is a state conflict; every other refusal is a request this version cannot carry out.
                var status = result.Code switch
                {
                    "replica_not_found" => StatusCodes.Status404NotFound,
                    "replica_not_restorable" => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status422UnprocessableEntity
                };
                return Results.Json(new { error = result.Code, message = result.Message }, statusCode: status);
        }
    }

    private static async Task<IResult> ListAsync(string? status, Guid? artifactId, Guid? agentId, int? page, int? pageSize, ITenantContext tenant, RestoreService service,
        CancellationToken cancellationToken)
    {
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100)
            return Results.BadRequest(new { error = "invalid_request", message = "Page must be positive and page size must be between 1 and 100." });
        RestoreStatus? parsed = null;
        if (status is not null)
        {
            var name = Enum.GetNames<RestoreStatus>().FirstOrDefault(candidate => string.Equals(candidate, status, StringComparison.OrdinalIgnoreCase));
            if (name is null) return Results.BadRequest(new { error = "invalid_request", field = "status", code = "invalid_value", message = "Unknown restore status." });
            parsed = Enum.Parse<RestoreStatus>(name);
        }
        return Results.Ok(await service.ListAsync(tenant.TenantId!, new RestoreQuery(parsed, artifactId, agentId, page ?? 1, pageSize ?? 25), cancellationToken));
    }

    private static async Task<IResult> GetAsync(Guid restoreId, ITenantContext tenant, RestoreService service, CancellationToken cancellationToken)
    {
        var restore = await service.GetAsync(tenant.TenantId!, restoreId, cancellationToken);
        return restore is null ? Results.NotFound() : Results.Ok(restore);
    }

    private static async Task<IResult> CancelAsync(Guid restoreId, ITenantContext tenant, RestoreService service, IAgentPushDispatcher push, ILogger<RestoreService> logger,
        CancellationToken cancellationToken)
    {
        var restore = await service.CancelAsync(tenant.TenantId!, restoreId, cancellationToken);
        if (restore is null) return Results.NotFound();
        await HintAsync(push, new AgentId(restore.AgentId), "CancellationRequested", logger);
        return Results.Ok(restore);
    }

    private static string Location(ITenantContext tenant, Guid restoreId) => $"/api/tenants/{tenant.TenantId!.Value}/restores/{restoreId}";

    private static async Task HintAsync(IAgentPushDispatcher push, AgentId agentId, string reason, ILogger logger)
    {
        try { await push.TrySendPollHintAsync(agentId, reason, CancellationToken.None); }
        catch (Exception exception) { logger.LogWarning(exception, "Restore poll hint delivery failed; durable polling remains authoritative."); }
    }
}
