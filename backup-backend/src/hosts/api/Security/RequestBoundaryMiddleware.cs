namespace BackupPlatform.Api.Security;

/// <summary>
/// Refuses route and query values no endpoint can accept, after authentication, authorization and the agent gates so a caller who may not see a resource learns nothing about it.
/// The all-zero Guid is never issued and every typed id constructor throws on it; PostgreSQL text cannot store a NUL, so one reaching the database is an unhandled 500.
/// Kestrel already refuses a NUL in the path; JSON bodies get the NUL rule from <see cref="NulFreeStringConverter"/> and their ids are validated by the handlers.
/// </summary>
public sealed class RequestBoundaryMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        foreach (var (name, value) in context.Request.RouteValues)
        {
            if (value is string text && IsEmptyGuid(text)) return Refuse(context, name);
        }
        foreach (var (name, values) in context.Request.Query)
        {
            if (name.Contains('\0') || values.Any(value => value is not null && (value.Contains('\0') || IsEmptyGuid(value)))) return Refuse(context, name);
        }
        return next(context);
    }

    private static bool IsEmptyGuid(string text) => Guid.TryParse(text, out var id) && id == Guid.Empty;

    private static Task Refuse(HttpContext context, string field)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsJsonAsync(new { error = "invalid_request", field, code = "invalid_value" });
    }
}
