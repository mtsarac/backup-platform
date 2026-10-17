namespace BackupPlatform.Api.Security;

public static class SecurityApplicationBuilderExtensions
{
    public static WebApplication UsePlatformSecurity(this WebApplication app)
    {
        var forwardedHeaders = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
        };
        forwardedHeaders.KnownIPNetworks.Clear();
        forwardedHeaders.KnownProxies.Clear();
        app.UseForwardedHeaders(forwardedHeaders);
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    return Task.CompletedTask;
                });
            }
            await next(context);
        });
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }
        // Development only: Google's registered loopback redirect URI is plain http://localhost; redirecting that single callback to the untrusted dev-cert HTTPS port would break the popup.
        var development = app.Environment.IsDevelopment();
        app.UseWhen(
            context => !(development && context.Request.Path.Equals("/api/platform/google-drive/oauth/callback", StringComparison.OrdinalIgnoreCase)),
            branch => branch.UseHttpsRedirection());
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.UseAuthorization();
        // After authorization so per-agent limits key on the authenticated agent; before the protocol gate so a mismatched agent is still throttled.
        app.UseRateLimiter();
        app.UseMiddleware<AgentProtocolMiddleware>();
        app.UseMiddleware<ApiAntiforgeryMiddleware>();
        return app;
    }
}
