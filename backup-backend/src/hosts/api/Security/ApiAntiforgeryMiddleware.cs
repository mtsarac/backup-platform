using Microsoft.AspNetCore.Antiforgery;

namespace BackupPlatform.Api.Security;

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

    /// <summary>
    /// Agent API routes never accept a browser session (they authenticate with their own header scheme), so cookie CSRF cannot apply; neither does it to an
    /// endpoint that opted out with <c>DisableAntiforgery</c> because its only credential is a token in the request itself (the email recipient links).
    /// </summary>
    private static bool RequiresValidation(HttpContext context) => context.Request.Path.StartsWithSegments("/api")
        && context.GetEndpoint()?.Metadata.GetMetadata<AgentApiEndpoint>() is null
        && context.GetEndpoint()?.Metadata.GetMetadata<IAntiforgeryMetadata>() is not { RequiresValidation: false }
        && !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method)
        && !HttpMethods.IsOptions(context.Request.Method)
        && !HttpMethods.IsTrace(context.Request.Method);
}
