using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Tenancy;
using Microsoft.AspNetCore.Authorization;
using BackupPlatform.Domain.Backups;

namespace BackupPlatform.Api.Endpoints;

public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}").WithMetadata(new TenantScopedEndpoint());
        tenant.MapPost("/backup-jobs", CreateJobAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapGet("/backup-jobs", ListJobsAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapPut("/backup-jobs/{jobId:guid}", UpdateJobAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapGet("/dashboard", GetDashboardAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/backup-jobs/{jobId:guid}", GetJobAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapPut("/backup-jobs/{jobId:guid}/schedule", ChangeScheduleAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapDelete("/backup-jobs/{jobId:guid}/schedule", RemoveScheduleAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapGet("/backup-jobs/{jobId:guid}/schedule/occurrences", ListOccurrencesAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapPut("/backup-jobs/{jobId:guid}/retention", ChangeRetentionAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPost("/backup-jobs/{jobId:guid}/enable", EnableAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapPost("/backup-jobs/{jobId:guid}/disable", DisableAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapPost("/backup-jobs/{jobId:guid}/runs", StartRunAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapGet("/backup-runs", ListRunsAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/backup-jobs/{jobId:guid}/runs", ListRunsAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/backup-runs/{runId:guid}", GetRunAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapPost("/backup-runs/{runId:guid}/cancel", CancelAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapPost("/backup-runs/{runId:guid}/retry", RetryAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        return endpoints;
    }

    private static async Task<IResult> CreateJobAsync(CreateBackupJobRequest request, HttpContext context, ITenantContext tenant, IAuthorizationService authorization,
        BackupOrchestrator service, BackupScheduleService schedules, CancellationToken cancellationToken)
    {
        if (request.Retention is not null && !await IsAdministratorAsync(authorization, context)) return RetentionForbidden();
        try
        {
            var job = await service.CreateJobAsync(tenant.TenantId!, request, cancellationToken);
            if (job is null) return Results.NotFound();
            if (job.Schedule is not null) await schedules.TrySynchronizeAsync(tenant.TenantId!, job.Id, cancellationToken);
            return Results.Created($"/api/tenants/{tenant.TenantId!.Value}/backup-jobs/{job.Id}", job);
        }
        catch (InvalidBackupScheduleException exception) { return InvalidSchedule(exception); }
        catch (InvalidBackupJobException exception) { return InvalidJob(exception); }
        catch (ArgumentException) { return Invalid("Invalid backup job configuration."); }
    }

    private static async Task<IResult> GetJobAsync(Guid jobId, ITenantContext tenant, IBackupUiStore store, TimeProvider time, CancellationToken cancellationToken)
    {
        var job = await store.GetJobAsync(tenant.TenantId!, jobId, time.GetUtcNow(), cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }

    private static async Task<IResult> ListJobsAsync(ITenantContext tenant, IBackupUiStore store, TimeProvider time, CancellationToken cancellationToken) =>
        Results.Ok(await store.ListJobsAsync(tenant.TenantId!, time.GetUtcNow(), cancellationToken));

    private static async Task<IResult> UpdateJobAsync(Guid jobId, UpdateBackupJobRequest request, HttpContext context, ITenantContext tenant, IAuthorizationService authorization,
        BackupOrchestrator service, BackupScheduleService schedules, CancellationToken cancellationToken)
    {
        // Retention keeps its administrator-only rule even though it now travels in the same request: an operator simply does not send it.
        if (request.Retention is not null && !await IsAdministratorAsync(authorization, context)) return RetentionForbidden();
        try
        {
            var job = await service.UpdateJobAsync(tenant.TenantId!, jobId, request, cancellationToken);
            if (job is null) return Results.NotFound();
            await schedules.TrySynchronizeAsync(tenant.TenantId!, jobId, cancellationToken);
            return Results.Ok(job);
        }
        catch (InvalidBackupScheduleException exception) { return InvalidSchedule(exception); }
        catch (InvalidBackupJobException exception) { return InvalidJob(exception); }
        catch (ArgumentException) { return Invalid("Invalid backup job configuration."); }
    }

    private static async Task<bool> IsAdministratorAsync(IAuthorizationService authorization, HttpContext context) =>
        (await authorization.AuthorizeAsync(context.User, context, AuthorizationPolicies.TenantAdministrator)).Succeeded;

    private static IResult RetentionForbidden() => Results.Json(new { error = "forbidden", field = "retention", code = "administrator_required" }, statusCode: StatusCodes.Status403Forbidden);

    private static async Task<IResult> GetDashboardAsync(ITenantContext tenant, IBackupUiStore store,
        AgentConnectivityQueryService connectivity, TimeProvider time, CancellationToken cancellationToken)
    {
        var summary = await store.GetDashboardAsync(tenant.TenantId!, time.GetUtcNow(), cancellationToken);
        var agents = await connectivity.SummarizeAsync(tenant.TenantId!, cancellationToken);
        return Results.Ok(new { summary.Jobs, agents = new { agents.Total, agents.Online, agents.Offline, agents.Unknown, agents.Disabled, agents.Revoked },
            summary.Runs, summary.Artifacts, summary.RecentRuns, summary.UpcomingJobs });
    }

    private static async Task<IResult> ChangeScheduleAsync(Guid jobId, BackupScheduleRequest request, ITenantContext tenant, BackupScheduleService schedules, CancellationToken cancellationToken)
    {
        try
        {
            var job = await schedules.ChangeScheduleAsync(tenant.TenantId!, jobId, request, cancellationToken);
            return job is null ? Results.NotFound() : Results.Ok(job);
        }
        catch (InvalidBackupScheduleException exception) { return InvalidSchedule(exception); }
        catch (ArgumentException) { return Invalid("Invalid schedule."); }
    }

    private static async Task<IResult> RemoveScheduleAsync(Guid jobId, ITenantContext tenant, BackupScheduleService schedules, CancellationToken cancellationToken)
    {
        var job = await schedules.ChangeScheduleAsync(tenant.TenantId!, jobId, null, cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }

    private static async Task<IResult> EnableAsync(Guid jobId, ITenantContext tenant, BackupScheduleService schedules, CancellationToken cancellationToken)
    {
        var job = await schedules.SetEnabledAsync(tenant.TenantId!, jobId, true, cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }

    /// <summary>Administrators only: a shorter keep period makes FUTURE backups expire sooner. Existing backups keep the expiration they were accepted with.</summary>
    private static async Task<IResult> ChangeRetentionAsync(Guid jobId, BackupRetentionPolicyRequest request, ITenantContext tenant, BackupArtifactService service, CancellationToken cancellationToken)
    {
        try
        {
            var job = await service.ChangeJobRetentionAsync(tenant.TenantId!, jobId, request, cancellationToken);
            return job is null ? Results.NotFound() : Results.Ok(job);
        }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_request", field = "retention", code = "invalid_value", message = "Invalid retention policy." }); }
    }

    private static async Task<IResult> DisableAsync(Guid jobId, ITenantContext tenant, BackupScheduleService schedules, CancellationToken cancellationToken)
    {
        var job = await schedules.SetEnabledAsync(tenant.TenantId!, jobId, false, cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }

    private static async Task<IResult> ListOccurrencesAsync(Guid jobId, int? limit, ITenantContext tenant, BackupScheduleService schedules, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) return Invalid("Limit must be between 1 and 100.");
        var occurrences = await schedules.ListOccurrencesAsync(tenant.TenantId!, jobId, limit ?? 50, cancellationToken);
        return occurrences is null ? Results.NotFound() : Results.Ok(occurrences);
    }

    private static async Task<IResult> StartRunAsync(Guid jobId, HttpContext context, ITenantContext tenant, BackupOrchestrator service,
        IAgentPushDispatcher push, ILogger<BackupOrchestrator> logger, CancellationToken cancellationToken)
    {
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !BackupInputValidation.IsValidKey(keys[0])) return Invalid("A valid Idempotency-Key is required (1-128 printable ASCII characters).");
        var result = await service.StartRunAsync(tenant.TenantId!, jobId, keys[0]!, cancellationToken);
        switch (result.Outcome)
        {
            case BackupStartOutcome.NotFound:
                return Results.NotFound();
            case BackupStartOutcome.SkippedOverlap:
                // Never a silent skip: the caller learns that nothing was started and which run is in the way.
                return Results.Conflict(new { error = "overlap_skipped", message = "The job's overlap policy is Skip and another run of this job is active; no run was started.", activeRunId = result.ActiveRunId });
            case BackupStartOutcome.AdmissionDenied:
                // The reason is a stable code the caller can show: nothing was started because of the tenant's subscription or quota.
                return Results.Conflict(new { error = "admission_denied", code = result.DenialCode, message = AdmissionMessage(result.DenialCode) });
            default:
                await HintAsync(push, result.Run!.AgentId, "WorkAvailable", logger);
                return Results.Ok(BackupOrchestrator.ToView(result.Run));
        }
    }

    private static string AdmissionMessage(string? code) => code == BackupAdmissionDenialCodes.SubscriptionRequired
        ? "The tenant has no active subscription; no run was started."
        : "The tenant's storage quota is used up; no run was started.";

    private static async Task<IResult> ListRunsAsync(Guid? jobId, Guid? agentId, string? status, string? trigger,
        DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize,
        ITenantContext tenant, IBackupUiStore views, CancellationToken cancellationToken)
    {
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100) return Invalid("Invalid history page.");
        if (from > to) return Results.BadRequest(new { error = "invalid_request", field = "from", code = "invalid_date_range" });
        if (status is not null && status != "CancellationRequested" && !Enum.GetNames<BackupStatus>().Contains(status, StringComparer.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "invalid_request", field = "status", code = "invalid_value" });
        if (trigger is not null && trigger is not ("Manual" or "Scheduled" or "Retry"))
            return Results.BadRequest(new { error = "invalid_request", field = "trigger", code = "invalid_value" });
        return Results.Ok(await views.ListRunsAsync(tenant.TenantId!, new(page ?? 1, pageSize ?? 25, jobId, agentId, status, from?.ToUniversalTime(), to?.ToUniversalTime(), trigger), cancellationToken));
    }

    private static async Task<IResult> GetRunAsync(Guid runId, ITenantContext tenant, IBackupUiStore store, CancellationToken cancellationToken)
    {
        var run = await store.GetRunAsync(tenant.TenantId!, runId, cancellationToken);
        return run is null ? Results.NotFound() : Results.Ok(run);
    }

    private static async Task<IResult> CancelAsync(Guid runId, ITenantContext tenant, BackupOrchestrator service,
        IAgentPushDispatcher push, ILogger<BackupOrchestrator> logger, CancellationToken cancellationToken)
    {
        var run = await service.CancelAsync(tenant.TenantId!, runId, cancellationToken);
        if (run is null) return Results.NotFound();
        await HintAsync(push, run.AgentId, "CancellationRequested", logger);
        return Results.Ok(BackupOrchestrator.ToView(run));
    }

    private static async Task<IResult> RetryAsync(Guid runId, ITenantContext tenant, BackupOrchestrator service,
        IAgentPushDispatcher push, ILogger<BackupOrchestrator> logger, CancellationToken cancellationToken)
    {
        var result = await service.RetryAsync(tenant.TenantId!, runId, cancellationToken);
        switch (result.Outcome)
        {
            case BackupRetryOutcome.NotFound:
                return Results.NotFound();
            case BackupRetryOutcome.Started:
                await HintAsync(push, result.Run!.AgentId, "WorkAvailable", logger);
                return Results.Ok(BackupOrchestrator.ToView(result.Run));
            default:
                var (reason, message) = result.Outcome switch
                {
                    BackupRetryOutcome.Succeeded => ("succeeded", "A successful run is never retried."),
                    BackupRetryOutcome.Cancelled => ("cancelled", "A cancelled run is not retried."),
                    BackupRetryOutcome.InProgress => ("in_progress", "An attempt is already queued or running."),
                    BackupRetryOutcome.AttemptsExhausted => ("attempts_exhausted", "The run has used every attempt its retry policy allows."),
                    _ => ("job_overlap", "The job's overlap policy forbids reopening this run while another run of the job is active.")
                };
                return Results.Conflict(new { error = "retry_not_allowed", reason, message });
        }
    }

    private static async Task HintAsync(IAgentPushDispatcher push, BackupPlatform.Domain.Identity.AgentId agentId, string reason, ILogger logger)
    {
        try { await push.TrySendPollHintAsync(agentId, reason, CancellationToken.None); }
        catch (Exception exception) { logger.LogWarning(exception, "Backup poll hint delivery failed; durable polling remains authoritative."); }
    }

    /// <summary>Machine-readable and safe to show: names the offending field and a stable code, never the parser's own message.</summary>
    private static IResult InvalidSchedule(InvalidBackupScheduleException exception) =>
        Results.BadRequest(new { error = "invalid_schedule", field = exception.Field, code = exception.Code, message = exception.Message });

    private static IResult InvalidJob(InvalidBackupJobException exception) =>
        Results.BadRequest(new { error = "invalid_request", field = exception.Field, code = exception.Code, message = "Invalid backup job field." });

    private static IResult Invalid(string message) => Results.BadRequest(new { error = "invalid_request", message });
}
