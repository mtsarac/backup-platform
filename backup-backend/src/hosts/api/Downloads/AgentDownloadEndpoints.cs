using BackupPlatform.Api.Backups;
using System.Security.Cryptography;
using BackupPlatform.Api.Endpoints;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.StorageProviders.GoogleDrive;
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
        downloads.MapPost("/signal", SignalAsync).WithMetadata(new RequestSizeLimitAttribute(64 * 1024));
        downloads.MapPost("/artifact-key", KeyAsync).WithMetadata(new RequestSizeLimitAttribute(64 * 1024));
        downloads.MapPost("/storage/access", AccessAsync).WithMetadata(new RequestSizeLimitAttribute(64 * 1024));
        downloads.MapPost("/content", () => Results.Conflict(new { error = DownloadAgentFailureCodes.DirectPathUnavailable }));
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
        if (request.Status is not ("Completed" or "Failed" or "Cancelled")) return Results.BadRequest();
        return await source.CompleteAsync(agent.Identity!, sessionId, request, cancellationToken) ? Results.NoContent() : Rejected();
    }, logger, sessionId, cancellationToken);


    private static async Task<IResult> SignalAsync(Guid sessionId, DownloadSignalRequest request, ICurrentAgent agent, AgentDownloadSource source, CancellationToken cancellationToken)
    {
        var result = await source.SignalAsync(agent.Identity!, sessionId, request, cancellationToken);
        return result is null ? Rejected() : Results.Ok(result);
    }

    private static async Task<IResult> KeyAsync(Guid sessionId, DownloadSignalRequest request, HttpContext http, ICurrentAgent agent,
        AgentDownloadSource source, IDownloadStore store, ITenantWrapKeyStore keys, CancellationToken cancellationToken)
    {
        var session = await source.GetFencedAsync(agent.Identity!, sessionId, request.LeaseToken, cancellationToken);
        if (session is not { Decrypted: true }) return Rejected();
        var artifact = await store.GetArtifactAsync(session.TenantId, session.ArtifactId, cancellationToken);
        if (artifact is not { Format: BackupArtifact.BpeFormat, EncryptionKeyId: { } keyId, EncryptionWrappedDekBase64: { } wrapped }) return Rejected();
        byte[] dek;
        try { dek = await keys.UnwrapDekAsync(session.TenantId, "download", sessionId, keyId, Convert.FromBase64String(wrapped), cancellationToken); }
        catch (TenantWrapKeyUnavailableException) { return Results.Conflict(new { error = "key_unavailable" }); }
        try
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new ArtifactKeyUnwrapResponse { DekBase64 = Convert.ToBase64String(dek) });
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    private static async Task<IResult> AccessAsync(Guid sessionId, HttpRequest request, ICurrentAgent agent, AgentDownloadSource source, IDownloadStore store,
        IStorageConnectionStore connections, ISecretProvider secrets, GoogleDriveConnectionProviderFactory driveFactory, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.Headers[TaskStorageProtocol.LeaseHeader], out var lease)) return Rejected();
        var session = await source.GetFencedAsync(agent.Identity!, sessionId, lease, cancellationToken);
        if (session is null || session.StorageProvider is not ("sftp" or "google-drive")) return Rejected();
        var artifact = await store.GetArtifactAsync(session.TenantId, session.ArtifactId, cancellationToken);
        var replica = artifact?.Replicas.SingleOrDefault(item => item.Id == session.ReplicaId);
        if (replica is null) return Rejected();
        var connection = (await connections.FindAsync(replica.StorageConnectionId, session.TenantId, cancellationToken))?.Connection;
        if (connection is null || connection.ProviderType != session.StorageProvider) return Rejected();
        return await AgentStorageTransferEndpoints.ResolveAccessResponseAsync(request, connection, secrets, driveFactory, cancellationToken);
    }

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
