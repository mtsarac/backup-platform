using BackupPlatform.Api.Downloads;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Downloads;

namespace BackupPlatform.Api.Endpoints;

public static class DownloadEndpoints
{
    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenants/{tenantId:guid}/backup-artifacts/{artifactId:guid}")
            .WithMetadata(new TenantScopedEndpoint()).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        group.MapGet("/download-availability", AvailabilityAsync);
        group.MapPost("/downloads", PrepareAsync);
        group.MapGet("/downloads/{sessionId:guid}", StatusAsync);
        group.MapGet("/downloads/{sessionId:guid}/content", ContentAsync);
        return endpoints;
    }

    private static async Task<IResult> AvailabilityAsync(Guid artifactId, ITenantContext tenant, DownloadService service, ILogger<DownloadService> logger, CancellationToken cancellationToken)
    {
        try
        {
            var availability = await service.GetAvailabilityAsync(tenant.TenantId!, artifactId, cancellationToken);
            return availability is null ? Results.NotFound() : Results.Ok(availability);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Download availability failed with {ExceptionType} for artifact {ArtifactId}", exception.GetType().Name, artifactId);
            return Results.Json(new { error = "download_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> PrepareAsync(Guid artifactId, PrepareDownloadRequest request, ITenantContext tenant, ICurrentUser user,
        DownloadService service, ILogger<DownloadService> logger, CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.PrepareAsync(tenant.TenantId!, artifactId, user.UserId!.Value, request, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (DownloadUnavailableException exception)
        {
            return exception.Code == "replica_not_found" ? Results.NotFound(new { error = exception.Code }) : Results.Conflict(new { error = exception.Code });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Download request or audit persistence failed with {ExceptionType} for artifact {ArtifactId}", exception.GetType().Name, artifactId);
            return Results.Json(new { error = "download_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> StatusAsync(Guid artifactId, Guid sessionId, ITenantContext tenant, ICurrentUser user,
        DownloadService service, ILogger<DownloadService> logger, CancellationToken cancellationToken)
    {
        try
        {
            var session = await service.GetOwnedAsync(tenant.TenantId!, artifactId, sessionId, user.UserId!.Value, cancellationToken);
            return session is null ? Results.NotFound() : Results.Ok(new DownloadStatus(session.Id, session.State.ToString(), session.FailureCode, session.TransferredBytes));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Download {SessionId} status or expiry audit failed with {ExceptionType}", sessionId, exception.GetType().Name);
            return Results.Json(new { error = "download_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> ContentAsync(Guid artifactId, Guid sessionId, ITenantContext tenant, ICurrentUser user,
        DownloadService service, DownloadStreamer streamer, ILogger<DownloadService> logger, CancellationToken cancellationToken)
    {
        try
        {
            var session = await service.GetOwnedAsync(tenant.TenantId!, artifactId, sessionId, user.UserId!.Value, cancellationToken);
            if (session is null) return Results.NotFound();
            if (session.State != DownloadState.Ready) return Results.Conflict(new { error = session.FailureCode ?? "download_not_ready" });
            return new NativeDownloadResult(session, streamer);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Download {SessionId} authorization or expiry audit failed with {ExceptionType}", sessionId, exception.GetType().Name);
            return Results.Json(new { error = "download_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private sealed class NativeDownloadResult(DownloadSession session, DownloadStreamer streamer) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => streamer.StreamAsync(httpContext, session);
    }
}
