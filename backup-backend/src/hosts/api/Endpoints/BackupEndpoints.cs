using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Tenancy;

namespace BackupPlatform.Api.Endpoints;

public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}").WithMetadata(new TenantScopedEndpoint());
        tenant.MapPost("/backup-jobs", CreateJobAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapGet("/backup-jobs/{jobId:guid}", GetJobAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapPost("/backup-jobs/{jobId:guid}/runs", StartRunAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapGet("/backup-runs", ListRunsAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/backup-jobs/{jobId:guid}/runs", ListRunsAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/backup-runs/{runId:guid}", GetRunAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapPost("/backup-runs/{runId:guid}/cancel", CancelAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        return endpoints;
    }

    private static async Task<IResult> CreateJobAsync(CreateBackupJobRequest request, ITenantContext tenant, BackupOrchestrator service, CancellationToken cancellationToken)
    {
        try
        {
            var job = await service.CreateJobAsync(tenant.TenantId!, request, cancellationToken);
            return job is null ? Results.NotFound() : Results.Created($"/api/tenants/{tenant.TenantId!.Value}/backup-jobs/{job.Id}", job);
        }
        catch (ArgumentException) { return Invalid("Invalid backup job configuration."); }
    }

    private static async Task<IResult> GetJobAsync(Guid jobId, ITenantContext tenant, IBackupRunStore store, CancellationToken cancellationToken)
    {
        var job = await store.GetJobAsync(tenant.TenantId!, jobId, cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(new BackupJobView(job.Id, job.AgentId.Value, job.Name, job.BackupType, job.OverlapPolicy, job.Enabled, job.CreatedAt, BackupOrchestrator.ToWire(job.Configuration)));
    }

    private static async Task<IResult> StartRunAsync(Guid jobId, HttpContext context, ITenantContext tenant, BackupOrchestrator service,
        IAgentPushDispatcher push, ILogger<BackupOrchestrator> logger, CancellationToken cancellationToken)
    {
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !BackupInputValidation.IsValidKey(keys[0])) return Invalid("A valid Idempotency-Key is required (1-128 printable ASCII characters).");
        var run = await service.StartRunAsync(tenant.TenantId!, jobId, keys[0]!, cancellationToken);
        if (run is null) return Results.NotFound();
        await HintAsync(push, run.AgentId, "WorkAvailable", logger);
        return Results.Ok(BackupOrchestrator.ToView(run));
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

    private static async Task HintAsync(IAgentPushDispatcher push, BackupPlatform.Domain.Identity.AgentId agentId, string reason, ILogger logger)
    {
        try { await push.TrySendPollHintAsync(agentId, reason, CancellationToken.None); }
        catch (Exception exception) { logger.LogWarning(exception, "Backup poll hint delivery failed; durable polling remains authoritative."); }
    }

    private static IResult Invalid(string message) => Results.BadRequest(new { error = "invalid_request", message });
}
