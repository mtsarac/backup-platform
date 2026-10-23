using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Api.Downloads;
using BackupPlatform.Api.Restores;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Restores;
using BackupPlatform.Application.Support;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Support;

namespace BackupPlatform.Api.Support;

public sealed class OpenSupportSessionRequest
{
    public Guid? TenantId { get; init; }
    public string? Reason { get; init; }
    public string[]? Permissions { get; init; }
    public int? DurationMinutes { get; init; }
}

public sealed class RevokeSupportSessionRequest
{
    public string? Reason { get; init; }
}

/// <summary>A support restore names one of the tenant's backup jobs as its target; the host, port, authentication and agent come from that job, never from the caller.</summary>
public sealed class SupportRestoreRequest
{
    public Guid? TargetJobId { get; init; }
    public string? DatabaseName { get; init; }
    public Guid? ReplicaId { get; init; }
    public string? Mode { get; init; }
    public RestoreOptionsRequest? Options { get; init; }
    public Guid? PrecheckId { get; init; }
}

/// <summary>
/// Platform support access. Every route requires the PlatformAdmin policy, and PlatformAdmin alone reaches no customer data: the data routes also pass
/// <see cref="SupportRequestGate"/> (an active session owned by the caller, with the permission). The tenant is always the session's, never the request's.
/// There is deliberately no PlatformAdmin artifact, download or restore route outside a session. Downloads and restores run on the M4 and M5 engines.
/// </summary>
public static class SupportEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaximumPageSize = 100;
    public const string EnvironmentWarning = "These targets are the customer's own database environment and may be a production system. A support restore here is not an isolated Support Restore Environment.";

    public static IEndpointRouteBuilder MapSupportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/support-sessions").RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        group.MapPost("", OpenAsync);
        group.MapGet("", ListAsync);
        group.MapGet("/{sessionId:guid}", GetAsync);
        group.MapPost("/{sessionId:guid}/close", CloseAsync);
        group.MapPost("/{sessionId:guid}/revoke", RevokeAsync);
        group.MapGet("/{sessionId:guid}/audit", AuditAsync);

        group.MapGet("/{sessionId:guid}/backup-jobs", JobsAsync);
        group.MapGet("/{sessionId:guid}/backup-runs", RunsAsync);
        group.MapGet("/{sessionId:guid}/backup-runs/{runId:guid}", RunAsync);
        group.MapGet("/{sessionId:guid}/artifacts", ArtifactsAsync);
        group.MapGet("/{sessionId:guid}/artifacts/{artifactId:guid}", ArtifactAsync);

        group.MapGet("/{sessionId:guid}/artifacts/{artifactId:guid}/download-availability", AvailabilityAsync);
        group.MapPost("/{sessionId:guid}/artifacts/{artifactId:guid}/downloads", PrepareDownloadAsync);
        group.MapGet("/{sessionId:guid}/artifacts/{artifactId:guid}/downloads/{downloadId:guid}", DownloadStatusAsync);
        group.MapGet("/{sessionId:guid}/artifacts/{artifactId:guid}/downloads/{downloadId:guid}/content", DownloadContentAsync);
        group.MapPost("/{sessionId:guid}/artifacts/{artifactId:guid}/downloads/{downloadId:guid}/content", DownloadContentAsync).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024));
        group.MapGet("/{sessionId:guid}/downloads", DownloadsAsync);

        group.MapGet("/{sessionId:guid}/artifacts/{artifactId:guid}/restore-targets", RestoreTargetsAsync);
        group.MapPost("/{sessionId:guid}/artifacts/{artifactId:guid}/restore-precheck", RestorePrecheckAsync);
        group.MapPost("/{sessionId:guid}/artifacts/{artifactId:guid}/restore", RestoreStartAsync);
        group.MapGet("/{sessionId:guid}/restores", RestoresAsync);
        group.MapGet("/{sessionId:guid}/restores/{restoreId:guid}", RestoreAsync);
        group.MapPost("/{sessionId:guid}/restores/{restoreId:guid}/cancel", RestoreCancelAsync);
        return endpoints;
    }

    // ---- session management ----

    private static async Task<IResult> OpenAsync(OpenSupportSessionRequest? request, HttpContext http, ICurrentUser user, SupportAccessService service, ILogger<SupportAccessService> logger,
        CancellationToken cancellationToken)
    {
        var keys = http.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !BackupInputValidation.IsValidKey(keys[0])) return Invalid("Idempotency-Key", "invalid_value");
        if (request?.TenantId is not { } tenantGuid || tenantGuid == Guid.Empty) return Invalid("tenantId", "required");
        try
        {
            var result = await service.OpenAsync(new TenantId(tenantGuid), user.UserId!, request.Reason, request.Permissions, request.DurationMinutes, keys[0], cancellationToken);
            switch (result.Outcome)
            {
                case SupportOpenOutcome.Opened or SupportOpenOutcome.Replayed:
                    var view = await ViewAsync(service, result.Session!.Id, user.UserId!, cancellationToken);
                    return result.Outcome == SupportOpenOutcome.Opened
                        ? Results.Json(view, statusCode: StatusCodes.Status201Created)
                        : Results.Ok(view);
                case SupportOpenOutcome.TenantNotFound:
                    return Results.NotFound(new { error = "tenant_not_found" });
                case SupportOpenOutcome.IdempotencyConflict:
                    return Results.Conflict(new { error = "idempotency_conflict" });
                case SupportOpenOutcome.ActiveSessionExists:
                    return Results.Conflict(new { error = "active_session_exists", sessionId = result.ExistingSessionId });
                default:
                    return Results.Conflict(new { error = "tenant_approval_required" });
            }
        }
        catch (InvalidSupportRequestException exception)
        {
            return Invalid(exception.Field, exception.Code);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Opening a support session failed with {ExceptionType}; nothing was opened", exception.GetType().Name);
            return SupportRequestGate.Unavailable();
        }
    }

    private static async Task<IResult> ListAsync(Guid? tenantId, string? status, bool? mine, int? page, int? pageSize, ICurrentUser user, SupportAccessService service,
        TimeProvider time, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size)) return Invalid("page", "invalid_value");
        SupportSessionStatus? parsed = null;
        if (status is not null)
        {
            var name = Enum.GetNames<SupportSessionStatus>().FirstOrDefault(candidate => string.Equals(candidate, status, StringComparison.OrdinalIgnoreCase));
            if (name is null) return Invalid("status", "invalid_value");
            parsed = Enum.Parse<SupportSessionStatus>(name);
        }
        var result = await service.ListAsync(tenantId is { } id && id != Guid.Empty ? new TenantId(id) : null, parsed, mine == true ? user.UserId : null, p, size, cancellationToken);
        var now = time.GetUtcNow();
        return Results.Ok(Page(result, row => SupportViews.ToView(row, user.UserId!, now)));
    }

    private static async Task<IResult> GetAsync(Guid sessionId, ICurrentUser user, SupportAccessService service, CancellationToken cancellationToken)
    {
        var view = await ViewAsync(service, sessionId, user.UserId!, cancellationToken);
        return view is null ? SupportRequestGate.NotFound() : Results.Ok(view);
    }

    private static async Task<IResult> CloseAsync(Guid sessionId, ICurrentUser user, SupportAccessService service, ILogger<SupportAccessService> logger, CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.CloseAsync(sessionId, user.UserId!, cancellationToken);
            return result.Outcome switch
            {
                SupportEndOutcome.NotFound => SupportRequestGate.NotFound(),
                SupportEndOutcome.NotOwner => Results.Json(new { error = "not_session_owner" }, statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Ok(await ViewAsync(service, sessionId, user.UserId!, cancellationToken))
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Closing support session {SessionId} failed with {ExceptionType}", sessionId, exception.GetType().Name);
            return SupportRequestGate.Unavailable();
        }
    }

    private static async Task<IResult> RevokeAsync(Guid sessionId, RevokeSupportSessionRequest? request, ICurrentUser user, SupportAccessService service, ILogger<SupportAccessService> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.RevokeAsync(sessionId, user.UserId!, request?.Reason, cancellationToken);
            return result.Outcome switch
            {
                SupportEndOutcome.NotFound => SupportRequestGate.NotFound(),
                SupportEndOutcome.AlreadyEnded => Results.Conflict(new { error = "session_not_active" }),
                _ => Results.Ok(await ViewAsync(service, sessionId, user.UserId!, cancellationToken))
            };
        }
        catch (InvalidSupportRequestException exception)
        {
            return Invalid(exception.Field, exception.Code);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Revoking support session {SessionId} failed with {ExceptionType}", sessionId, exception.GetType().Name);
            return SupportRequestGate.Unavailable();
        }
    }

    private static async Task<IResult> AuditAsync(Guid sessionId, int? page, int? pageSize, SupportAccessService service, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size)) return Invalid("page", "invalid_value");
        if (await service.GetAsync(sessionId, cancellationToken) is null) return SupportRequestGate.NotFound();
        var result = await service.ListAuditAsync(sessionId, p, size, cancellationToken);
        return Results.Ok(Page(result, SupportViews.ToAuditView));
    }

    // ---- ViewBackupMetadata ----

    private static async Task<IResult> JobsAsync(Guid sessionId, SupportRequestGate gate, IBackupUiStore store, TimeProvider time, CancellationToken cancellationToken)
    {
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.ViewBackupMetadata, Audit(SupportAuditActions.BackupViewed, SupportAuditActions.SessionTargetType, sessionId.ToString("D"), "jobs"),
            "backup_jobs.list", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        var jobs = await store.ListJobsAsync(context.TenantId, time.GetUtcNow(), cancellationToken);
        return Results.Ok(jobs.Select(SupportViews.ToView).ToList());
    }

    private static async Task<IResult> RunsAsync(Guid sessionId, Guid? jobId, string? status, int? page, int? pageSize, SupportRequestGate gate, IBackupUiStore store,
        CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size)) return Invalid("page", "invalid_value");
        if (status is not null && status != "CancellationRequested" && !Enum.GetNames<BackupPlatform.Domain.Backups.BackupStatus>().Contains(status, StringComparer.OrdinalIgnoreCase))
            return Invalid("status", "invalid_value");
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.ViewBackupMetadata, Audit(SupportAuditActions.BackupViewed, SupportAuditActions.SessionTargetType, sessionId.ToString("D"), "runs"),
            "backup_runs.list", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        var result = await store.ListRunsAsync(context.TenantId, new BackupRunQuery(p, size, jobId, null, status), cancellationToken);
        return Results.Ok(new { items = result.Items.Select(SupportViews.ToView).ToList(), totalCount = result.TotalCount, page = result.Page, pageSize = result.PageSize });
    }

    private static async Task<IResult> RunAsync(Guid sessionId, Guid runId, SupportRequestGate gate, IBackupUiStore store, CancellationToken cancellationToken)
    {
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.ViewBackupMetadata, Audit(SupportAuditActions.BackupViewed, "backup_run", runId.ToString("D"), "run"),
            "backup_run.get", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        var run = await store.GetRunAsync(context.TenantId, runId, cancellationToken);
        return run is null ? Results.NotFound() : Results.Ok(SupportViews.ToView(run));
    }

    private static async Task<IResult> ArtifactsAsync(Guid sessionId, Guid? jobId, string? state, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize,
        SupportRequestGate gate, BackupArtifactService artifacts, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size)) return Invalid("page", "invalid_value");
        if (state is not null && state is not ("Available" or "DeletePending" or "Deleting" or "DeleteFailed" or "Deleted")) return Invalid("state", "invalid_value");
        if (from > to) return Invalid("from", "invalid_date_range");
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.ViewBackupMetadata,
            Audit(SupportAuditActions.ArtifactListed, SupportAuditActions.SessionTargetType, sessionId.ToString("D"), "artifacts"), "artifacts.list", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        var result = await artifacts.QueryAsync(context.TenantId, new BackupArtifactQuery(jobId, null, state, from?.ToUniversalTime(), to?.ToUniversalTime(), p, size), cancellationToken);
        return Results.Ok(new { items = result.Items.Select(SupportViews.ToView).ToList(), totalCount = result.TotalCount, page = result.Page, pageSize = result.PageSize });
    }

    private static async Task<IResult> ArtifactAsync(Guid sessionId, Guid artifactId, SupportRequestGate gate, BackupArtifactService artifacts, CancellationToken cancellationToken)
    {
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.ViewBackupMetadata, Audit(SupportAuditActions.ArtifactViewed, SupportAuditActions.ArtifactTargetType, artifactId.ToString("D"), "detail"),
            "artifact.get", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        var artifact = await artifacts.GetAsync(context.TenantId, artifactId, cancellationToken);
        return artifact is null ? Results.NotFound() : Results.Ok(SupportViews.ToView(artifact));
    }

    // ---- DownloadBackup (the M4 engine) ----

    private static async Task<IResult> AvailabilityAsync(Guid sessionId, Guid artifactId, SupportRequestGate gate, DownloadService downloads, ILogger<DownloadService> logger,
        CancellationToken cancellationToken)
    {
        // Not audited per call: the UI re-asks while a download panel is open, and what matters is audited where it happens (the artifact view, the download request,
        // its start and its end). The session, its owner and the permission are still judged, and a refusal is audited.
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.DownloadBackup, null, "download.availability", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        try
        {
            var availability = await downloads.GetAvailabilityAsync(context.TenantId, artifactId, cancellationToken);
            // Where the bytes are stored (provider target) is not shown to support; only whether and why a copy can be downloaded.
            return availability is null ? Results.NotFound() : Results.Ok(new
            {
                available = availability.Available, reason = availability.Reason, checksum = availability.Checksum, sizeBytes = availability.SizeBytes,
                replicas = availability.Replicas.Select(replica => new { replicaId = replica.ReplicaId, storageProvider = replica.StorageProvider, available = replica.Available, reason = replica.Reason })
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Support download availability failed with {ExceptionType} for artifact {ArtifactId}", exception.GetType().Name, artifactId);
            return Results.Json(new { error = "download_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> PrepareDownloadAsync(Guid sessionId, Guid artifactId, PrepareDownloadRequest? request, SupportRequestGate gate, DownloadService downloads,
        ICurrentUser user, ILogger<DownloadService> logger, CancellationToken cancellationToken)
    {
        // The authorization is checked here for a prompt answer; the download's own audit ("download requested") is written by the store in the same transaction that
        // creates the download and that re-judges the session under its lock, so there is no gap between the check and the use.
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.DownloadBackup, null, "download.request", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        try
        {
            var result = await downloads.PrepareAsync(context.TenantId, artifactId, context.ActorId.Value, request ?? new PrepareDownloadRequest(), cancellationToken, context.SessionId);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (SupportAccessDeniedException exception)
        {
            return await gate.DeniedAsync(context, exception.Denial, SupportPermissions.DownloadBackup, SupportAuditActions.ArtifactTargetType, artifactId.ToString("D"), "download.request", cancellationToken);
        }
        catch (DownloadUnavailableException exception)
        {
            return exception.Code switch
            {
                "replica_not_found" => Results.NotFound(new { error = exception.Code }),
                "decrypt_not_permitted" => Results.Json(new { error = exception.Code }, statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Conflict(new { error = exception.Code })
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Support download request or audit persistence failed with {ExceptionType} for artifact {ArtifactId}", exception.GetType().Name, artifactId);
            return Results.Json(new { error = "download_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> DownloadStatusAsync(Guid sessionId, Guid artifactId, Guid downloadId, SupportRequestGate gate, DownloadService downloads,
        ILogger<DownloadService> logger, CancellationToken cancellationToken)
    {
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.DownloadBackup, null, "download.status", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        try
        {
            var session = await downloads.GetOwnedAsync(context.TenantId, artifactId, downloadId, context.ActorId.Value, cancellationToken, context.SessionId);
            return session is null ? Results.NotFound() : Results.Ok(new DownloadStatus(session.Id, session.State.ToString(), session.FailureCode, session.TransferredBytes));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Support download {DownloadId} status or expiry audit failed with {ExceptionType}", downloadId, exception.GetType().Name);
            return Results.Json(new { error = "download_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> DownloadContentAsync(Guid sessionId, Guid artifactId, Guid downloadId, SupportRequestGate gate, DownloadService downloads, DownloadStreamer streamer,
        DirectDownloadSignals signals, HttpContext http, ILogger<DownloadService> logger, CancellationToken cancellationToken)
    {
        // The claim is bound to the real admin and to this support session: another admin, or a download of another session or of the tenant's own users, is not found.
        // Streaming start re-judges the session in the database under its lock and audits "download started" in the same transaction (fail-closed).
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.DownloadBackup, null, "download.content", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        try
        {
            var session = await downloads.GetOwnedAsync(context.TenantId, artifactId, downloadId, context.ActorId.Value, cancellationToken, context.SessionId);
            if (session is null) return Results.NotFound();
            if (session.AgentId is not null) return await signals.BrowserAsync(http, session);
            if (!HttpMethods.IsGet(http.Request.Method)) return Results.BadRequest();
            if (session.State != DownloadState.Ready) return Results.Conflict(new { error = session.FailureCode ?? "download_not_ready" });
            return new StreamResult(session, streamer);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Support download {DownloadId} authorization or expiry audit failed with {ExceptionType}", downloadId, exception.GetType().Name);
            return Results.Json(new { error = "download_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> DownloadsAsync(Guid sessionId, int? page, int? pageSize, SupportRequestGate gate, ISupportQueryStore queries, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size)) return Invalid("page", "invalid_value");
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.ViewBackupMetadata, null, "downloads.list", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        var result = await queries.ListDownloadsAsync(context.TenantId, context.SessionId, p, size, cancellationToken);
        return Results.Ok(new
        {
            items = result.Items.Select(download => new SupportDownloadView(download.Id, download.ArtifactId, download.ReplicaId, download.State.ToString(), download.FailureCode,
                download.TransferredBytes, download.SizeBytes, download.Filename, download.CreatedAt, download.ReleasedAt)).ToList(),
            totalCount = result.TotalCount, page = result.Page, pageSize = result.PageSize
        });
    }

    // ---- RestoreBackup (the M5 engine, NewDatabase only) ----

    private static async Task<IResult> RestoreTargetsAsync(Guid sessionId, Guid artifactId, SupportRequestGate gate, RestoreTargetsReader reader, CancellationToken cancellationToken)
    {
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.RestoreBackup,
            Audit(SupportAuditActions.ArtifactViewed, SupportAuditActions.ArtifactTargetType, artifactId.ToString("D"), "restore_targets"), "restore.targets", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        var targets = await reader.ReadAsync(context.TenantId, artifactId, cancellationToken);
        if (targets is null) return Results.NotFound(new { error = "artifact_not_found" });
        return Results.Ok(new
        {
            artifactId = targets.ArtifactId, sourceDatabase = targets.SourceDatabase, backupType = targets.BackupType, sizeBytes = targets.SizeBytes, engine = targets.Engine,
            // The targets are the customer's real SQL Servers, taken from its backup jobs: say so, loudly, and never imply isolation.
            environment = new { kind = "CustomerEnvironment", isolated = false, warning = EnvironmentWarning },
            agents = targets.Agents.Select(agent => new
            {
                agent.AgentId, agent.HostName, agent.Status, agent.Connectivity, agent.CanRestore, agent.CanPrecheck, agent.SourceEligible, agent.SourceAccess, agent.RejectionCode, agent.RejectionMessage
            }),
            targets = targets.Targets.Select(target => new
            {
                target.JobId, target.JobName, target.AgentId, target.Database, host = target.Target.Host, port = target.Target.Port, integratedSecurity = target.Target.IntegratedSecurity
            })
        });
    }

    private static Task<IResult> RestorePrecheckAsync(Guid sessionId, Guid artifactId, SupportRestoreRequest? request, HttpContext http, SupportRequestGate gate, [FromServices] SupportRestoreDeps deps,
        CancellationToken cancellationToken) => RestoreCreateAsync(sessionId, artifactId, request, http, gate, deps, precheck: true, cancellationToken);

    private static Task<IResult> RestoreStartAsync(Guid sessionId, Guid artifactId, SupportRestoreRequest? request, HttpContext http, SupportRequestGate gate, [FromServices] SupportRestoreDeps deps,
        CancellationToken cancellationToken) => RestoreCreateAsync(sessionId, artifactId, request, http, gate, deps, precheck: false, cancellationToken);

    private static async Task<IResult> RestoreCreateAsync(Guid sessionId, Guid artifactId, SupportRestoreRequest? request, HttpContext http, SupportRequestGate gate, [FromServices] SupportRestoreDeps deps,
        bool precheck, CancellationToken cancellationToken)
    {
        var attempted = precheck ? "restore.precheck" : "restore.start";
        var keys = http.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !BackupInputValidation.IsValidKey(keys[0])) return Invalid("Idempotency-Key", "invalid_value");
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.RestoreBackup, null, attempted, cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        // A destructive restore is not part of support access: refuse it by name, audited, before anything is looked at.
        if (!string.IsNullOrEmpty(request?.Mode) && !string.Equals(request.Mode, nameof(RestoreMode.NewDatabase), StringComparison.OrdinalIgnoreCase))
            return await gate.DeniedAsync(context, SupportAccessDenial.ReplaceForbidden, SupportPermissions.RestoreBackup, SupportAuditActions.ArtifactTargetType, artifactId.ToString("D"), attempted, cancellationToken);
        if (request?.TargetJobId is not { } jobId || jobId == Guid.Empty) return Invalid("targetJobId", "required");
        if (!precheck && request.PrecheckId is null) return Invalid("precheckId", "required");
        var job = await deps.Jobs.GetJobAsync(context.TenantId, jobId, deps.Time.GetUtcNow(), cancellationToken);
        if (job is null) return Results.NotFound(new { error = "target_not_found" });
        var allowedTargets = await deps.Targets.ReadAsync(context.TenantId, artifactId, cancellationToken);
        if (allowedTargets is null) return Results.NotFound(new { error = "artifact_not_found" });
        if (!allowedTargets.Targets.Any(target => target.JobId == jobId)) return Invalid("targetJobId", "engine_mismatch");
        var configuration = job.Configuration;
        var target = new RestoreTargetRequest(configuration.Host, configuration.Port, configuration.IntegratedSecurity, configuration.DatabaseCredentialReference, configuration.Encrypt,
            configuration.TrustServerCertificate);
        // The key lives in the support session's own namespace: it can neither replay nor collide with the tenant's restores, nor with another session's.
        var key = $"s{context.SessionId:N}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keys[0]!)))[..48]}";
        try
        {
            var result = precheck
                ? await deps.Restores.PrecheckAsync(context.TenantId, artifactId, key, context.ActorId.Value,
                    new StartRestorePrecheckRequest(job.AgentId, target, request.DatabaseName ?? string.Empty, request.ReplicaId, null, request.Options), cancellationToken, context.SessionId)
                : await deps.Restores.StartAsync(context.TenantId, artifactId, key, context.ActorId.Value,
                    new StartRestoreRequest(job.AgentId, target, request.DatabaseName ?? string.Empty, request.ReplicaId, null, null, request.Options, request.PrecheckId), cancellationToken, context.SessionId);
            return await MapRestoreAsync(result, context, artifactId, attempted, http, gate, deps, cancellationToken);
        }
        catch (InvalidRestoreRequestException exception) when (exception.Code == "replica_not_found")
        {
            return Results.NotFound(new { error = "replica_not_found" });
        }
        catch (InvalidRestoreRequestException exception)
        {
            return Results.BadRequest(new { error = "invalid_request", field = exception.Field, code = exception.Code, message = "Invalid restore request field." });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            deps.Logger.LogError("Support restore request or audit persistence failed with {ExceptionType} for artifact {ArtifactId}", exception.GetType().Name, artifactId);
            return SupportRequestGate.Unavailable();
        }
    }

    private static async Task<IResult> MapRestoreAsync(RestoreStartResult result, SupportAccessContext context, Guid artifactId, string attempted, HttpContext http, SupportRequestGate gate,
        SupportRestoreDeps deps, CancellationToken cancellationToken)
    {
        string Location(Guid restoreId) => $"/api/platform/support-sessions/{context.SessionId}/restores/{restoreId}";
        switch (result.Outcome)
        {
            case RestoreStartOutcome.Started:
                await HintAsync(deps, new AgentId(result.Restore!.AgentId), "WorkAvailable");
                return Results.Accepted(Location(result.Restore.Id), Sanitized(result.Restore));
            case RestoreStartOutcome.Replayed:
                http.Response.Headers.Location = Location(result.Restore!.Id);
                return Results.Ok(Sanitized(result.Restore));
            case RestoreStartOutcome.ArtifactNotFound:
                return Results.NotFound(new { error = "artifact_not_found", message = "The backup does not exist." });
            case RestoreStartOutcome.AgentNotFound:
                return Results.NotFound(new { error = "agent_not_found", message = "The agent does not exist." });
            case RestoreStartOutcome.IdempotencyConflict or RestoreStartOutcome.PrecheckRejected:
                return Results.Conflict(new { error = result.Code, message = result.Message });
            case RestoreStartOutcome.AgentNotActive or RestoreStartOutcome.AgentCannotRestore:
                return Results.Json(new { error = result.Code, reason = result.Detail, message = result.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
            case RestoreStartOutcome.SupportAccessDenied:
                // The session ended or expired between the check and the commit: the database refused, and the refusal is audited like any other.
                return await gate.DeniedAsync(context, SupportDenialOf(result.Detail), SupportPermissions.RestoreBackup, SupportAuditActions.ArtifactTargetType, artifactId.ToString("D"), attempted, cancellationToken);
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

    private static SupportAccessDenial SupportDenialOf(string? code) =>
        Enum.GetValues<SupportAccessDenial>().FirstOrDefault(denial => SupportAccessDenials.Code(denial) == code, SupportAccessDenial.SessionClosed);

    private static async Task<IResult> RestoresAsync(Guid sessionId, string? status, int? page, int? pageSize, SupportRequestGate gate, RestoreService restores, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size)) return Invalid("page", "invalid_value");
        RestoreStatus? parsed = null;
        if (status is not null)
        {
            var name = Enum.GetNames<RestoreStatus>().FirstOrDefault(candidate => string.Equals(candidate, status, StringComparison.OrdinalIgnoreCase));
            if (name is null) return Invalid("status", "invalid_value");
            parsed = Enum.Parse<RestoreStatus>(name);
        }
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.ViewBackupMetadata, null, "restores.list", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        var result = await restores.ListAsync(context.TenantId, new RestoreQuery(parsed, null, null, p, size, context.SessionId), cancellationToken);
        return Results.Ok(new { items = result.Items.Select(Sanitized).ToList(), totalCount = result.TotalCount, page = result.Page, pageSize = result.PageSize });
    }

    private static async Task<IResult> RestoreAsync(Guid sessionId, Guid restoreId, SupportRequestGate gate, IRestoreStore store, CancellationToken cancellationToken)
    {
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.ViewBackupMetadata, null, "restore.get", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        var operation = await store.GetAsync(context.TenantId, restoreId, cancellationToken);
        return operation is null || operation.SupportSessionId != context.SessionId ? Results.NotFound() : Results.Ok(Sanitized(RestoreService.ToView(operation, includeResult: true)));
    }

    private static async Task<IResult> RestoreCancelAsync(Guid sessionId, Guid restoreId, SupportRequestGate gate, [FromServices] SupportRestoreDeps deps, CancellationToken cancellationToken)
    {
        // Cancelling only ever reduces what a restore can do, so the session is judged here and the store records the request (audited) without re-judging it.
        // M5's semantics apply as they are: a pending restore is withdrawn, a leased or running one is asked to stop, and the target's condition is reported, never assumed.
        var allowed = await gate.RequireAsync(sessionId, SupportPermissions.RestoreBackup, null, "restore.cancel", cancellationToken);
        if (allowed.Context is not { } context) return allowed.Failure!;
        try
        {
            var restore = await deps.Restores.CancelAsync(context.TenantId, restoreId, cancellationToken, context.ActorId.Value, context.SessionId);
            if (restore is null) return Results.NotFound();
            await HintAsync(deps, new AgentId(restore.AgentId), "CancellationRequested");
            return Results.Ok(Sanitized(restore));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            deps.Logger.LogError("Support restore cancellation or audit persistence failed with {ExceptionType} for restore {RestoreId}", exception.GetType().Name, restoreId);
            return SupportRequestGate.Unavailable();
        }
    }

    // ---- helpers ----

    /// <summary>The restore as support sees it: the target's credential reference (the name of a secret held by the customer's agent) is never shown.</summary>
    private static RestoreView Sanitized(RestoreView view) => view with { Target = view.Target with { DatabaseCredentialReference = null } };

    private static async Task HintAsync(SupportRestoreDeps deps, AgentId agentId, string reason)
    {
        try { await deps.Push.TrySendPollHintAsync(agentId, reason, CancellationToken.None); }
        catch (Exception exception) { deps.Logger.LogWarning(exception, "Support restore poll hint delivery failed; durable polling remains authoritative."); }
    }

    private static async Task<SupportSessionView?> ViewAsync(SupportAccessService service, Guid sessionId, UserId viewer, CancellationToken cancellationToken)
    {
        var row = await service.GetAsync(sessionId, cancellationToken);
        return row is null ? null : SupportViews.ToView(row, viewer, service.Now);
    }

    private static SupportAuditSpec Audit(string action, string targetType, string targetId, string resource) =>
        new(action, targetType, targetId, new Dictionary<string, object?> { ["resource"] = resource });

    private static IResult Invalid(string field, string code) => Results.BadRequest(new { error = "invalid_request", field, code });

    private static bool TryPaging(int? page, int? pageSize, out int p, out int size)
    {
        p = page ?? 1;
        size = pageSize ?? DefaultPageSize;
        return p is >= 1 and <= 1_000_000 && size is >= 1 and <= MaximumPageSize;
    }

    private static object Page<TSource, TResult>(SupportPage<TSource> result, Func<TSource, TResult> map) => new
    {
        items = result.Items.Select(map).ToList(), totalCount = result.TotalCount, page = result.Page, pageSize = result.PageSize
    };

    private sealed class StreamResult(DownloadSession session, DownloadStreamer streamer) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => streamer.StreamAsync(httpContext, session);
    }
}

/// <summary>Dependencies of the support restore routes, grouped so the route signatures stay readable.</summary>
internal sealed class SupportRestoreDeps(RestoreService restores, IBackupUiStore jobs, IAgentPushDispatcher push, TimeProvider time, ILogger<RestoreService> logger, RestoreTargetsReader targets)
{
    public RestoreService Restores { get; } = restores;
    public IBackupUiStore Jobs { get; } = jobs;
    public IAgentPushDispatcher Push { get; } = push;
    public TimeProvider Time { get; } = time;
    public ILogger<RestoreService> Logger { get; } = logger;
    public RestoreTargetsReader Targets { get; } = targets;
}
