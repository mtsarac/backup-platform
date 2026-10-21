using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Tenancy;

namespace BackupPlatform.Api.Endpoints;

public static class BackupHealthEndpoints
{
    public static IEndpointRouteBuilder MapBackupHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}").WithMetadata(new TenantScopedEndpoint());
        tenant.MapGet("/backup-jobs/{jobId:guid}/health", GetJobAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/observability/health-summary", GetSummaryAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        endpoints.MapGet("/api/v1/backups/jobs/{id:guid}/health", GetJobV1Async).RequireAuthorization();
        endpoints.MapGet("/api/v1/observability/health-summary", GetSummaryV1Async).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> GetJobAsync(Guid jobId, ITenantContext tenant, BackupHealthEvaluator evaluator, CancellationToken cancellationToken)
    {
        var health = await evaluator.GetJobAsync(tenant.TenantId!, jobId, cancellationToken);
        return health is null ? Results.NotFound() : Results.Ok(health);
    }

    private static async Task<IResult> GetSummaryAsync(ITenantContext tenant, BackupHealthEvaluator evaluator, CancellationToken cancellationToken) =>
        Results.Ok(await evaluator.GetSummaryAsync(tenant.TenantId!, cancellationToken));

    private static async Task<IResult> GetJobV1Async(Guid id, IBackupHealthStore store, BackupHealthEvaluator evaluator, ICurrentUser user,
        TenantResolutionService resolution, IUserAccessStore access, CancellationToken cancellationToken)
    {
        var tenantId = await store.FindJobTenantAsync(id, cancellationToken);
        if (tenantId is null || !await CanReadAsync(tenantId, user, resolution, access, cancellationToken)) return Results.NotFound();
        var health = await evaluator.GetJobAsync(tenantId, id, cancellationToken);
        return health is null ? Results.NotFound() : Results.Ok(health);
    }

    private static async Task<IResult> GetSummaryV1Async(Guid tenantId, BackupHealthEvaluator evaluator, ICurrentUser user,
        TenantResolutionService resolution, IUserAccessStore access, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) return Results.BadRequest();
        var target = new TenantId(tenantId);
        if (!await CanReadAsync(target, user, resolution, access, cancellationToken)) return Results.NotFound();
        return Results.Ok(await evaluator.GetSummaryAsync(target, cancellationToken));
    }

    private static async Task<bool> CanReadAsync(TenantId tenantId, ICurrentUser user, TenantResolutionService resolution, IUserAccessStore access, CancellationToken cancellationToken) =>
        user.IsAuthenticated && user.UserId is { } userId &&
        (await access.IsPlatformAdminAsync(userId, cancellationToken) || await resolution.ResolveAsync(tenantId, cancellationToken) is not null);
}
