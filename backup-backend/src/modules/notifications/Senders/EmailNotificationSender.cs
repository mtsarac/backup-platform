using System.Net.Sockets;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.SecretProviders.Abstractions;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BackupPlatform.Notifications.Senders;

/// <summary>
/// Sends one message through the operator's SMTP relay. The connection is made by <see cref="NotificationNetworkGuard"/> (permitted addresses only,
/// validated address = connected address) and handed to the client, so the relay host cannot be pointed at an internal service by a DNS trick. TLS is
/// REQUIRED unless the operator chose no encryption (which is refused when credentials are configured), and server certificates are validated by
/// the platform defaults. The message id is derived from the delivery id, so a redelivery after a crash is recognizable to the recipient's server.
/// Every message carries a signed unsubscribe link (also as a one-click <c>List-Unsubscribe</c> header); the claim ties it to the address it was sent to.
/// </summary>
/// <param name="configureClient">A seam for tests (for example to trust a test certificate); production composes none.</param>
public sealed class EmailNotificationSender(
    INotificationStore store, ISecretProvider secrets, NotificationNetworkGuard guard, IRecipientTokens tokens, NotificationOptions options, Action<SmtpClient>? configureClient = null) : INotificationSender
{
    public NotificationChannelKind Kind => NotificationChannelKind.Email;

    public async Task<SendOutcome> SendAsync(DeliveryWork work, CancellationToken cancellationToken)
    {
        if (work.Target is null || NotificationTargets.Normalize(NotificationChannelKind.Email, work.Target) is null) return SendOutcome.Permanent("invalid_recipient");
        // No unsubscribe link, no email: every message must give its recipient a way out.
        if (options.PublicBaseUrl is null) return SendOutcome.Permanent("public_url_not_configured");
        var transport = await store.GetTransportAsync(NotificationTransports.Email, cancellationToken);
        if (NotificationTransportRules.ReadEmail(transport) is not { } settings) return SendOutcome.Permanent("transport_unavailable");
        string? password = null;
        if (settings.Username is not null)
        {
            try { password = (await secrets.ResolveAsync(new SecretReference(transport!.SecretReference!), cancellationToken)).Reveal(); }
            catch (SecretUnavailableException) { return SendOutcome.Permanent("secret_unavailable"); }
        }

        var rendered = NotificationMessageFormatter.Render(work.Event);
        var fingerprint = RecipientLinks.Fingerprint(work.Target);
        string Link(RecipientAction action) => RecipientLinks.Url(options, tokens.Issue(new RecipientClaim(work.TenantId, work.ChannelId, action, fingerprint)));
        var unsubscribe = Link(RecipientAction.Unsubscribe);
        var text = work.Event.Type == NotificationEventTypes.RecipientVerification ? $"{rendered.Text}\n\nTo confirm this address, open:\n{Link(RecipientAction.Confirm)}" : rendered.Text;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName ?? NotificationMessageFormatter.Product, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(work.Target));
        message.Subject = rendered.Subject;
        message.MessageId = $"{work.DeliveryId:N}@{settings.FromAddress[(settings.FromAddress.LastIndexOf('@') + 1)..]}";
        message.Headers.Add("Auto-Submitted", "auto-generated");
        message.Headers.Add("List-Unsubscribe", $"<{unsubscribe}>");
        message.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
        message.Body = new TextPart("plain") { Text = $"{text}\n\nTo stop receiving these emails, open:\n{unsubscribe}" };

        Socket? socket = null;
        try
        {
            socket = await guard.ConnectAsync(settings.Host, settings.Port, cancellationToken);
            using var client = new SmtpClient();
            configureClient?.Invoke(client);
            var security = settings.Security switch
            {
                SmtpSecurity.Implicit => SecureSocketOptions.SslOnConnect,
                SmtpSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls
            };
            await client.ConnectAsync(socket, settings.Host, settings.Port, security, cancellationToken);
            socket = null; // owned by the client from here on
            if (password is not null) await client.AuthenticateAsync(settings.Username!, password, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
            return SendOutcome.Sent;
        }
        catch (NotificationNetworkException exception)
        {
            return exception.Code == NotificationNetworkException.Denied ? SendOutcome.Permanent(exception.Code) : SendOutcome.Transient(exception.Code);
        }
        catch (AuthenticationException)
        {
            return SendOutcome.Permanent("smtp_auth_failed");
        }
        catch (SmtpCommandException exception)
        {
            return (int)exception.StatusCode >= 500 ? SendOutcome.Permanent("smtp_rejected") : SendOutcome.Transient("smtp_temporary");
        }
        catch (SslHandshakeException)
        {
            return SendOutcome.Transient("smtp_tls_failed");
        }
        catch (NotSupportedException)
        {
            // MailKit's report that a required STARTTLS (or an authentication mechanism) is not offered: refused, never downgraded.
            return SendOutcome.Transient("smtp_tls_unavailable");
        }
        catch (Exception exception) when (exception is ServiceNotConnectedException or ServiceNotAuthenticatedException or ProtocolException or IOException or SocketException or SmtpProtocolException)
        {
            return SendOutcome.Transient("smtp_unavailable");
        }
        finally
        {
            socket?.Dispose();
        }
    }
}
