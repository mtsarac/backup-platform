using Microsoft.AspNetCore.Routing;

namespace BackupPlatform.Api.Security;

/// <summary>The only place a <c>{tenantId}</c> route is declared: tenant resolution and the TenantAccess policy come with the group.</summary>
public static class TenantEndpointRouteBuilderExtensions
{
    public static RouteGroupBuilder MapTenantGroup(this IEndpointRouteBuilder endpoints, string path = "") =>
        endpoints.MapGroup("/api/tenants/{tenantId:guid}" + path)
            .WithMetadata(new TenantScopedEndpoint())
            .RequireAuthorization(AuthorizationPolicies.TenantAccess);
}
