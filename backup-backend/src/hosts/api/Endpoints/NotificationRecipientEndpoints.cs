using System.Net;
using System.Security.Cryptography;
using System.Text;
using BackupPlatform.Api.Notifications;
using BackupPlatform.Notifications;
using Microsoft.AspNetCore.Mvc;

namespace BackupPlatform.Api.Endpoints;

/// <summary>
/// The links in notification emails. They are anonymous by design: the signed token is the whole credential and grants exactly one action (confirm or
/// unsubscribe) on one channel. No cookie is involved, so there is nothing for cross-site requests to ride on.
/// <list type="bullet">
/// <item><b>The page link</b> carries the token in the URL fragment, which browsers never transmit, so it appears in no access log. GET returns one static
/// page whose script moves the token from the fragment into a form body; nothing changes until the person presses the button (a mail scanner that opens
/// the link runs no script and changes nothing).</item>
/// <item><b>The one-click header</b> (RFC 8058) is a POST by a mail client with the token in the query. It is an unsubscribe token only.</item>
/// </list>
/// Both are POSTs to <c>/{confirm|unsubscribe}</c>; <see cref="RecipientLinkThrottle"/> bounds them without ever refusing a first genuine request.
/// </summary>
public static class NotificationRecipientEndpoints
{
    private const string Script =
        "var t=new URLSearchParams(location.hash.slice(1)).get('token');" +
        "if(t){document.getElementById('t').value=t;document.getElementById('f').hidden=false;history.replaceState(null,'',location.pathname)}" +
        "else{document.getElementById('m').textContent='This link is incomplete or has expired.'}";

    private static readonly string ScriptPolicy =
        $"default-src 'none'; style-src 'unsafe-inline'; script-src 'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Script)))}'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    public static IEndpointRouteBuilder MapNotificationRecipientEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var recipient = endpoints.MapGroup("/api/notifications/recipient").AllowAnonymous();
        recipient.MapGet("/{route}", PageAsync);
        recipient.MapPost("/{route}", ApplyAsync).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(4096));
        return endpoints;
    }

    private static IResult PageAsync(string route, HttpContext context)
    {
        if (!TryAction(route, out var action)) return Results.NotFound();
        var (question, button) = action == RecipientAction.Confirm
            ? ("Confirm that you want to receive Backup Platform alert emails at this address.", "Confirm")
            : ("Stop Backup Platform alert emails to this address.", "Unsubscribe");
        Headers(context, ScriptPolicy);
        var html = Document($"<p id=\"m\">{WebUtility.HtmlEncode(question)}</p><form id=\"f\" method=\"post\" hidden><input type=\"hidden\" id=\"t\" name=\"token\">" +
                            $"<button type=\"submit\">{WebUtility.HtmlEncode(button)}</button></form><noscript><p>Enable JavaScript to continue.</p></noscript><script>{Script}</script>");
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static async Task<IResult> ApplyAsync(string route, HttpContext context, NotificationRecipientService service, RecipientLinkThrottle throttle, CancellationToken cancellationToken)
    {
        if (!TryAction(route, out var action)) return Results.NotFound();
        // A mail client's one-click POST carries the token in the query; the page posts it as a form field. Both are read, neither is ever logged here.
        var token = context.Request.Query["token"].ToString();
        if (token.Length == 0 && context.Request.HasFormContentType)
        {
            try { token = (await context.Request.ReadFormAsync(cancellationToken))["token"].ToString(); }
            catch (InvalidDataException) { } // a malformed body is an invalid request, answered (and counted) like a forged token
        }

        var client = RecipientLinkThrottle.ClientKey(context);
        if (service.Inspect(token) is not { } claim || claim.Action != action)
        {
            if (throttle.IsBlocked(client)) return Limited(context, throttle);
            throttle.RecordInvalid(client);
            return Message(context, StatusCodes.Status400BadRequest, "This link is invalid or has expired.");
        }
        if (!throttle.TryAcquire(token)) return Limited(context, throttle);

        return await service.ApplyAsync(token, cancellationToken) switch
        {
            RecipientOutcome.Confirmed => Message(context, StatusCodes.Status200OK, "Thank you. This address will receive Backup Platform alert emails."),
            RecipientOutcome.Unsubscribed => Message(context, StatusCodes.Status200OK, "You are unsubscribed. No further Backup Platform alert emails will be sent to this address."),
            RecipientOutcome.Gone => Message(context, StatusCodes.Status404NotFound, "This link no longer applies."),
            _ => Message(context, StatusCodes.Status400BadRequest, "This link is invalid or has expired.")
        };
    }

    private static bool TryAction(string route, out RecipientAction action)
    {
        action = route switch { "confirm" => RecipientAction.Confirm, "unsubscribe" => RecipientAction.Unsubscribe, _ => 0 };
        return action != 0;
    }

    private static IResult Limited(HttpContext context, RecipientLinkThrottle throttle)
    {
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(throttle.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Message(context, StatusCodes.Status429TooManyRequests, "Too many requests. Try again in a minute.");
    }

    private static void Headers(HttpContext context, string contentSecurityPolicy)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = contentSecurityPolicy;
    }

    private static IResult Message(HttpContext context, int status, string message)
    {
        Headers(context, "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'");
        return Results.Content(Document($"<p>{WebUtility.HtmlEncode(message)}</p>"), "text/html; charset=utf-8", statusCode: status);
    }

    private static string Document(string body) =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Backup Platform</title>" +
        "<style>body{font:16px system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem}button{font:inherit;padding:.5rem 1.2rem}</style></head>" +
        $"<body><h1>Backup Platform</h1>{body}</body></html>";
}
