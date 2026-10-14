using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.SecretProviders.Abstractions;

namespace BackupPlatform.Notifications.Senders;

/// <summary>One SMS to hand to a gateway adapter. <see cref="Secret"/> is resolved for this call only and must never be logged or kept.</summary>
public sealed record SmsRequest(Guid MessageId, string To, string Text, SmsSettings Settings, string? Secret);

/// <summary>
/// A gateway adapter: how one kind of SMS API is called. Adding a vendor adds an adapter registered next to <see cref="GenericHttpSmsProvider"/>; the
/// delivery pipeline (durability, retry, dedup, rate limits, history) is shared and knows no vendor.
/// </summary>
public interface ISmsProvider
{
    string Key { get; }

    Task<SendOutcome> SendAsync(SmsRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The only adapter shipped: POSTs <c>{"to","text","from"?,"messageId"}</c> as JSON to the configured HTTPS endpoint and treats any 2xx as accepted.
/// It matches no particular vendor's API (a vendor with a different request shape needs its own adapter). Authentication: none, bearer token, HTTP
/// basic, or one custom header carrying the secret.
/// </summary>
public sealed class GenericHttpSmsProvider(NotificationHttpClient http) : ISmsProvider
{
    public string Key => NotificationTransportRules.GenericHttpProvider;

    public async Task<SendOutcome> SendAsync(SmsRequest request, CancellationToken cancellationToken)
    {
        var settings = request.Settings;
        if (NotificationTargets.Normalize(NotificationChannelKind.Webhook, settings.Endpoint) is null) return SendOutcome.Permanent("invalid_endpoint");
        var body = JsonSerializer.SerializeToUtf8Bytes(new { to = request.To, text = request.Text, from = settings.Sender, messageId = request.MessageId });
        using var message = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint) { Content = new ByteArrayContent(body) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.TryAddWithoutValidation("Idempotency-Key", request.MessageId.ToString("D"));
        message.Headers.TryAddWithoutValidation("User-Agent", "BackupPlatform-Notifications/1");
        switch (settings.Authentication)
        {
            case SmsAuthentication.Bearer when request.Secret is not null:
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Secret);
                break;
            case SmsAuthentication.Basic when request.Secret is not null && settings.Username is not null:
                message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Username + ":" + request.Secret)));
                break;
            case SmsAuthentication.Header when request.Secret is not null && settings.HeaderName is not null:
                message.Headers.TryAddWithoutValidation(settings.HeaderName, request.Secret);
                break;
            case not SmsAuthentication.None:
                return SendOutcome.Permanent("secret_missing");
        }
        return await http.SendAsync(message, cancellationToken);
    }
}

/// <summary>Sends SMS deliveries through the operator-configured gateway adapter. Recipients are the tenant's; the gateway and its credential are the platform's.</summary>
public sealed class SmsNotificationSender(INotificationStore store, ISecretProvider secrets, IEnumerable<ISmsProvider> providers) : INotificationSender
{
    private readonly Dictionary<string, ISmsProvider> _providers = providers.ToDictionary(provider => provider.Key, StringComparer.Ordinal);

    public NotificationChannelKind Kind => NotificationChannelKind.Sms;

    public async Task<SendOutcome> SendAsync(DeliveryWork work, CancellationToken cancellationToken)
    {
        if (work.Target is null || NotificationTargets.Normalize(NotificationChannelKind.Sms, work.Target) is null) return SendOutcome.Permanent("invalid_recipient");
        var transport = await store.GetTransportAsync(NotificationTransports.Sms, cancellationToken);
        if (NotificationTransportRules.ReadSms(transport) is not { } settings || !_providers.TryGetValue(settings.Provider, out var provider))
            return SendOutcome.Permanent("transport_unavailable");
        string? secret = null;
        if (transport!.SecretReference is { } reference)
        {
            try { secret = (await secrets.ResolveAsync(new SecretReference(reference), cancellationToken)).Reveal(); }
            catch (SecretUnavailableException) { return SendOutcome.Permanent("secret_unavailable"); }
        }
        return await provider.SendAsync(new SmsRequest(work.DeliveryId, work.Target, NotificationMessageFormatter.Render(work.Event).Short, settings, secret), cancellationToken);
    }
}
