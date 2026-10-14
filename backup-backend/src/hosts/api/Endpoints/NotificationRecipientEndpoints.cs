using System.Net;
using BackupPlatform.Notifications;

namespace BackupPlatform.Api.Endpoints;

/// <summary>
/// The links in notification emails. They are anonymous by design: the signed token in the link is the whole credential and grants exactly one action
/// (confirm or unsubscribe) on one channel. A GET only shows what the link would do, so a mail scanner that opens every link changes nothing; the
/// change happens on POST (the button on that page, or a mail client's one-click <c>List-Unsubscribe-Post</c>). No cookie is involved, so there is
/// nothing for cross-site requests to ride on.
/// </summary>
public static class NotificationRecipientEndpoints
{
    public static IEndpointRouteBuilder MapNotificationRecipientEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var recipient = endpoints.MapGroup("/api/notifications/recipient").AllowAnonymous();
        recipient.MapGet("", PreviewAsync);
        recipient.MapPost("", ApplyAsync).DisableAntiforgery();
        return endpoints;
    }

    private static IResult PreviewAsync(string? token, NotificationRecipientService service, HttpContext context)
    {
        if (service.Inspect(token) is not { } claim) return Page(context, StatusCodes.Status400BadRequest, "This link is invalid or has expired.");
        var (question, button) = claim.Action == RecipientAction.Confirm
            ? ("Confirm that you want to receive Backup Platform alert emails at this address.", "Confirm")
            : ("Stop Backup Platform alert emails to this address.", "Unsubscribe");
        return Page(context, StatusCodes.Status200OK, question, button);
    }

    private static async Task<IResult> ApplyAsync(string? token, NotificationRecipientService service, HttpContext context, CancellationToken cancellationToken) =>
        await service.ApplyAsync(token, cancellationToken) switch
        {
            RecipientOutcome.Confirmed => Page(context, StatusCodes.Status200OK, "Thank you. This address will receive Backup Platform alert emails."),
            RecipientOutcome.Unsubscribed => Page(context, StatusCodes.Status200OK, "You are unsubscribed. No further Backup Platform alert emails will be sent to this address."),
            RecipientOutcome.Gone => Page(context, StatusCodes.Status404NotFound, "This link no longer applies."),
            _ => Page(context, StatusCodes.Status400BadRequest, "This link is invalid or has expired.")
        };

    /// <summary>A minimal self-contained page; <paramref name="button"/> adds a POST form back to the same URL (token included).</summary>
    private static IResult Page(HttpContext context, int status, string message, string? button = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'";
        var form = button is null ? string.Empty : $"<form method=\"post\"><button type=\"submit\">{WebUtility.HtmlEncode(button)}</button></form>";
        var html = $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Backup Platform</title>" +
                   "<style>body{font:16px system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem}button{font:inherit;padding:.5rem 1.2rem}</style></head>" +
                   $"<body><h1>Backup Platform</h1><p>{WebUtility.HtmlEncode(message)}</p>{form}</body></html>";
        return Results.Content(html, "text/html; charset=utf-8", statusCode: status);
    }
}
