using BackupPlatform.Api.Backups;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Storage;
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
        tasks.MapPost("/progress", ProgressAsync).WithMetadata(new RequestSizeLimitAttribute(8 * 1024 * 1024));
        tasks.MapPost("/complete", CompleteAsync);
        tasks.MapGet("/retained-transfer-release", CanReleaseRetainedTransferAsync);
        var deletions = endpoints.MapGroup("/api/agent/replica-deletions/{replicaId:guid}")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol(), new RequestSizeLimitAttribute(4 * 1024))
            .RequireAuthorization(AuthorizationPolicies.Agent);
        deletions.MapPost("/lease", RenewDeletionAsync);
        deletions.MapPost("/complete", CompleteDeletionAsync);
        deletions.MapPost("/storage/access", GetDeletionStorageAccessAsync);
        return endpoints;
    }

    private static async Task<IResult> CanReleaseRetainedTransferAsync(Guid attemptId, ICurrentAgent agent,
        IBackupRunStore store, CancellationToken cancellationToken)
    {
        var claim = await store.GetAttemptAsync(agent.Identity!.TenantId, agent.Identity.AgentId, attemptId, cancellationToken);
        return claim is null ? Results.NotFound() : Results.Ok(claim.Attempt.IsTerminal);
    }

    private static async Task<IResult> StartAsync(Guid attemptId, BackupTaskStartRequest request, ICurrentAgent agent,
        BackupOrchestrator service, IBackupRunStore store, UploadBandwidthService bandwidth, CancellationToken cancellationToken)
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
            LeaseExpiresAt = lease.LeaseExpiresAt, CancellationRequested = lease.CancellationRequested, PauseRequested = lease.PauseRequested,
            TimeRulePauseRequested = (await bandwidth.PolicyAsync(command, UploadSliceAction.Observe, cancellationToken))?.PauseRequested == true
        });
    }

    private static async Task<IResult> RenewAsync(Guid attemptId, BackupTaskLeaseRequest request, ICurrentAgent agent,
        BackupOrchestrator service, IBackupRunStore store, UploadBandwidthService bandwidth, CancellationToken cancellationToken)
    {
        if (request.LeaseToken == Guid.Empty) return Invalid();
        var command = service.Command(agent.Identity!, attemptId, request.LeaseToken);
        var lease = await store.RenewLeaseAsync(command, BackupOrchestrator.LeaseDuration, cancellationToken);
        return lease is null ? Mutation(BackupMutationResult.Conflict) : Results.Ok(new BackupTaskLeaseResponse
        {
            LeaseExpiresAt = lease.LeaseExpiresAt, CancellationRequested = lease.CancellationRequested, PauseRequested = lease.PauseRequested,
            TimeRulePauseRequested = (await bandwidth.PolicyAsync(command, UploadSliceAction.Observe, cancellationToken))?.PauseRequested == true
        });
    }

    private static async Task<IResult> ProgressAsync(Guid attemptId, BackupTaskProgressRequest request, ICurrentAgent agent,
        BackupOrchestrator service, IBackupRunStore store, CancellationToken cancellationToken)
    {
        if (!BackupInputValidation.IsValidProgress(request)) return Invalid();
        return Mutation(await store.ReportProgressAsync(service.Command(agent.Identity!, attemptId, request.LeaseToken), new BackupProgress(request.Stage, request.Percent) { Checkpoint = request.Checkpoint }, cancellationToken));
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

    /// <summary>Releases the connection's access only to the agent that holds the live lease of this very removal; the client names neither connection nor key.</summary>
    private static async Task<IResult> GetDeletionStorageAccessAsync(Guid replicaId, HttpRequest request, ICurrentAgent agent, AgentReplicaDeletionService service,
        IStorageConnectionStore connections, ISecretProvider secrets, GoogleDriveConnectionProviderFactory driveFactory, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.Headers[TaskStorageProtocol.LeaseHeader], out var lease) || lease == Guid.Empty) return Invalid();
        var target = await service.AuthorizeStorageAsync(agent.Identity!, replicaId, lease, cancellationToken);
        if (target is null) return Mutation(BackupMutationResult.Conflict);
        var connection = (await connections.FindAsync(target.StorageConnectionId, agent.Identity!.TenantId, cancellationToken))?.Connection;
        if (connection is null || connection.TenantId != agent.Identity.TenantId || connection.ProviderType != target.StorageProvider)
            return Results.Conflict(new AgentApiError { Error = "destination_unavailable", Message = "The storage connection is not available." });
        return await AgentStorageTransferEndpoints.ResolveAccessResponseAsync(request, connection, secrets, driveFactory, cancellationToken);
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
    internal static IResult Invalid() => Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "Invalid task request." });
    internal static IResult Mutation(BackupMutationResult result) => result switch
    {
        BackupMutationResult.Accepted => Results.NoContent(),
        BackupMutationResult.NotFound => Results.NotFound(),
        _ => Results.Conflict(new AgentApiError { Error = "lease_conflict", Message = "The task lease or lifecycle no longer permits this operation." })
    };
}
