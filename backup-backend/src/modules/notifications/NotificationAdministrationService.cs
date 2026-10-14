using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.SecretProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Notifications;

/// <summary>
/// A tenant's notification administration: channels, policy and delivery history. Callers authorize first (these are tenant-administrator
/// operations) and always pass the tenant they authorized, so another tenant's id is simply not found. Credentials enter through
/// <see cref="ISecretStore"/> and leave only as "set or not"; a view, an audit event and a log line never carry a secret or a full target.
/// </summary>
/// <remarks>
/// Secret/row consistency: a new secret is written under a fresh reference BEFORE the row points at it and the previous one is deleted only AFTER the
/// row's change committed, so a failure in between leaves at worst an unreferenced ciphertext, never a channel pointing at a missing secret.
/// </remarks>
public sealed class NotificationAdministrationService(
    INotificationStore store, ISecretStore secrets, NotificationOptions options, TimeProvider time, ILogger<NotificationAdministrationService> logger)
{
    private const int MaximumSecretLength = 1024;

    // ---- policy -------------------------------------------------------------------------------------------------------------------------

    public async Task<NotificationPolicyView?> GetPolicyAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        if (await store.ListChannelsAsync(tenantId, cancellationToken) is null) return null;
        var stored = await store.GetPolicyAsync(tenantId, cancellationToken);
        return await ToViewAsync(stored ?? NotificationPolicy.Defaults(tenantId, time.GetUtcNow()), hasRow: stored is not null, cancellationToken);
    }

    public async Task<NotificationResult<NotificationPolicyView>> UpdatePolicyAsync(TenantId tenantId, PolicyCommand command, NotificationActor actor, CancellationToken cancellationToken)
    {
        var severity = ParseEnum<NotificationSeverity>(command.MinimumSeverity, "minimumSeverity");
        var kinds = (command.ChannelKinds ?? throw new InvalidNotificationException("channelKinds", "required")).Select(kind => ParseEnum<NotificationChannelKind>(kind, "channelKinds")).ToList();
        var types = command.EventTypes ?? throw new InvalidNotificationException("eventTypes", "required");
        if (types.Any(type => !NotificationEventTypes.IsSelectable(type))) throw new InvalidNotificationException("eventTypes", "invalid_value");
        Check(command.StaleAfterHours, NotificationPolicy.MinimumStaleAfterHours, NotificationPolicy.MaximumStaleAfterHours, "staleAfterHours");
        Check(command.AgentOfflineAfterMinutes, NotificationPolicy.MinimumAgentOfflineMinutes, NotificationPolicy.MaximumAgentOfflineMinutes, "agentOfflineAfterMinutes");
        Check(command.ReminderIntervalHours, 0, NotificationPolicy.MaximumReminderIntervalHours, "reminderIntervalHours");
        Check(command.MaxNotificationsPerHour, NotificationPolicy.MinimumPerHour, Math.Min(NotificationPolicy.MaximumPerHour, options.MaxNotificationsPerHourCeiling), "maxNotificationsPerHour");
        var now = time.GetUtcNow();
        var result = await store.UpdatePolicyAsync(tenantId, actor, policy => policy.Update(command.Enabled, types, severity, kinds, command.RecoveryNotifications,
            command.StaleAfterHours, command.AgentOfflineAfterMinutes, command.ReminderIntervalHours, command.MaxNotificationsPerHour, now), cancellationToken);
        if (result is null) return new(NotificationOutcome.TenantNotFound);
        return new(result.Value.Changed ? NotificationOutcome.Changed : NotificationOutcome.Unchanged, await ToViewAsync(result.Value.Policy, true, cancellationToken));
    }

    private async Task<NotificationPolicyView> ToViewAsync(NotificationPolicy policy, bool hasRow, CancellationToken cancellationToken)
    {
        var email = NotificationTransportRules.ReadEmail(await store.GetTransportAsync(NotificationTransports.Email, cancellationToken)) is not null;
        var sms = NotificationTransportRules.ReadSms(await store.GetTransportAsync(NotificationTransports.Sms, cancellationToken)) is not null;
        return new NotificationPolicyView(policy.Enabled, policy.EventTypes, Lower(policy.MinimumSeverity), policy.ChannelKinds.Select(Lower).ToList(), policy.RecoveryNotifications,
            policy.StaleAfterHours, policy.AgentOfflineAfterMinutes, policy.ReminderIntervalHours, policy.MaxNotificationsPerHour, hasRow ? policy.UpdatedAt : null, email, sms);
    }

    // ---- channels -----------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<NotificationChannelView>?> ListChannelsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        (await store.ListChannelsAsync(tenantId, cancellationToken))?.Select(ToView).ToList();

    public async Task<NotificationChannelView?> GetChannelAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken) =>
        await store.GetChannelAsync(tenantId, id, cancellationToken) is { } record ? ToView(record) : null;

    public async Task<NotificationResult<NotificationChannelView>> CreateChannelAsync(TenantId tenantId, CreateChannelCommand command, NotificationActor actor, CancellationToken cancellationToken)
    {
        var kind = ParseEnum<NotificationChannelKind>(command.Kind, "kind");
        var authentication = command.Authentication is null ? WebhookAuthentication.None : ParseEnum<WebhookAuthentication>(command.Authentication, "authentication");
        if (kind != NotificationChannelKind.Webhook && authentication != WebhookAuthentication.None) throw new InvalidNotificationException("authentication", "not_supported");
        var target = NotificationTargets.Normalize(kind, command.Target) ?? throw new InvalidNotificationException("target", "invalid_value");
        NotificationChannel.Names(command.Name);
        var secret = ValidateSecret(command.Secret, authentication);
        var now = time.GetUtcNow();
        var channel = new NotificationChannel(Guid.NewGuid(), tenantId, kind, command.Name!, target, authentication, now);
        string? reference = null;
        if (secret is not null)
        {
            reference = NewReference(channel.Id);
            await secrets.SetAsync(new SecretReference(reference), secret, cancellationToken);
            channel.AttachSecret(reference, now);
        }
        ChannelMutationResult result;
        try { result = await store.CreateChannelAsync(channel, actor, options.MaxChannelsPerTenant, cancellationToken); }
        catch { await DiscardAsync(reference); throw; }
        if (result.Outcome != NotificationOutcome.Changed) await DiscardAsync(reference);
        return Respond(result);
    }

    public async Task<NotificationResult<NotificationChannelView>> UpdateChannelAsync(TenantId tenantId, Guid id, UpdateChannelCommand command, NotificationActor actor, CancellationToken cancellationToken)
    {
        if (await KindOfAsync(tenantId, id, cancellationToken) is not { } kind) return new(NotificationOutcome.NotFound);
        var target = command.Target is null ? null : NotificationTargets.Normalize(kind, command.Target);
        if (command.Target is not null && target is null) throw new InvalidNotificationException("target", "invalid_value");
        if (command.Name is not null) NotificationChannel.Names(command.Name);
        var authentication = command.Authentication is null ? (WebhookAuthentication?)null : ParseEnum<WebhookAuthentication>(command.Authentication, "authentication");
        var now = time.GetUtcNow();
        var result = await store.MutateChannelAsync(tenantId, id, actor, channel =>
        {
            if (authentication is not null && channel.Kind != NotificationChannelKind.Webhook && authentication != WebhookAuthentication.None)
                throw new InvalidNotificationException("authentication", "not_supported");
            var fields = new List<string>();
            if (command.Name is not null && channel.Rename(command.Name, now)) fields.Add("name");
            if (target is not null && channel.Retarget(target, now)) fields.Add("target");
            string? obsolete = null;
            if (authentication is { } wanted)
            {
                obsolete = channel.ChangeAuthentication(wanted, now, out var changed);
                if (changed) fields.Add("authentication");
            }
            return fields.Count == 0 ? null : new ChannelChange(NotificationAuditActions.ChannelUpdated, obsolete, new Dictionary<string, object?> { ["fields"] = fields });
        }, cancellationToken);
        await DiscardAsync(result.ObsoleteSecret);
        return Respond(result);
    }

    public async Task<NotificationResult<NotificationChannelView>> SetSecretAsync(TenantId tenantId, Guid id, string? value, NotificationActor actor, CancellationToken cancellationToken)
    {
        var secret = ValidateSecret(value, WebhookAuthentication.Bearer) ?? throw new InvalidNotificationException("value", "required");
        var reference = NewReference(id);
        await secrets.SetAsync(new SecretReference(reference), secret, cancellationToken);
        var now = time.GetUtcNow();
        ChannelMutationResult result;
        try
        {
            result = await store.MutateChannelAsync(tenantId, id, actor, channel =>
            {
                if (channel.Authentication == WebhookAuthentication.None) throw new InvalidNotificationException("value", "authentication_none");
                return new ChannelChange(NotificationAuditActions.ChannelSecretRotated, channel.AttachSecret(reference, now));
            }, cancellationToken);
        }
        catch { await DiscardAsync(reference); throw; }
        if (result.Outcome != NotificationOutcome.Changed) await DiscardAsync(reference);
        await DiscardAsync(result.ObsoleteSecret);
        return Respond(result);
    }

    public async Task<NotificationResult<NotificationChannelView>> ClearSecretAsync(TenantId tenantId, Guid id, NotificationActor actor, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var result = await store.MutateChannelAsync(tenantId, id, actor,
            channel => channel.DetachSecret(now) is { } previous ? new ChannelChange(NotificationAuditActions.ChannelSecretCleared, previous) : null, cancellationToken);
        await DiscardAsync(result.ObsoleteSecret);
        return Respond(result);
    }

    public async Task<NotificationResult<NotificationChannelView>> SetEnabledAsync(TenantId tenantId, Guid id, bool enabled, NotificationActor actor, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return Respond(await store.MutateChannelAsync(tenantId, id, actor,
            channel => channel.SetEnabled(enabled, now) ? new ChannelChange(enabled ? NotificationAuditActions.ChannelEnabled : NotificationAuditActions.ChannelDisabled) : null, cancellationToken));
    }

    public async Task<NotificationOutcome> DeleteChannelAsync(TenantId tenantId, Guid id, NotificationActor actor, CancellationToken cancellationToken)
    {
        var result = await store.DeleteChannelAsync(tenantId, id, actor, cancellationToken);
        await DiscardAsync(result.ObsoleteSecret);
        return result.Outcome;
    }

    /// <summary>
    /// Queues a test notification for one channel through the normal pipeline (so it exercises retry, history and rate limits like a real one),
    /// regardless of the tenant's policy. Limited per channel per hour.
    /// </summary>
    public async Task<NotificationResult<TestRequestView>> RequestTestAsync(TenantId tenantId, Guid channelId, NotificationActor actor, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var outcome = NotificationOutcome.NotFound;
        Guid? deliveryId = null;
        var found = await store.InTenantSessionAsync(tenantId, async session =>
        {
            if (await session.GetChannelAsync(channelId) is not { } channel) return;
            if (!channel.Enabled) throw new InvalidNotificationException("channel", "disabled");
            if (NotificationTransports.KeyOf(channel.Kind) is { } key && !NotificationTransportRules.IsAvailable(await store.GetTransportAsync(key, cancellationToken)))
            {
                outcome = NotificationOutcome.TransportNotConfigured;
                return;
            }
            if (await session.CountTestsSinceAsync(channelId, now - TimeSpan.FromHours(1)) >= options.TestNotificationsPerChannelPerHour)
            {
                outcome = NotificationOutcome.TestRateLimited;
                return;
            }
            var subject = new NotificationSubject("channel", channel.Id, channel.Name);
            var notificationEvent = new NotificationEvent(Guid.NewGuid(), tenantId, NotificationEventTypes.Test, NotificationPhase.Test, NotificationSeverity.Info, subject, null,
                $"test:{Guid.NewGuid():N}", now, now);
            var delivery = new NotificationDelivery(Guid.NewGuid(), tenantId, notificationEvent.Id, channel.Id, channel.Kind, channel.Name, NotificationTargets.Display(channel.Kind, channel.Target), now);
            session.AddEvent(notificationEvent);
            session.AddDelivery(delivery);
            session.Audit(actor, NotificationAuditActions.ChannelTestRequested, NotificationAuditActions.ChannelTarget, channel.Id.ToString("D"),
                new Dictionary<string, object?> { ["kind"] = channel.Kind.ToString(), ["name"] = channel.Name, ["deliveryId"] = delivery.Id.ToString("D") });
            deliveryId = delivery.Id;
            outcome = NotificationOutcome.Changed;
        }, cancellationToken);
        return found ? new(outcome, deliveryId is { } id ? new TestRequestView(id) : null) : new(NotificationOutcome.TenantNotFound);
    }

    // ---- history ------------------------------------------------------------------------------------------------------------------------

    public async Task<PagedResult<NotificationDeliveryView>> ListDeliveriesAsync(
        TenantId tenantId, string? status, string? kind, string? eventType, int page, int pageSize, CancellationToken cancellationToken)
    {
        var filter = new DeliveryFilter(
            status is null ? null : ParseEnum<NotificationDeliveryStatus>(Pascal(status), "status"),
            kind is null ? null : ParseEnum<NotificationChannelKind>(kind, "kind"),
            eventType is null ? null : (eventType == NotificationEventTypes.Test || NotificationEventTypes.IsSelectable(eventType) ? eventType : throw new InvalidNotificationException("eventType", "invalid_value")));
        var result = await store.ListDeliveriesAsync(tenantId, filter, page, pageSize, cancellationToken);
        return new PagedResult<NotificationDeliveryView>(result.Items.Select(ToView).ToList(), result.TotalCount, result.Page, result.PageSize);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------------

    private async Task<NotificationChannelKind?> KindOfAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken) =>
        (await store.GetChannelAsync(tenantId, id, cancellationToken))?.Channel.Kind;

    private static SecretValue? ValidateSecret(string? value, WebhookAuthentication authentication)
    {
        if (value is null) return null;
        if (authentication == WebhookAuthentication.None) throw new InvalidNotificationException("secret", "authentication_none");
        if (value.Length is 0 or > MaximumSecretLength || value.Any(char.IsControl)) throw new InvalidNotificationException("secret", "invalid_value");
        return new SecretValue(value);
    }

    private static string NewReference(Guid channelId) => $"nc-{channelId:N}-{Guid.NewGuid().ToString("N")[..12]}";

    private async Task DiscardAsync(string? reference)
    {
        if (reference is null) return;
        try { await secrets.DeleteAsync(new SecretReference(reference), CancellationToken.None); }
        catch (Exception exception) { logger.LogWarning("An unreferenced notification secret could not be deleted ({ExceptionType}).", exception.GetType().Name); }
    }

    private static NotificationResult<NotificationChannelView> Respond(ChannelMutationResult result) =>
        new(result.Outcome, result.Record is null ? null : ToView(result.Record));

    private static NotificationChannelView ToView(NotificationChannelRecord record)
    {
        var channel = record.Channel;
        return new(channel.Id, Lower(channel.Kind), channel.Name, NotificationTargets.Display(channel.Kind, channel.Target), channel.Enabled, Lower(channel.Authentication),
            channel.HasSecret, channel.SecretUpdatedAt, channel.CreatedAt, channel.UpdatedAt,
            record.Last is { } last ? new LastDeliveryView(Snake(last.Status), last.At, last.ErrorCode) : null);
    }

    private static NotificationDeliveryView ToView(NotificationDeliveryRecord record)
    {
        var d = record.Delivery;
        return new(d.Id, d.EventId, record.EventType, Lower(record.Phase), Lower(record.Severity), record.SubjectKind, record.SubjectName, Lower(d.ChannelKind), d.ChannelName,
            d.TargetDisplay, Snake(d.Status), d.AttemptCount, d.ErrorCode, d.Status == NotificationDeliveryStatus.Pending ? d.NextAttemptAt : null, d.SentAt, d.CreatedAt);
    }

    private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static string Snake(NotificationDeliveryStatus status) => status == NotificationDeliveryStatus.InProgress ? "in_progress" : Lower(status);

    private static string Pascal(string snake) => string.Concat(snake.Split('_').Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    private static T ParseEnum<T>(string? value, string field) where T : struct, Enum =>
        value is not null && !value.All(char.IsDigit) && Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidNotificationException(field, value is null ? "required" : "invalid_value");

    private static void Check(int value, int minimum, int maximum, string field)
    {
        if (value < minimum || value > maximum) throw new InvalidNotificationException(field, "out_of_range");
    }
}

public sealed record TestRequestView(Guid DeliveryId);
