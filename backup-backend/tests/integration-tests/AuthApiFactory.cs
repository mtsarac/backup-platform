using System.Security.Claims;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BackupPlatform.IntegrationTests;

internal sealed class AuthApiFactory(
    string connectionString,
    TimeProvider? timeProvider = null,
    BackupPlatform.Agents.AgentSecurityOptions? agentOptions = null,
    BackupPlatform.Agents.Connectivity.AgentConnectivityOptions? connectivityOptions = null,
    AgentRateLimitOptions? rateLimitOptions = null,
    System.Security.Cryptography.X509Certificates.X509Certificate2? kestrelCertificate = null,
    IReadOnlyDictionary<string, string?>? configuration = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        if (kestrelCertificate is not null)
        {
            // A real TLS listener on an ephemeral loopback port, for tests that need real sockets (SignalR).
            builder.ConfigureKestrel(kestrel => kestrel.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.UseHttps(kestrelCertificate)));
        }
        // Scheduling and retention are off unless a test asks for them: their workers would otherwise start in every API host the suite creates.
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = connectionString,
            ["Backups:Scheduling:Enabled"] = "false",
            ["Backups:Retention:Enabled"] = "false",
            ["ManagedService:Quota:MaintenanceEnabled"] = "false",
            ["Notifications:Enabled"] = "false",
            ["Notifications:PublicBaseUrl"] = "https://backup.example.com"
        };
        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>()) settings[key] = value;
        builder.ConfigureAppConfiguration((_, builderConfiguration) => builderConfiguration.AddInMemoryCollection(settings));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<PlatformDbContext>>();
            services.AddSingleton(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options);
            services.AddTransient<IStartupFilter>(_ => new AuthProbeStartupFilter());
            if (timeProvider is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(timeProvider);
            }
            if (agentOptions is not null)
            {
                services.RemoveAll<BackupPlatform.Agents.AgentSecurityOptions>();
                services.AddSingleton(agentOptions);
            }
            if (connectivityOptions is not null)
            {
                services.RemoveAll<BackupPlatform.Agents.Connectivity.AgentConnectivityOptions>();
                services.AddSingleton(connectivityOptions);
            }
            if (rateLimitOptions is not null)
            {
                services.RemoveAll<AgentRateLimitOptions>();
                services.AddSingleton(rateLimitOptions);
            }
            configureServices?.Invoke(services);
        });
    }

    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    public HttpClient CreateReplayClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = false
    });
}

internal sealed class AuthProbeStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseRouting();
        next(app);
        app.UseEndpoints(endpoints =>
        {
            MapTenantPolicy(endpoints, "administrator", AuthorizationPolicies.TenantAdministrator);
            MapTenantPolicy(endpoints, "operator", AuthorizationPolicies.TenantOperator);
            endpoints.MapGet("/api/test/platform", context => context.Response.WriteAsJsonAsync(new { allowed = true }))
                .RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
            endpoints.MapGet("/api/test/tenants/{tenantId}/immutable", ProbeImmutabilityAsync)
                .WithMetadata(new TenantScopedEndpoint())
                .RequireAuthorization(AuthorizationPolicies.TenantViewer);
        });
    };

    private static void MapTenantPolicy(IEndpointRouteBuilder endpoints, string path, string policy) =>
        endpoints.MapGet($"/api/test/tenants/{{tenantId}}/{path}", context => context.Response.WriteAsJsonAsync(new { allowed = true }))
            .WithMetadata(new TenantScopedEndpoint())
            .RequireAuthorization(policy);

    private static async Task ProbeImmutabilityAsync(HttpContext context)
    {
        var tenant = context.RequestServices.GetRequiredService<ITenantContext>();
        var user = context.RequestServices.GetRequiredService<ICurrentUser>();
        var database = context.RequestServices.GetRequiredService<PlatformDbContext>();
        var originalTenant = tenant.TenantId!;
        var originalUser = user.UserId!;
        var replacementTenant = new TenantId(Guid.Parse(context.Request.Query["replacementTenant"].ToString()));
        var replacementUser = new UserId(Guid.Parse(context.Request.Query["replacementUser"].ToString()));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, replacementUser.Value.ToString())], BrowserAuthentication.Scheme));
        context.Request.Headers["X-Tenant-Id"] = replacementTenant.Value.ToString();
        context.Request.RouteValues["tenantId"] = replacementTenant.Value.ToString();
        context.Features.Set(new ResolvedTenantContext(new TenantAccess(replacementTenant, "Untrusted replacement", TenantRole.Owner)));
        var visibleTenants = await database.Tenants.Select(row => row.Id).ToArrayAsync();
        await context.Response.WriteAsJsonAsync(new
        {
            originalTenantId = originalTenant.Value,
            tenantId = context.RequestServices.GetRequiredService<ITenantContext>().TenantId!.Value,
            originalUserId = originalUser.Value,
            userId = context.RequestServices.GetRequiredService<ICurrentUser>().UserId!.Value,
            visibleTenantIds = visibleTenants.Select(id => id.Value).ToArray()
        });
    }
}
