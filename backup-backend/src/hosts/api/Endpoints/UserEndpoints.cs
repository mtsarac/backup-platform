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
        endpoints.MapGet("/api/tenants/{tenantId}/context", GetTenantContext)
            .WithMetadata(new TenantScopedEndpoint())
            .RequireAuthorization(AuthorizationPolicies.TenantViewer);
        return endpoints;
    }

    private static async Task<IResult> GetCurrentUserAsync(ICurrentUser currentUser, IUserAccessStore userAccessStore, CancellationToken cancellationToken)
    {
        var profile = await userAccessStore.GetUserAsync(currentUser.UserId!, cancellationToken);
        return profile is null
            ? Results.Unauthorized()
            : Results.Ok(new { userId = profile.UserId.Value, displayName = profile.DisplayName });
    }

    private static async Task<IResult> GetTenantsAsync(ICurrentUser currentUser, IUserAccessStore userAccessStore, CancellationToken cancellationToken)
    {
        var memberships = await userAccessStore.ListTenantsAsync(currentUser.UserId!, cancellationToken);
        return Results.Ok(memberships.Select(membership => new
        {
            tenantId = membership.TenantId.Value,
            name = membership.Name,
            role = FormatRole(membership.Role)
        }));
    }

    private static IResult GetTenantContext(ITenantContext tenantContext)
    {
        var membership = ((ResolvedTenantContext)tenantContext).Membership;
        return Results.Ok(new
        {
            tenantId = membership.TenantId.Value,
            name = membership.Name,
            role = FormatRole(membership.Role)
        });
    }

    private static string FormatRole(TenantRole role) => role switch
    {
        TenantRole.Owner or TenantRole.Admin => "Administrator",
        TenantRole.Operator => "Operator",
        TenantRole.Viewer => "Viewer",
        _ => throw new InvalidOperationException("The tenant membership has an unsupported role.")
    };
}
