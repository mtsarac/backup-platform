using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Backups;

namespace BackupPlatform.Api.Endpoints;

public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}").WithMetadata(new TenantScopedEndpoint());
        tenant.MapPost("/backup-jobs", CreateJobAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
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

    private static async Task<IResult> CreateJobAsync(CreateBackupJobRequest request, ITenantContext tenant, BackupOrchestrator service, BackupScheduleService schedules, CancellationToken cancellationToken)
    {
        try
        {
            var job = await service.CreateJobAsync(tenant.TenantId!, request, cancellationToken);
            if (job is null) return Results.NotFound();
            if (job.Schedule is not null) await schedules.TrySynchronizeAsync(tenant.TenantId!, job.Id, cancellationToken);
            return Results.Created($"/api/tenants/{tenant.TenantId!.Value}/backup-jobs/{job.Id}", job);
        }
        catch (InvalidBackupScheduleException exception) { return InvalidSchedule(exception); }
        catch (ArgumentException) { return Invalid("Invalid backup job configuration."); }
    }

    private static async Task<IResult> GetJobAsync(Guid jobId, ITenantContext tenant, IBackupRunStore store, TimeProvider time, CancellationToken cancellationToken)
    {
        var job = await store.GetJobAsync(tenant.TenantId!, jobId, cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(BackupOrchestrator.ToView(job, time.GetUtcNow()));
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
        catch (ArgumentException) { return Invalid("Invalid retention policy."); }
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
            default:
                await HintAsync(push, result.Run!.AgentId, "WorkAvailable", logger);
                return Results.Ok(BackupOrchestrator.ToView(result.Run));
        }
    }

    private static async Task<IResult> ListRunsAsync(Guid? jobId, int? limit, ITenantContext tenant, IBackupRunStore store, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) return Invalid("Limit must be between 1 and 100.");
        var runs = await store.ListRunsAsync(tenant.TenantId!, jobId, limit ?? 50, cancellationToken);
        return Results.Ok(runs.Select(BackupOrchestrator.ToView));
    }

    private static async Task<IResult> GetRunAsync(Guid runId, ITenantContext tenant, IBackupRunStore store, CancellationToken cancellationToken)
    {
        var run = await store.GetRunAsync(tenant.TenantId!, runId, cancellationToken);
        return run is null ? Results.NotFound() : Results.Ok(BackupOrchestrator.ToView(run));
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

    private static IResult Invalid(string message) => Results.BadRequest(new { error = "invalid_request", message });
}
