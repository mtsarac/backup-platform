using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using Microsoft.AspNetCore.Mvc;

namespace BackupPlatform.Api.Endpoints;

/// <summary>
/// The target agent's side of a claimed restore attempt. Every call is fenced by the attempt id, the lease token and the agent's own identity: an attempt of
/// another agent or tenant is simply not found, a lost or stale lease is a conflict, and a malformed report is a bad request. Start, lease and progress reuse the
/// backup task messages (they carry nothing backup-specific); the completion is restore's own.
/// </summary>
public static class RestoreTaskEndpoints
{
    public static IEndpointRouteBuilder MapRestoreTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var restores = endpoints.MapGroup("/api/agent/restores/{attemptId:guid}")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol(), new RequestSizeLimitAttribute(64 * 1024))
            .RequireAuthorization(AuthorizationPolicies.Agent);
        restores.MapPost("/start", StartAsync);
        restores.MapPost("/lease", RenewAsync);
        restores.MapPost("/progress", ProgressAsync);
        restores.MapPost("/complete", CompleteAsync);
        return endpoints;
    }

    private static async Task<IResult> StartAsync(Guid attemptId, BackupTaskStartRequest request, ICurrentAgent agent, RestoreService service, CancellationToken cancellationToken)
    {
        try { return Lease(await service.StartAttemptAsync(agent.Identity!, attemptId, request, cancellationToken)); }
        catch (ArgumentException) { return BackupTaskEndpoints.Invalid(); }
    }

    private static async Task<IResult> RenewAsync(Guid attemptId, BackupTaskLeaseRequest request, ICurrentAgent agent, RestoreService service, CancellationToken cancellationToken)
    {
        try { return Lease(await service.RenewLeaseAsync(agent.Identity!, attemptId, request, cancellationToken)); }
        catch (ArgumentException) { return BackupTaskEndpoints.Invalid(); }
    }

    private static async Task<IResult> ProgressAsync(Guid attemptId, BackupTaskProgressRequest request, ICurrentAgent agent, RestoreService service, CancellationToken cancellationToken)
    {
        try { return BackupTaskEndpoints.Mutation(await service.ReportProgressAsync(agent.Identity!, attemptId, request, cancellationToken)); }
        catch (ArgumentException) { return BackupTaskEndpoints.Invalid(); }
    }

    private static async Task<IResult> CompleteAsync(Guid attemptId, RestoreTaskCompletionRequest request, ICurrentAgent agent, RestoreService service, CancellationToken cancellationToken)
    {
        try { return BackupTaskEndpoints.Mutation(await service.CompleteAsync(agent.Identity!, attemptId, request, cancellationToken)); }
        catch (ArgumentException) { return BackupTaskEndpoints.Invalid(); }
    }

    private static IResult Lease(RestoreLeaseOutcome outcome) => outcome.Result == BackupMutationResult.Accepted
        ? Results.Ok(new BackupTaskLeaseResponse { LeaseExpiresAt = outcome.LeaseExpiresAt!.Value, CancellationRequested = outcome.CancellationRequested })
        : BackupTaskEndpoints.Mutation(outcome.Result);
}
