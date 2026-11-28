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
        var (page, subject, text) = RenderAccountMail(mail.Kind, NotificationLanguages.Parse(mail.Language));
        return await SubmitAsync(mail.To, subject, $"{text}\n\n{options.PublicBaseUrl.TrimEnd('/')}/{page}?token={mail.Token}", cancellationToken);
    }

    public async Task<bool?> SendSecurityNoticeAsync(string to, string eventType, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var (subject, body) = RenderSecurityNotice(eventType, occurredAt, NotificationLanguages.Default);
        var outcome = await SubmitAsync(to, subject, body, cancellationToken);
        return outcome.Status switch { SendStatus.Sent => true, SendStatus.Retry => null, _ => false };
    }

    public static (string Page, string Subject, string Text) RenderAccountMail(AccountMailKind kind, NotificationLanguage language)
    {
        var tr = language == NotificationLanguage.Turkish;
        return kind == AccountMailKind.PasswordReset
            ? ("reset-password", tr ? "Şifre sıfırlama" : "Password reset", tr
                ? "Şifrenizi sıfırlamak için aşağıdaki bağlantıyı açın. Bağlantı 30 dakika geçerlidir ve yalnızca bir kez kullanılabilir. Bu isteği siz yapmadıysanız bu iletiyi yok sayın."
                : "Open the link below to reset your password. The link is valid for 30 minutes and can be used only once. If you did not make this request, ignore this message.")
            : ("verify-email", tr ? "E-posta adresini doğrulama" : "Email address verification", tr
                ? "E-posta adresinizi doğrulamak için aşağıdaki bağlantıyı açın. Bağlantı 24 saat geçerlidir ve yalnızca bir kez kullanılabilir."
                : "Open the link below to verify your email address. The link is valid for 24 hours and can be used only once.");
    }

    public static (string Subject, string Body) RenderSecurityNotice(string eventType, DateTimeOffset occurredAt, NotificationLanguage language)
    {
        var tr = language == NotificationLanguage.Turkish;
        var time = tr ? "Zaman (UTC)" : "Time (UTC)";
        var advice = tr
            ? "Bu işlemi siz yapmadıysanız şifrenizi değiştirin ve bir Platform Yöneticisine haber verin."
            : "If you did not do this, change your password and tell a Platform Administrator.";
        return (tr ? "Hesap güvenliği bildirimi" : "Account security notice",
            $"{SecurityNoticeText(eventType, tr)}\n\n{time}: {occurredAt.UtcDateTime:yyyy-MM-dd HH:mm}\n\n{advice}");
    }

    private static string SecurityNoticeText(string eventType, bool tr) => eventType switch
    {
        NotificationEventTypes.SecurityPasswordChanged => tr ? "Hesabınızın şifresi değiştirildi." : "Your account password was changed.",
        NotificationEventTypes.SecurityTemporaryPassword => tr ? "Hesabınıza bir yönetici tarafından geçici şifre atandı." : "An administrator assigned a temporary password to your account.",
        NotificationEventTypes.SecurityPasswordReset => tr ? "Hesabınızın şifresi e-posta ile sıfırlandı." : "Your account password was reset by email.",
        NotificationEventTypes.SecurityAccountDisabled => tr ? "Hesabınız devre dışı bırakıldı." : "Your account was disabled.",
        NotificationEventTypes.SecurityAccountEnabled => tr ? "Hesabınız yeniden etkinleştirildi." : "Your account was enabled again.",
        NotificationEventTypes.SecurityFactorAdded => tr ? "Hesabınıza yeni bir ikinci faktör veya geçiş anahtarı eklendi." : "A new second factor or passkey was added to your account.",
        NotificationEventTypes.SecurityFactorRemoved => tr ? "Hesabınızdan bir ikinci faktör veya geçiş anahtarı kaldırıldı." : "A second factor or passkey was removed from your account.",
        NotificationEventTypes.SecurityRecoveryRegenerated => tr ? "Hesabınızın kurtarma kodları yeniden oluşturuldu." : "Your account recovery codes were regenerated.",
        NotificationEventTypes.SecurityRecoveryUsed => tr ? "Hesabınıza giriş için bir kurtarma kodu kullanıldı." : "A recovery code was used to sign in to your account.",
        NotificationEventTypes.SecurityEmailChanged => tr ? "Hesabınızın e-posta adresi değiştirildi." : "Your account email address was changed.",
        _ => tr ? "Hesabınızda şüpheli bir giriş etkinliği algılandı." : "Suspicious sign-in activity was detected on your account."
    };

    private async Task<SendOutcome> SubmitAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
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
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName ?? NotificationMessageFormatter.Product, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = $"{NotificationMessageFormatter.Product}: {subject}";
        message.Headers.Add("Auto-Submitted", "auto-generated");
        message.Body = new TextPart("plain") { Text = body };
        return await relay.SendAsync(settings, password, message, cancellationToken);
    }
}
