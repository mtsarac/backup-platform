using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace BackupPlatform.Api.Endpoints;

public static class BackupHealthEndpoints
{
    public static IEndpointRouteBuilder MapBackupHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}").WithMetadata(new TenantScopedEndpoint());
        tenant.MapGet("/backup-jobs/{jobId:guid}/health", GetJobAsync).RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/observability/health-summary", GetSummaryAsync).RequireAuthorization(AuthorizationPolicies.TenantAccess);
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

    private static async Task<IResult> GetJobV1Async(Guid id, IBackupHealthStore store, BackupHealthEvaluator evaluator,
        TenantResolutionService resolution, HttpContext context, IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        var tenantId = await store.FindJobTenantAsync(id, cancellationToken);
        if (tenantId is null || !await AuthorizeResolvedTenantAsync(tenantId, resolution, context, authorization, cancellationToken)) return Results.NotFound();
        var health = await evaluator.GetJobAsync(tenantId, id, cancellationToken);
        return health is null ? Results.NotFound() : Results.Ok(health);
    }

    private static async Task<IResult> GetSummaryV1Async(Guid tenantId, BackupHealthEvaluator evaluator,
        TenantResolutionService resolution, HttpContext context, IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        var target = new TenantId(tenantId);
        if (!await AuthorizeResolvedTenantAsync(target, resolution, context, authorization, cancellationToken)) return Results.NotFound();
        return Results.Ok(await evaluator.GetSummaryAsync(target, cancellationToken));
    }

    internal static async Task<bool> AuthorizeResolvedTenantAsync(TenantId tenantId, TenantResolutionService resolution,
        HttpContext context, IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        var access = await resolution.ResolveAsync(tenantId, cancellationToken);
        if (access is null) return false;
        context.Features.Set(new ResolvedTenantContext(access));
        return (await authorization.AuthorizeAsync(context.User, context, AuthorizationPolicies.TenantAccess)).Succeeded;
    }
}
