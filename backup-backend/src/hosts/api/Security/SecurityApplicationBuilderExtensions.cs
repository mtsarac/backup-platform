namespace BackupPlatform.Api.Security;

public static class SecurityApplicationBuilderExtensions
{
    public static WebApplication UsePlatformSecurity(this WebApplication app)
    {
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
        app.UseHttpsRedirection();
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.UseAuthorization();
        app.UseMiddleware<ApiAntiforgeryMiddleware>();
        return app;
    }
}
