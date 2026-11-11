using Microsoft.AspNetCore.Antiforgery;

namespace BackupPlatform.Api.Security;

// Only endpoints that authorize exclusively with an explicit request token may use this marker
public sealed class TokenOnlyEndpoint { }

public sealed class ApiAntiforgeryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (RequiresValidation(context))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid antiforgery token." }, context.RequestAborted);
                return;
            }
        }
        await next(context);
    }

    private static bool RequiresValidation(HttpContext context) => context.Request.Path.StartsWithSegments("/api")
        && context.GetEndpoint()?.Metadata.GetMetadata<AgentApiEndpoint>() is null
        && context.GetEndpoint()?.Metadata.GetMetadata<TokenOnlyEndpoint>() is null
        && !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method)
        && !HttpMethods.IsOptions(context.Request.Method)
        && !HttpMethods.IsTrace(context.Request.Method);
}
