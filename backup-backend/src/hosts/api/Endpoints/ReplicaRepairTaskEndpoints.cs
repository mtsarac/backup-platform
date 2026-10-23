using BackupPlatform.Api.Backups;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Storage;
using BackupPlatform.StorageProviders.GoogleDrive;
using Microsoft.AspNetCore.Mvc;

namespace BackupPlatform.Api.Endpoints;

/// <summary>
/// Agent half of an SFTP or Google Drive replica repair. Every route is fenced by the operation's lease token and the agent that claimed it; the agent names no
/// connection and no key of its own, and the backend carries no payload.
/// </summary>
public static class ReplicaRepairTaskEndpoints
{
    public static IEndpointRouteBuilder MapReplicaRepairTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var repairs = endpoints.MapGroup("/api/agent/replica-repairs/{operationId:guid}")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol(), new RequestSizeLimitAttribute(16 * 1024))
            .RequireAuthorization(AuthorizationPolicies.Agent);
        repairs.MapPost("/lease", RenewAsync);
        repairs.MapPost("/pin", PinAsync);
        repairs.MapPost("/progress", ProgressAsync);
        repairs.MapPost("/cleanup-check", CleanupCheckAsync);
        repairs.MapPost("/complete", CompleteAsync);
        repairs.MapPost("/storage/source/access", (Guid operationId, HttpRequest request, ICurrentAgent agent, ReplicaRepairService service,
            IStorageConnectionStore connections, ISecretProvider secrets, GoogleDriveConnectionProviderFactory driveFactory, CancellationToken cancellationToken) =>
            AccessAsync(operationId, source: true, request, agent, service, connections, secrets, driveFactory, cancellationToken));
        repairs.MapPost("/storage/target/access", (Guid operationId, HttpRequest request, ICurrentAgent agent, ReplicaRepairService service,
            IStorageConnectionStore connections, ISecretProvider secrets, GoogleDriveConnectionProviderFactory driveFactory, CancellationToken cancellationToken) =>
            AccessAsync(operationId, source: false, request, agent, service, connections, secrets, driveFactory, cancellationToken));
        return endpoints;
    }

    private static async Task<IResult> RenewAsync(Guid operationId, ReplicaRepairLeaseRequest request, ICurrentAgent agent,
        ReplicaRepairService service, CancellationToken cancellationToken)
    {
        if (request.LeaseToken == Guid.Empty) return BackupTaskEndpoints.Invalid();
        var lease = await service.RenewForAgentAsync(agent.Identity!, operationId, request.LeaseToken, cancellationToken);
        return lease is null ? BackupTaskEndpoints.Mutation(BackupMutationResult.Conflict) : Results.Ok(lease);
    }

    private static async Task<IResult> PinAsync(Guid operationId, ReplicaRepairPinRequest request, ICurrentAgent agent,
        ReplicaRepairService service, CancellationToken cancellationToken)
    {
        if (request.LeaseToken == Guid.Empty || request.SourceReplicaId == Guid.Empty || string.IsNullOrWhiteSpace(request.TargetKey)) return BackupTaskEndpoints.Invalid();
        try { return BackupTaskEndpoints.Mutation(await service.PinForAgentAsync(agent.Identity!, operationId, request, cancellationToken)); }
        catch (ArgumentException) { return BackupTaskEndpoints.Invalid(); }
    }

    private static async Task<IResult> ProgressAsync(Guid operationId, ReplicaRepairProgressRequest request, ICurrentAgent agent,
        ReplicaRepairService service, CancellationToken cancellationToken)
    {
        if (request.LeaseToken == Guid.Empty) return BackupTaskEndpoints.Invalid();
        try { return BackupTaskEndpoints.Mutation(await service.ProgressForAgentAsync(agent.Identity!, operationId, request, cancellationToken)); }
        catch (ArgumentException) { return BackupTaskEndpoints.Invalid(); }
    }

    private static async Task<IResult> CleanupCheckAsync(Guid operationId, ReplicaRepairCleanupRequest request, ICurrentAgent agent,
        ReplicaRepairService service, CancellationToken cancellationToken)
    {
        if (request.LeaseToken == Guid.Empty) return BackupTaskEndpoints.Invalid();
        try
        {
            return Results.Ok(new ReplicaRepairCleanupResponse
            {
                Allowed = await service.CanCleanupForAgentAsync(agent.Identity!, operationId, request, cancellationToken)
            });
        }
        catch (ArgumentException) { return BackupTaskEndpoints.Invalid(); }
    }

    private static async Task<IResult> CompleteAsync(Guid operationId, ReplicaRepairCompletionRequest request, ICurrentAgent agent,
        ReplicaRepairService service, IGoogleDriveFileCatalog catalog, CancellationToken cancellationToken)
    {
        if (request.LeaseToken == Guid.Empty) return BackupTaskEndpoints.Invalid();
        var tenantId = agent.Identity!.TenantId.Value;
        try
        {
            return BackupTaskEndpoints.Mutation(await service.CompleteForAgentAsync(agent.Identity, operationId, request,
                (connectionId, key, record, token) => catalog.SaveAsync(tenantId, connectionId, key,
                    new GoogleDriveFileRecord(record.FileId, record.ParentFolderId, record.FileName, record.SizeBytes, record.Checksum, record.Md5Checksum), token),
                cancellationToken));
        }
        catch (ArgumentException) { return BackupTaskEndpoints.Invalid(); }
    }

    /// <summary>Credentials for the one connection this repair is entitled to; the connection comes from the operation, never from the request.</summary>
    private static async Task<IResult> AccessAsync(Guid operationId, bool source, HttpRequest request, ICurrentAgent agent, ReplicaRepairService service,
        IStorageConnectionStore connections, ISecretProvider secrets, GoogleDriveConnectionProviderFactory driveFactory, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.Headers[TaskStorageProtocol.LeaseHeader].FirstOrDefault(), out var lease) || lease == Guid.Empty)
            return Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "Missing lease header." });

        var identity = agent.Identity!;
        if (await service.ResolveAccessAsync(identity, operationId, lease, source, cancellationToken) is not { } access)
            return Results.Conflict(new AgentApiError { Error = "lease_conflict", Message = "Invalid lease or storage target." });

        var connection = (await connections.FindAsync(access.ConnectionId, identity.TenantId, cancellationToken))?.Connection;
        if (connection is null || connection.TenantId != identity.TenantId || connection.ProviderType != access.StorageProvider || (!source && !connection.EffectiveEnabled))
            return Results.Conflict(new AgentApiError { Error = "storage_unavailable", Message = "The storage connection is not available." });

        return await AgentStorageTransferEndpoints.ResolveAccessResponseAsync(request, connection, secrets, driveFactory, cancellationToken);
    }
}
