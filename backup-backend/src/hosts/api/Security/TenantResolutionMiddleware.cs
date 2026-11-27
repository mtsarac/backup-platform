using BackupPlatform.Application.Tenancy;
using BackupPlatform.Application.Identity;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Routing;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Tenancy;

namespace BackupPlatform.Api.Security;

public sealed class TenantScopedEndpoint { }

public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantResolutionService resolutionService)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<TenantScopedEndpoint>() is null)
        {
            await next(context);
            return;
        }
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!Guid.TryParse(context.Request.RouteValues["tenantId"]?.ToString(), out var tenantId) || tenantId == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var membership = await resolutionService.ResolveAsync(new TenantId(tenantId), context.RequestAborted);
        if (membership is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (context.Features.Get<ResolvedTenantContext>() is not null)
        {
            throw new InvalidOperationException("The verified tenant context has already been established.");
        }
        context.Features.Set(new ResolvedTenantContext(membership));
        await PlatformTenantAudit.RecordAsync(context, membership.TenantId);
        await next(context);
    }
}

internal static class PlatformTenantAudit
{
    internal static async Task RecordAsync(HttpContext context, TenantId tenantId)
    {
        var user = context.RequestServices.GetRequiredService<ICurrentUser>();
        var auth = context.RequestServices.GetRequiredService<ICentralAuthorizationService>();
        if (user.UserId is not { } actor || !await auth.IsPlatformAdminAsync(actor, context.RequestAborted)) return;
        var db = context.RequestServices.GetRequiredService<PlatformDbContext>();
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unknown";
        db.RecordAudit(AuditEvent.Record(context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(),
            actor, AuditActorKind.PlatformAdmin, "tenant.support_request", tenantId, "http_route", route,
            new Dictionary<string, object?> { ["method"] = context.Request.Method }));
        await db.SaveChangesAsync(context.RequestAborted);
    }
}

internal sealed class UnresolvedTenantContext : ITenantContext
{
    internal static readonly UnresolvedTenantContext Instance = new();

    private UnresolvedTenantContext() { }

    public TenantId? TenantId => null;
}
