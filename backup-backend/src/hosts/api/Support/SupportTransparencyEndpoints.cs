using BackupPlatform.Api.Security;
using BackupPlatform.Application.Support;
using BackupPlatform.Application.Tenancy;

namespace BackupPlatform.Api.Support;

/// <summary>
/// What a tenant's own Owners and Admins can see of the support access to their tenant: who, when, why, with which permissions, which artifacts and whether a
/// download or restore happened. Tenant-scoped (membership resolved by the middleware) and TenantAdministrator only, so Operators and Viewers get 403 and one
/// tenant never sees another's sessions. Strictly read-only: the audit it reads cannot be changed from here, and a tenant cannot end a session through it.
/// </summary>
public static class SupportTransparencyEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaximumPageSize = 100;

    public static IEndpointRouteBuilder MapSupportTransparencyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenants/{tenantId:guid}/support-access").WithMetadata(new TenantScopedEndpoint())
            .RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        group.MapGet("/sessions", ListAsync);
        group.MapGet("/sessions/{sessionId:guid}", GetAsync);
        group.MapGet("/sessions/{sessionId:guid}/activity", ActivityAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(int? page, int? pageSize, ITenantContext tenant, ISupportQueryStore queries, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size)) return Invalid();
        var result = await queries.ListTenantSessionsAsync(tenant.TenantId!, p, size, cancellationToken);
        var now = time.GetUtcNow();
        return Results.Ok(new { items = result.Items.Select(row => SupportViews.ToTenantView(row, now)).ToList(), totalCount = result.TotalCount, page = result.Page, pageSize = result.PageSize });
    }

    private static async Task<IResult> GetAsync(Guid sessionId, ITenantContext tenant, ISupportQueryStore queries, TimeProvider time, CancellationToken cancellationToken)
    {
        var row = await queries.GetTenantSessionAsync(tenant.TenantId!, sessionId, cancellationToken);
        return row is null ? Results.NotFound() : Results.Ok(SupportViews.ToTenantView(row, time.GetUtcNow()));
    }

    private static async Task<IResult> ActivityAsync(Guid sessionId, int? page, int? pageSize, ITenantContext tenant, ISupportQueryStore queries, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, out var p, out var size)) return Invalid();
        var result = await queries.ListTenantActivityAsync(tenant.TenantId!, sessionId, p, size, cancellationToken);
        return result is null
            ? Results.NotFound()
            : Results.Ok(new { items = result.Items.Select(SupportViews.ToActivityView).ToList(), totalCount = result.TotalCount, page = result.Page, pageSize = result.PageSize });
    }

    private static IResult Invalid() => Results.BadRequest(new { error = "invalid_request", field = "page", code = "invalid_value" });

    private static bool TryPaging(int? page, int? pageSize, out int p, out int size)
    {
        p = page ?? 1;
        size = pageSize ?? DefaultPageSize;
        return p is >= 1 and <= 1_000_000 && size is >= 1 and <= MaximumPageSize;
    }
}
