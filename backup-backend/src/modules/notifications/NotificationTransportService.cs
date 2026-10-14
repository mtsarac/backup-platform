using BackupPlatform.Domain.Notifications;
using BackupPlatform.SecretProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Notifications;

/// <summary>
/// The platform operator's configuration of the SMTP relay and the SMS gateway. PlatformAdmin-only (the endpoints are the gate). Same secret rules as
/// channels: the credential is written before the row references it, replaced credentials are deleted after the commit, and a view only says whether
/// a credential is set.
/// </summary>
public sealed class NotificationTransportService(INotificationStore store, ISecretStore secrets, TimeProvider time, ILogger<NotificationTransportService> logger)
{
    public async Task<IReadOnlyList<NotificationTransportView>> ListAsync(CancellationToken cancellationToken) =>
    [
        await GetAsync(NotificationTransports.Email, cancellationToken),
        await GetAsync(NotificationTransports.Sms, cancellationToken)
    ];

    public async Task<NotificationTransportView> GetAsync(string key, CancellationToken cancellationToken)
    {
        RequireKey(key);
        var settings = await store.GetTransportAsync(key, cancellationToken);
        return settings is null
            ? new NotificationTransportView(key, false, false, new Dictionary<string, string>(), false, null, null)
            : ToView(settings);
    }

    public async Task<NotificationResult<NotificationTransportView>> UpdateAsync(string key, TransportCommand command, NotificationActor actor, CancellationToken cancellationToken)
    {
        RequireKey(key);
        var values = key == NotificationTransports.Email
            ? NotificationTransportRules.ValidateEmail(command.Values, command.Enabled)
            : NotificationTransportRules.ValidateSms(command.Values, command.Enabled);
        var now = time.GetUtcNow();
        var result = await store.MutateTransportAsync(key, actor, settings =>
        {
            // Enabling a transport that cannot work yet (credentials configured but no stored password) is refused, not silently inert.
            var changed = settings.Apply(command.Enabled, values, now);
            if (command.Enabled && NotificationTransportRules.ReadEmail(settings) is null && NotificationTransportRules.ReadSms(settings) is null)
                throw new InvalidNotificationException("enabled", "incomplete_configuration");
            return changed ? new TransportChange(NotificationAuditActions.TransportUpdated) : null;
        }, cancellationToken);
        return Respond(result);
    }

    public async Task<NotificationResult<NotificationTransportView>> SetSecretAsync(string key, string? value, NotificationActor actor, CancellationToken cancellationToken)
    {
        RequireKey(key);
        if (string.IsNullOrEmpty(value) || value.Length > 1024 || value.Any(char.IsControl)) throw new InvalidNotificationException("value", "invalid_value");
        var reference = $"nt-{key}-{Guid.NewGuid().ToString("N")[..16]}";
        await secrets.SetAsync(new SecretReference(reference), new SecretValue(value), cancellationToken);
        var now = time.GetUtcNow();
        TransportMutationResult result;
        try { result = await store.MutateTransportAsync(key, actor, settings => new TransportChange(NotificationAuditActions.TransportSecretRotated, settings.AttachSecret(reference, now)), cancellationToken); }
        catch { await DiscardAsync(reference); throw; }
        await DiscardAsync(result.ObsoleteSecret);
        return Respond(result);
    }

    public async Task<NotificationResult<NotificationTransportView>> ClearSecretAsync(string key, NotificationActor actor, CancellationToken cancellationToken)
    {
        RequireKey(key);
        var now = time.GetUtcNow();
        var result = await store.MutateTransportAsync(key, actor, settings =>
        {
            var previous = settings.DetachSecret(now);
            if (previous is null) return null;
            // Credentials are required by the transport's own configuration: clearing them switches it off rather than leaving it enabled and broken.
            if (settings.Enabled && NotificationTransportRules.ReadEmail(settings) is null && NotificationTransportRules.ReadSms(settings) is null)
                settings.Apply(false, settings.Configuration, now);
            return new TransportChange(NotificationAuditActions.TransportSecretCleared, previous);
        }, cancellationToken);
        await DiscardAsync(result.ObsoleteSecret);
        return Respond(result);
    }

    private static void RequireKey(string key)
    {
        if (!NotificationTransports.IsValid(key)) throw new InvalidNotificationException("transport", "invalid_value");
    }

    private static NotificationResult<NotificationTransportView> Respond(TransportMutationResult result) =>
        new(result.Outcome, result.Settings is null ? null : ToView(result.Settings));

    private static NotificationTransportView ToView(NotificationTransportSettings settings) =>
        new(settings.Key, settings.Enabled, NotificationTransportRules.IsConfigured(settings), settings.Configuration, settings.HasSecret, settings.SecretUpdatedAt, settings.UpdatedAt);

    private async Task DiscardAsync(string? reference)
    {
        if (reference is null) return;
        try { await secrets.DeleteAsync(new SecretReference(reference), CancellationToken.None); }
        catch (Exception exception) { logger.LogWarning("An unreferenced notification secret could not be deleted ({ExceptionType}).", exception.GetType().Name); }
    }
}
