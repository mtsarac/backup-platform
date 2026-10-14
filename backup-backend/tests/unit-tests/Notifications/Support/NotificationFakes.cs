using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Notifications;
using BackupPlatform.SecretProviders.Abstractions;

namespace BackupPlatform.UnitTests.Notifications.Support;

internal sealed class InMemorySecrets : ISecretProvider
{
    private readonly Dictionary<string, string> _values = [];

    public void Set(string name, string value) => _values[name] = value;

    public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken cancellationToken) =>
        _values.TryGetValue(reference.Name, out var value) ? ValueTask.FromResult(new SecretValue(value)) : throw new SecretUnavailableException();
}

/// <summary>A store that supports only what a test overrides; anything else is a test bug and throws.</summary>
internal class FakeNotificationStore : INotificationStore
{
    public Dictionary<string, NotificationTransportSettings> Transports { get; } = [];

    public virtual Task<NotificationTransportSettings?> GetTransportAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Transports.GetValueOrDefault(key));
    public virtual Task<bool> InTenantSessionAsync(TenantId tenantId, Func<INotificationSession, Task> work, CancellationToken cancellationToken) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<DeliveryWork>> ClaimDueAsync(string owner, DateTimeOffset now, int batchSize, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken) => throw new NotSupportedException();
    public virtual Task<bool> CompleteAsync(Guid deliveryId, string owner, int attempt, DeliveryResult result, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<NotificationChannelRecord>?> ListChannelsAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<NotificationChannelRecord?> GetChannelAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ChannelMutationResult> CreateChannelAsync(NotificationChannel channel, NotificationActor actor, int maximumChannels, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ChannelMutationResult> MutateChannelAsync(TenantId tenantId, Guid id, NotificationActor actor, Func<NotificationChannel, ChannelChange?> mutate, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ChannelMutationResult> DeleteChannelAsync(TenantId tenantId, Guid id, NotificationActor actor, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<NotificationPolicy?> GetPolicyAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<(NotificationPolicy Policy, bool Changed)?> UpdatePolicyAsync(TenantId tenantId, NotificationActor actor, Func<NotificationPolicy, bool> apply, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PagedResult<NotificationDeliveryRecord>> ListDeliveriesAsync(TenantId tenantId, DeliveryFilter filter, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<TransportMutationResult> MutateTransportAsync(string key, NotificationActor actor, Func<NotificationTransportSettings, TransportChange?> mutate, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<NotificationPolicy>> ListEnabledPoliciesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>One tenant's notification state in memory, with the same contract as the persistent session.</summary>
internal sealed class InMemorySession : INotificationSession
{
    public NotificationPolicy? Policy { get; set; }
    public List<NotificationChannel> Channels { get; } = [];
    public List<NotificationCondition> Conditions { get; } = [];
    public List<NotificationEvent> Events { get; } = [];
    public List<NotificationDelivery> Deliveries { get; } = [];
    /// <summary>Events already persisted before this session, as (key, createdAt).</summary>
    public List<(string Key, DateTimeOffset At)> PriorEvents { get; } = [];

    public Task<NotificationPolicy?> GetPolicyAsync() => Task.FromResult(Policy);
    public Task<IReadOnlyList<NotificationChannel>> ListChannelsAsync() => Task.FromResult<IReadOnlyList<NotificationChannel>>(Channels.ToList());
    public Task<NotificationChannel?> GetChannelAsync(Guid id) => Task.FromResult(Channels.FirstOrDefault(channel => channel.Id == id));
    public Task<NotificationCondition?> GetConditionAsync(string key) => Task.FromResult(Conditions.FirstOrDefault(condition => condition.Key == key));
    public Task<IReadOnlyList<NotificationCondition>> ListActiveConditionsAsync(string type) =>
        Task.FromResult<IReadOnlyList<NotificationCondition>>(Conditions.Where(condition => condition.IsActive && condition.Type == type).ToList());
    public void AddCondition(NotificationCondition condition) => Conditions.Add(condition);
    public Task<bool> EventExistsAsync(string dedupKey) => Task.FromResult(PriorEvents.Any(prior => prior.Key == dedupKey) || Events.Any(e => e.DedupKey == dedupKey));
    public Task<int> CountEventsSinceAsync(DateTimeOffset since) => Task.FromResult(PriorEvents.Count(prior => prior.At >= since));
    public Task<int> CountTestsSinceAsync(Guid? channelId, DateTimeOffset since) => Task.FromResult(0);
    public void AddEvent(NotificationEvent notificationEvent) => Events.Add(notificationEvent);
    public void AddDelivery(NotificationDelivery delivery) => Deliveries.Add(delivery);
    public void Audit(NotificationActor actor, string action, string targetType, string targetId, IReadOnlyDictionary<string, object?> details) { }
}

internal static class NotificationTestData
{
    public static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    public static readonly DateTimeOffset Start = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    public static NotificationPolicy EnabledPolicy(DateTimeOffset now, Action<NotificationPolicy>? adjust = null, int perHour = 30, int reminderHours = 24, bool recovery = true)
    {
        var policy = NotificationPolicy.Defaults(Tenant, now.AddDays(-30));
        policy.Update(true, NotificationEventTypes.Selectable, NotificationSeverity.Warning, [NotificationChannelKind.Email, NotificationChannelKind.Webhook],
            recovery, 48, 15, reminderHours, perHour, now.AddDays(-30));
        adjust?.Invoke(policy);
        return policy;
    }

    /// <summary>An email channel; confirmed by default (as every channel that receives alerts is), unconfirmed when <paramref name="confirmed"/> is false.</summary>
    public static NotificationChannel Email(string address = "ops@example.com", string name = "Ops", bool confirmed = true)
    {
        var channel = new NotificationChannel(Guid.NewGuid(), Tenant, NotificationChannelKind.Email, name, address, WebhookAuthentication.None, Start);
        if (confirmed) channel.ConfirmRecipient(Start);
        return channel;
    }

    public static NotificationChannel Webhook(string url = "https://hooks.example.com/n", string name = "Hook", WebhookAuthentication auth = WebhookAuthentication.None) =>
        new(Guid.NewGuid(), Tenant, NotificationChannelKind.Webhook, name, url, auth, Start);

    public static NotificationObservation Condition(string type, string key, NotificationSeverity severity = NotificationSeverity.Critical, string name = "build-01", DateTimeOffset? at = null) =>
        new(key, type, severity, new NotificationSubject("agent", Guid.NewGuid(), name), at ?? Start);

    public static ObservationSet Set(string type, bool isCondition, params NotificationObservation[] observations) => new(type, isCondition, observations);

    public static DeliveryWork Work(NotificationChannelKind kind, string? target, WebhookAuthentication auth = WebhookAuthentication.None, string? secretReference = null,
        string type = NotificationEventTypes.AgentOffline, NotificationPhase phase = NotificationPhase.Raised, string subjectName = "build-01", int attempt = 1,
        IReadOnlyDictionary<string, string>? facts = null) =>
        new(Guid.NewGuid(), Tenant, attempt, kind, Guid.NewGuid(), target, auth, secretReference,
            new NotificationEventSnapshot(Guid.NewGuid(), type, phase, NotificationSeverity.Critical, "agent", Guid.NewGuid(), subjectName,
                facts ?? new Dictionary<string, string> { ["minutes"] = "42" }, Start), null);
}

/// <summary>Readable, unsigned tokens: the contract under test is what the module puts in them and does with them, not the cryptography (that is the host's Data Protection).</summary>
internal sealed class FakeRecipientTokens : IRecipientTokens
{
    public string Issue(RecipientClaim claim) => $"{claim.TenantId.Value:N}.{claim.ChannelId:N}.{(int)claim.Action}.{claim.TargetFingerprint}";

    public RecipientClaim? Read(string? token)
    {
        var parts = token?.Split('.');
        return parts is { Length: 4 } && Guid.TryParseExact(parts[0], "N", out var tenant) && Guid.TryParseExact(parts[1], "N", out var channel) && int.TryParse(parts[2], out var action)
            ? new RecipientClaim(new TenantId(tenant), channel, (RecipientAction)action, parts[3]) : null;
    }
}

/// <summary>An observation source for engine tests that feed observation sets by hand: only the subjects a test marks as retired are reported as taken out of service.</summary>
internal sealed class StubSource : INotificationObservationSource
{
    public HashSet<Guid> Retired { get; } = [];

    public Task<IReadOnlyList<ObservationSet>> CollectAsync(NotificationPolicy policy, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlySet<Guid>> RetiredSubjectsAsync(TenantId tenantId, string type, IReadOnlyCollection<Guid> subjectIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(subjectIds.Where(Retired.Contains).ToHashSet());
}
