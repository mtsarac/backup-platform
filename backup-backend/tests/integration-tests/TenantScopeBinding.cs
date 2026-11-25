using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

/// <summary>Gives a service scope the verified tenant a request of that tenant would carry, for tests that call tenant-facing services outside a request.</summary>
internal static class TenantScopeBinding
{
    public static IServiceProvider ForTenant(this IServiceProvider services, TenantId tenantId)
    {
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        var context = new DefaultHttpContext();
        context.Features.Set(new ResolvedTenantContext(new TenantAccess(tenantId, "test")));
        accessor.HttpContext = context;
        return services;
    }
}
