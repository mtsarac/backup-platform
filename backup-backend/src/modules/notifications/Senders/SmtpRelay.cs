using System.Net.Sockets;
using BackupPlatform.Domain.Notifications;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BackupPlatform.Notifications.Senders;

/// <summary>One SMTP submission through the operator's relay, with the guarded connection and the TLS rules described on <see cref="EmailNotificationSender"/>.</summary>
public sealed class SmtpRelay(NotificationNetworkGuard guard, Action<SmtpClient>? configureClient = null)
{
    public async Task<SendOutcome> SendAsync(SmtpSettings settings, string? password, MimeMessage message, CancellationToken cancellationToken)
    {
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
