using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.SecretProviders.Abstractions;

namespace BackupPlatform.Notifications.Senders;

/// <summary>
/// POSTs a JSON event to the tenant's HTTPS endpoint. The request carries an <c>Idempotency-Key</c> (the delivery id) so a receiver can discard the
/// duplicate that at-least-once delivery occasionally produces after a worker crash. Authentication is a bearer token or an HMAC-SHA256 signature
/// over <c>{timestamp}.{body}</c> (the secret itself is never sent). Everything about reaching the endpoint safely is in <see cref="NotificationHttpClient"/>.
/// </summary>
public sealed class WebhookNotificationSender(NotificationHttpClient http, ISecretProvider secrets, TimeProvider time) : INotificationSender
{
    public NotificationChannelKind Kind => NotificationChannelKind.Webhook;

    public async Task<SendOutcome> SendAsync(DeliveryWork work, CancellationToken cancellationToken)
    {
        if (work.Target is null || NotificationTargets.Normalize(NotificationChannelKind.Webhook, work.Target) is null) return SendOutcome.Permanent("invalid_endpoint");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = work.Event.Id,
            deliveryId = work.DeliveryId,
            tenantId = work.TenantId.Value,
            type = work.Event.Type,
            phase = work.Event.Phase.ToString().ToLowerInvariant(),
            severity = work.Event.Severity.ToString().ToLowerInvariant(),
            occurredAt = work.Event.OccurredAt,
            subject = new { kind = work.Event.SubjectKind, id = work.Event.SubjectId, name = NotificationMessageFormatter.Flatten(work.Event.SubjectName) },
            facts = work.Event.Facts,
            attempt = work.Attempt
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, work.Target) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", work.DeliveryId.ToString("D"));
        request.Headers.TryAddWithoutValidation("User-Agent", "BackupPlatform-Notifications/1");
        if (work.Authentication != WebhookAuthentication.None)
        {
            if (work.SecretReference is null) return SendOutcome.Permanent("secret_missing");
            string secret;
            try { secret = (await secrets.ResolveAsync(new SecretReference(work.SecretReference), cancellationToken)).Reveal(); }
            catch (SecretUnavailableException) { return SendOutcome.Permanent("secret_unavailable"); }
            if (work.Authentication == WebhookAuthentication.Bearer)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            }
            else
            {
                var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray()));
                request.Headers.TryAddWithoutValidation("X-Backup-Timestamp", timestamp);
                request.Headers.TryAddWithoutValidation("X-Backup-Signature", "sha256=" + signature);
            }
        }
        return await http.SendAsync(request, cancellationToken);
    }
}
