using System.Threading.Channels;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Identity;
using BackupPlatform.Notifications;
using BackupPlatform.Notifications.Senders;
using BackupPlatform.SecretProviders.Abstractions;
using MimeKit;

namespace BackupPlatform.Api.Notifications;

/// <summary>
/// Sends account mail (verification, password reset) through the platform SMTP relay of the notification transports. Sending happens on a background
/// loop so a request answers in the same time whether or not a message goes out. Delivery is best effort: a lost message is fixed by requesting again.
/// </summary>
public sealed class AccountMailer(IServiceScopeFactory scopes, NotificationOptions options, NotificationNetworkGuard guard, ILogger<AccountMailer> logger) : BackgroundService, IAccountMailer
{
    private readonly Channel<AccountMail> queue = Channel.CreateBounded<AccountMail>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SmtpRelay relay = new(guard);

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (options.PublicBaseUrl is null) return false;
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
        return NotificationTransportRules.ReadEmail(await store.GetTransportAsync(NotificationTransports.Email, cancellationToken)) is not null;
    }

    public void Enqueue(AccountMail mail)
    {
        if (!queue.Writer.TryWrite(mail)) logger.LogWarning("Account mail queue is full; a {Kind} message was dropped.", mail.Kind);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var mail in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var outcome = await SendAsync(mail, stoppingToken);
                if (outcome.Status != SendStatus.Sent) logger.LogWarning("Account mail {Kind} was not sent: {Code}.", mail.Kind, outcome.Code);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Account mail {Kind} failed.", mail.Kind);
            }
        }
    }

    private async Task<SendOutcome> SendAsync(AccountMail mail, CancellationToken cancellationToken)
    {
        if (options.PublicBaseUrl is null) return SendOutcome.Permanent("public_url_not_configured");
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
        var transport = await store.GetTransportAsync(NotificationTransports.Email, cancellationToken);
        if (NotificationTransportRules.ReadEmail(transport) is not { } settings) return SendOutcome.Permanent("transport_unavailable");
        string? password = null;
        if (settings.Username is not null)
        {
            try { password = (await scope.ServiceProvider.GetRequiredService<ISecretProvider>().ResolveAsync(new SecretReference(transport!.SecretReference!), cancellationToken)).Reveal(); }
            catch (SecretUnavailableException) { return SendOutcome.Permanent("secret_unavailable"); }
        }
        var (page, subject, text) = mail.Kind == AccountMailKind.PasswordReset
            ? ("reset-password", "Şifre sıfırlama", "Şifrenizi sıfırlamak için aşağıdaki bağlantıyı açın. Bağlantı 30 dakika geçerlidir ve yalnızca bir kez kullanılabilir. Bu isteği siz yapmadıysanız bu iletiyi yok sayın.")
            : ("verify-email", "E-posta adresini doğrulama", "E-posta adresinizi doğrulamak için aşağıdaki bağlantıyı açın. Bağlantı 24 saat geçerlidir ve yalnızca bir kez kullanılabilir.");
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName ?? NotificationMessageFormatter.Product, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(mail.To));
        message.Subject = $"{NotificationMessageFormatter.Product}: {subject}";
        message.Headers.Add("Auto-Submitted", "auto-generated");
        message.Body = new TextPart("plain") { Text = $"{text}\n\n{options.PublicBaseUrl.TrimEnd('/')}/{page}?token={mail.Token}" };
        return await relay.SendAsync(settings, password, message, cancellationToken);
    }
}
