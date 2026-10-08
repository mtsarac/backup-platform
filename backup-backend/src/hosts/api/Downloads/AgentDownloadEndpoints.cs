using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Downloads;
using Microsoft.AspNetCore.Mvc;

namespace BackupPlatform.Api.Downloads;

public static class AgentDownloadEndpoints
{
    public static IEndpointRouteBuilder MapAgentDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var downloads = endpoints.MapGroup("/api/agent/downloads/{sessionId:guid}")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol())
            .RequireAuthorization(AuthorizationPolicies.Agent);
        downloads.MapPost("/start", StartAsync).WithMetadata(new RequestSizeLimitAttribute(64 * 1024));
        downloads.MapPost("/lease", RenewAsync).WithMetadata(new RequestSizeLimitAttribute(64 * 1024));
        downloads.MapPost("/complete", CompleteAsync).WithMetadata(new RequestSizeLimitAttribute(64 * 1024));
        // The expected artifact length and session lease enforce the bound while receiving; the backup may exceed Kestrel's default request size.
        downloads.MapPost("/content", ContentAsync).WithMetadata(new DisableRequestSizeLimitAttribute());
        return endpoints;
    }

    private static Task<IResult> StartAsync(Guid sessionId, BackupTaskStartRequest request, ICurrentAgent agent, AgentDownloadSource source,
        ILogger<AgentDownloadSource> logger, CancellationToken cancellationToken) => GuardAsync(async () =>
    {
        var result = await source.RenewAsync(agent.Identity!, sessionId, request.LeaseToken, cancellationToken);
        return result is null ? Rejected() : Results.Ok(result);
    }, logger, sessionId, cancellationToken);

    private static Task<IResult> RenewAsync(Guid sessionId, BackupTaskLeaseRequest request, ICurrentAgent agent, AgentDownloadSource source,
        ILogger<AgentDownloadSource> logger, CancellationToken cancellationToken) => GuardAsync(async () =>
    {
        var result = await source.RenewAsync(agent.Identity!, sessionId, request.LeaseToken, cancellationToken);
        return result is null ? Rejected() : Results.Ok(result);
    }, logger, sessionId, cancellationToken);

    private static Task<IResult> CompleteAsync(Guid sessionId, DownloadTaskCompletionRequest request, ICurrentAgent agent, AgentDownloadSource source,
        ILogger<AgentDownloadSource> logger, CancellationToken cancellationToken) => GuardAsync(async () =>
    {
        if (request.Status is not ("Failed" or "Cancelled")) return Results.BadRequest();
        return await source.CompleteAsync(agent.Identity!, sessionId, request, cancellationToken) ? Results.NoContent() : Rejected();
    }, logger, sessionId, cancellationToken);

    private static Task<IResult> ContentAsync(Guid sessionId, [FromHeader(Name = DownloadTaskTypes.LeaseHeader)] Guid leaseToken,
        HttpContext context, ICurrentAgent agent, AgentDownloadSource source, ILogger<AgentDownloadSource> logger, CancellationToken cancellationToken) =>
        GuardAsync(async () => await source.ReceiveAsync(agent.Identity!, sessionId, leaseToken, context.Request, cancellationToken)
            ? Results.NoContent() : Rejected(), logger, sessionId, cancellationToken);

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action, ILogger logger, Guid sessionId, CancellationToken cancellationToken)
    {
        try { return await action(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.Empty; }
        catch (Exception exception)
        {
            logger.LogWarning("Agent download request refused: Session={SessionId} Code=download_request_failed Type={ExceptionType}.",
                sessionId, exception.GetType().Name);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult Rejected() => Results.Json(new { error = "download_lease_rejected" }, statusCode: StatusCodes.Status409Conflict);
}
