using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Api.Restores;
using BackupPlatform.Api.Databases;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Restores;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Capabilities;
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
        tenant.MapPost("/backup-artifacts/{artifactId:guid}/restore-precheck", PrecheckAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapPost("/backup-artifacts/{artifactId:guid}/restore-verifications", VerifyAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapPost("/backup-artifacts/{artifactId:guid}/restore-discovery", DiscoverAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapGet("/backup-artifacts/{artifactId:guid}/restore-targets", TargetsAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/restores", ListAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/restores/{restoreId:guid}", GetAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapPost("/restores/{restoreId:guid}/cancel", CancelAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        // Retrying the removal of a verification's own disposable database is routine; removing the only copy of a replaced database is an administrator's decision.
        tenant.MapPost("/restores/{restoreId:guid}/cleanup/retry", RetryCleanupAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapPost("/restores/{restoreId:guid}/rollback-removal", RemoveRollbackAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        return endpoints;
    }

    private static async Task<IResult> StartAsync(Guid artifactId, StartRestoreRequest request, HttpContext context, ITenantContext tenant, ICurrentUser user, RestoreService service,
        DatabaseCredentialVault vault, IAgentPushDispatcher push, ILogger<RestoreService> logger, CancellationToken cancellationToken)
    {
        if (!vault.AcceptsTarget(request.AgentId, request.Target))
            return Results.BadRequest(new { error = "invalid_request", field = "target.databaseCredentialReference", code = "invalid_value", message = "Invalid restore request field." });
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !BackupInputValidation.IsValidKey(keys[0]))
            return Results.BadRequest(new { error = "invalid_request", field = "Idempotency-Key", code = "invalid_value", message = "A valid Idempotency-Key is required (1-128 printable ASCII characters)." });
        RestoreStartResult result;
        try { result = await service.StartAsync(tenant.TenantId!, artifactId, keys[0]!, user.UserId?.Value, request, cancellationToken); }
        catch (InvalidRestoreRequestException exception)
        {
            return Results.BadRequest(new { error = "invalid_request", field = exception.Field, code = exception.Code, message = "Invalid restore request field." });
        }
        return await MapStartResultAsync(result, tenant, context, push, logger);
    }

    private static async Task<IResult> PrecheckAsync(Guid artifactId, StartRestorePrecheckRequest request, HttpContext context, ITenantContext tenant, ICurrentUser user, RestoreService service,
        DatabaseCredentialVault vault, IAgentPushDispatcher push, ILogger<RestoreService> logger, CancellationToken cancellationToken)
    {
        if (!vault.AcceptsTarget(request.AgentId, request.Target))
            return Results.BadRequest(new { error = "invalid_request", field = "target.databaseCredentialReference", code = "invalid_value", message = "Invalid restore request field." });
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !BackupInputValidation.IsValidKey(keys[0]))
            return Results.BadRequest(new { error = "invalid_request", field = "Idempotency-Key", code = "invalid_value", message = "A valid Idempotency-Key is required (1-128 printable ASCII characters)." });
        RestoreStartResult result;
        try { result = await service.PrecheckAsync(tenant.TenantId!, artifactId, keys[0]!, user.UserId?.Value, request, cancellationToken); }
        catch (InvalidRestoreRequestException exception)
        {
            return Results.BadRequest(new { error = "invalid_request", field = exception.Field, code = exception.Code, message = "Invalid restore request field." });
        }
        return await MapStartResultAsync(result, tenant, context, push, logger);
    }

    private static async Task<IResult> VerifyAsync(Guid artifactId, StartRestoreVerificationRequest request, HttpContext context, ITenantContext tenant, ICurrentUser user, RestoreService service,
        DatabaseCredentialVault vault, IAgentPushDispatcher push, ILogger<RestoreService> logger, CancellationToken cancellationToken)
    {
        if (!vault.AcceptsTarget(request.AgentId, request.Target))
            return Results.BadRequest(new { error = "invalid_request", field = "target.databaseCredentialReference", code = "invalid_value", message = "Invalid restore request field." });
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !BackupInputValidation.IsValidKey(keys[0]))
            return Results.BadRequest(new { error = "invalid_request", field = "Idempotency-Key", code = "invalid_value", message = "A valid Idempotency-Key is required (1-128 printable ASCII characters)." });
        RestoreStartResult result;
        try { result = await service.VerifyAsync(tenant.TenantId!, artifactId, keys[0]!, user.UserId?.Value, request, cancellationToken); }
        catch (InvalidRestoreRequestException exception)
        {
            return Results.BadRequest(new { error = "invalid_request", field = exception.Field, code = exception.Code, message = "Invalid restore request field." });
        }
        return await MapStartResultAsync(result, tenant, context, push, logger);
    }

    private static Task<IResult> RetryCleanupAsync(Guid restoreId, ITenantContext tenant, ICurrentUser user, RestoreService service, IAgentPushDispatcher push, ILogger<RestoreService> logger,
        CancellationToken cancellationToken) => RequestCleanupAsync(restoreId, RestoreCleanupKind.VerificationDatabase, null, tenant, user, service, push, logger, cancellationToken);

    private static Task<IResult> RemoveRollbackAsync(Guid restoreId, RestoreCleanupRequest request, ITenantContext tenant, ICurrentUser user, RestoreService service, IAgentPushDispatcher push,
        ILogger<RestoreService> logger, CancellationToken cancellationToken) =>
        RequestCleanupAsync(restoreId, RestoreCleanupKind.RollbackCopy, request.ConfirmDatabaseName, tenant, user, service, push, logger, cancellationToken);

    private static async Task<IResult> RequestCleanupAsync(Guid restoreId, RestoreCleanupKind kind, string? confirmation, ITenantContext tenant, ICurrentUser user, RestoreService service,
        IAgentPushDispatcher push, ILogger<RestoreService> logger, CancellationToken cancellationToken)
    {
        var (outcome, restore) = await service.RequestCleanupAsync(tenant.TenantId!, restoreId, kind, confirmation, user.UserId?.Value, cancellationToken);
        switch (outcome)
        {
            case RestoreCleanupRequestOutcome.Accepted:
                await HintAsync(push, new AgentId(restore!.AgentId), "WorkAvailable", logger);
                return Results.Accepted(Location(tenant, restoreId), restore);
            case RestoreCleanupRequestOutcome.NotFound:
                return Results.NotFound();
            case RestoreCleanupRequestOutcome.ConfirmationMismatch:
                return Results.BadRequest(new { error = "invalid_request", field = "confirmDatabaseName", code = "confirmation_mismatch", message = "Repeat the exact database name to remove it." });
            case RestoreCleanupRequestOutcome.RetentionNotElapsed:
                return Results.Conflict(new { error = "retention_not_elapsed", message = "The rollback copy is still inside its guarded retention period." });
            default:
                return Results.Conflict(new { error = "cleanup_not_applicable", message = "There is no removal to request for this restore right now." });
        }
    }

    private static async Task<IResult> DiscoverAsync(Guid artifactId, StartRestoreDiscoveryRequest request, HttpContext context, ITenantContext tenant, ICurrentUser user, RestoreService service,
        DatabaseCredentialVault vault, IAgentPushDispatcher push, ILogger<RestoreService> logger, CancellationToken cancellationToken)
    {
        if (!vault.AcceptsTarget(request.AgentId, request.Target))
            return Results.BadRequest(new { error = "invalid_request", field = "target.databaseCredentialReference", code = "invalid_value", message = "Invalid restore request field." });
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !BackupInputValidation.IsValidKey(keys[0]))
            return Results.BadRequest(new { error = "invalid_request", field = "Idempotency-Key", code = "invalid_value", message = "A valid Idempotency-Key is required (1-128 printable ASCII characters)." });
        RestoreStartResult result;
        try { result = await service.DiscoverAsync(tenant.TenantId!, artifactId, keys[0]!, user.UserId?.Value, request, cancellationToken); }
        catch (InvalidRestoreRequestException exception)
        {
            return Results.BadRequest(new { error = "invalid_request", field = exception.Field, code = exception.Code, message = "Invalid restore request field." });
        }
        return await MapStartResultAsync(result, tenant, context, push, logger);
    }

    private static async Task<IResult> MapStartResultAsync(RestoreStartResult result, ITenantContext tenant, HttpContext context, IAgentPushDispatcher push, ILogger logger)
    {
        switch (result.Outcome)
        {
            case RestoreStartOutcome.Started:
                await HintAsync(push, new AgentId(result.Restore!.AgentId), "WorkAvailable", logger);
                return Results.Accepted(Location(tenant, result.Restore.Id), result.Restore);
            case RestoreStartOutcome.Replayed:
                context.Response.Headers.Location = Location(tenant, result.Restore!.Id);
                return Results.Ok(result.Restore);
            case RestoreStartOutcome.ArtifactNotFound:
                return Results.NotFound(new { error = "artifact_not_found", message = "The backup does not exist." });
            case RestoreStartOutcome.AgentNotFound:
                return Results.NotFound(new { error = "agent_not_found", message = "The agent does not exist." });
            case RestoreStartOutcome.IdempotencyConflict or RestoreStartOutcome.PrecheckRejected:
                return Results.Conflict(new { error = result.Code, message = result.Message });
            case RestoreStartOutcome.AgentNotActive or RestoreStartOutcome.AgentCannotRestore:
                return Results.Json(new { error = result.Code, reason = result.Detail, message = result.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
            default:
                var status = result.Code switch
                {
                    "replica_not_found" => StatusCodes.Status404NotFound,
                    "replica_not_restorable" => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status422UnprocessableEntity
                };
                return Results.Json(new { error = result.Code, message = result.Message }, statusCode: status);
        }
    }

    private static async Task<IResult> TargetsAsync(Guid artifactId, ITenantContext tenant, RestoreTargetsReader reader, CancellationToken cancellationToken)
    {
        var targets = await reader.ReadAsync(tenant.TenantId!, artifactId, cancellationToken);
        return targets is null ? Results.NotFound(new { error = "artifact_not_found", message = "The backup does not exist." }) : Results.Ok(targets);
    }

    private static async Task<IResult> ListAsync(string? status, Guid? artifactId, Guid? agentId, string? purpose, int? page, int? pageSize, ITenantContext tenant, RestoreService service,
        CancellationToken cancellationToken)
    {
        // Execution restores by default; the isolated verifications of an artifact are the only other list a tenant can ask for.
        var parsedPurpose = RestorePurpose.Execution;
        if (purpose is not null)
        {
            if (!string.Equals(purpose, "Execution", StringComparison.OrdinalIgnoreCase) && !string.Equals(purpose, "Verification", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "invalid_request", field = "purpose", code = "invalid_value", message = "Unknown restore purpose." });
            parsedPurpose = Enum.Parse<RestorePurpose>(purpose, ignoreCase: true);
        }
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100)
            return Results.BadRequest(new { error = "invalid_request", message = "Page must be positive and page size must be between 1 and 100." });
        RestoreStatus? parsed = null;
        if (status is not null)
        {
            var name = Enum.GetNames<RestoreStatus>().FirstOrDefault(candidate => string.Equals(candidate, status, StringComparison.OrdinalIgnoreCase));
            if (name is null) return Results.BadRequest(new { error = "invalid_request", field = "status", code = "invalid_value", message = "Unknown restore status." });
            parsed = Enum.Parse<RestoreStatus>(name);
        }
        return Results.Ok(await service.ListAsync(tenant.TenantId!, new RestoreQuery(parsed, artifactId, agentId, page ?? 1, pageSize ?? 25, Purpose: parsedPurpose), cancellationToken));
    }

    private static async Task<IResult> GetAsync(Guid restoreId, ITenantContext tenant, RestoreService service, CancellationToken cancellationToken)
    {
        var restore = await service.GetAsync(tenant.TenantId!, restoreId, cancellationToken);
        return restore is null ? Results.NotFound() : Results.Ok(restore);
    }

    private static async Task<IResult> CancelAsync(Guid restoreId, ITenantContext tenant, ICurrentUser user, RestoreService service, IAgentPushDispatcher push, ILogger<RestoreService> logger,
        CancellationToken cancellationToken)
    {
        var restore = await service.CancelAsync(tenant.TenantId!, restoreId, cancellationToken, user.UserId?.Value);
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
