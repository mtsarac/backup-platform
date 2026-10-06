using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using Microsoft.AspNetCore.Mvc;

namespace BackupPlatform.Api.Endpoints;

public static class BackupTaskEndpoints
{
    public static IEndpointRouteBuilder MapBackupTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tasks = endpoints.MapGroup("/api/agent/tasks/{attemptId:guid}")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol(), new RequestSizeLimitAttribute(64 * 1024))
            .RequireAuthorization(AuthorizationPolicies.Agent);
        tasks.MapPost("/start", StartAsync);
        tasks.MapPost("/lease", RenewAsync);
        tasks.MapPost("/progress", ProgressAsync);
        tasks.MapPost("/complete", CompleteAsync);
        var deletions = endpoints.MapGroup("/api/agent/replica-deletions/{replicaId:guid}")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol(), new RequestSizeLimitAttribute(4 * 1024))
            .RequireAuthorization(AuthorizationPolicies.Agent);
        deletions.MapPost("/lease", RenewDeletionAsync);
        deletions.MapPost("/complete", CompleteDeletionAsync);
        return endpoints;
    }

    private static async Task<IResult> StartAsync(Guid attemptId, BackupTaskStartRequest request, ICurrentAgent agent,
        BackupOrchestrator service, IBackupRunStore store, CancellationToken cancellationToken)
    {
        if (request.LeaseToken == Guid.Empty || !Bounded(request.SoftwareVersion) || !Bounded(request.ProtocolVersion) || !Bounded(request.Platform)) return Invalid();
        var command = service.Command(agent.Identity!, attemptId, request.LeaseToken) with
        {
            SoftwareVersion = request.SoftwareVersion, ProtocolVersion = request.ProtocolVersion, Platform = request.Platform
        };
        var result = await store.StartAttemptAsync(command, cancellationToken);
        if (result != BackupMutationResult.Accepted) return Mutation(result);
        var lease = await store.RenewLeaseAsync(command, BackupOrchestrator.LeaseDuration, cancellationToken);
        return lease is null ? Mutation(BackupMutationResult.Conflict) : Results.Ok(new BackupTaskLeaseResponse
        {
            LeaseExpiresAt = lease.LeaseExpiresAt, CancellationRequested = lease.CancellationRequested
        });
    }

    private static async Task<IResult> RenewAsync(Guid attemptId, BackupTaskLeaseRequest request, ICurrentAgent agent,
        BackupOrchestrator service, IBackupRunStore store, CancellationToken cancellationToken)
    {
        if (request.LeaseToken == Guid.Empty) return Invalid();
        var lease = await store.RenewLeaseAsync(service.Command(agent.Identity!, attemptId, request.LeaseToken), BackupOrchestrator.LeaseDuration, cancellationToken);
        return lease is null ? Mutation(BackupMutationResult.Conflict) : Results.Ok(new BackupTaskLeaseResponse
        {
            LeaseExpiresAt = lease.LeaseExpiresAt, CancellationRequested = lease.CancellationRequested
        });
    }

    private static async Task<IResult> ProgressAsync(Guid attemptId, BackupTaskProgressRequest request, ICurrentAgent agent,
        BackupOrchestrator service, IBackupRunStore store, CancellationToken cancellationToken)
    {
        if (!BackupInputValidation.IsValidProgress(request)) return Invalid();
        return Mutation(await store.ReportProgressAsync(service.Command(agent.Identity!, attemptId, request.LeaseToken), new BackupProgress(request.Stage, request.Percent), cancellationToken));
    }

    private static async Task<IResult> CompleteAsync(Guid attemptId, BackupTaskCompletionRequest request, ICurrentAgent agent,
        BackupOrchestrator service, CancellationToken cancellationToken)
    {
        try { return Mutation(await service.CompleteAsync(agent.Identity!, attemptId, request, cancellationToken)); }
        catch (ArgumentException) { return Invalid(); }
    }
    private static async Task<IResult> RenewDeletionAsync(Guid replicaId, DeleteReplicaLeaseRequest request, ICurrentAgent agent,
        AgentReplicaDeletionService service, CancellationToken cancellationToken)
    {
        if (request.OperationId == Guid.Empty || request.LeaseToken == Guid.Empty) return Invalid();
        var lease = await service.RenewAsync(agent.Identity!, replicaId, request, cancellationToken);
        return lease is null ? Mutation(BackupMutationResult.Conflict) : Results.Ok(lease);
    }

    private static async Task<IResult> CompleteDeletionAsync(Guid replicaId, DeleteReplicaCompletionRequest request, ICurrentAgent agent,
        AgentReplicaDeletionService service, CancellationToken cancellationToken)
    {
        if (request.OperationId == Guid.Empty || request.LeaseToken == Guid.Empty
            || request.AbsenceVerified != (request.FailureCode is null)) return Invalid();
        try { return Mutation(await service.CompleteAsync(agent.Identity!, replicaId, request, cancellationToken)); }
        catch (ArgumentException) { return Invalid(); }
    }


    private static bool Bounded(string? value) => value is { Length: > 0 and <= 64 } && !value.Any(char.IsControl);
    private static IResult Invalid() => Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "Invalid task request." });
    private static IResult Mutation(BackupMutationResult result) => result switch
    {
        BackupMutationResult.Accepted => Results.NoContent(),
        BackupMutationResult.NotFound => Results.NotFound(),
        _ => Results.Conflict(new AgentApiError { Error = "lease_conflict", Message = "The task lease or lifecycle no longer permits this operation." })
    };
}
