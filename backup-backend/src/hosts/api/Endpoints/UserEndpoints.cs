using BackupPlatform.Api.Security;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Api.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/me", GetCurrentUserAsync).RequireAuthorization();
        endpoints.MapGet("/api/me/tenants", GetTenantsAsync).RequireAuthorization();
        endpoints.MapGet("/api/tenants/{tenantId:guid}/context", GetTenantContext)
            .WithMetadata(new TenantScopedEndpoint())
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
        return endpoints;
    }

    private static async Task<IResult> GetCurrentUserAsync(ICurrentUser currentUser, IUserAccessStore userAccessStore, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId) return Results.Unauthorized();
        var profile = await userAccessStore.GetUserAsync(userId, cancellationToken);
        return profile is null
            ? Results.Unauthorized()
            : Results.Ok(new
            {
                userId = profile.UserId.Value,
                displayName = profile.DisplayName,
                role = profile.Role.ToString(),
                isPlatformAdmin = profile.Role == UserRole.PlatformAdmin
            });
    }

    private static async Task<IResult> GetTenantsAsync(ICurrentUser currentUser, IUserAccessStore userAccessStore, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId) return Results.Unauthorized();
        var memberships = await userAccessStore.ListTenantsAsync(userId, cancellationToken);
        return Results.Ok(memberships.Select(membership => new
        {
            tenantId = membership.TenantId.Value,
            name = membership.Name
        }));
    }

    private static IResult GetTenantContext(ITenantContext tenantContext)
    {
        if (tenantContext is not ResolvedTenantContext resolved) return Results.NotFound(new { error = "tenant_not_found" });
        var membership = resolved.Membership;
        return Results.Ok(new
        {
            tenantId = membership.TenantId.Value,
            name = membership.Name
        });
    }

}
