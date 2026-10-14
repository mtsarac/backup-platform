using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Infrastructure.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.Infrastructure.Notifications;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.Notifications;
using BackupPlatform.Notifications.Senders;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.UnitTests.Notifications.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests.Notifications;

/// <summary>A secret store held in memory: the platform's own is exercised by its own suites; here only "what does the notification code do with a secret" matters.</summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];

    public IReadOnlyCollection<string> Names { get { lock (_values) return _values.Keys.ToList(); } }
    public IReadOnlyCollection<string> Values { get { lock (_values) return _values.Values.ToList(); } }

    public Task SetAsync(SecretReference reference, SecretValue value, CancellationToken cancellationToken)
    {
        lock (_values) _values[reference.Name] = value.Reveal();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        lock (_values) return Task.FromResult(_values.Remove(reference.Name));
    }

    public Task<bool> ExistsAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        lock (_values) return Task.FromResult(_values.ContainsKey(reference.Name));
    }

    public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        lock (_values) return _values.TryGetValue(reference.Name, out var value) ? ValueTask.FromResult(new SecretValue(value)) : throw new SecretUnavailableException();
    }
}

/// <summary>
/// The notification stack on a real database: real stores, engine, detectors and senders, with the clock under the test's control. Every method returns
/// independent objects (own pool) so racing callers behave like separate backend nodes.
/// </summary>
internal sealed class NotificationWorld(PostgreSqlFixture database)
{
    public static readonly NotificationOptions Loopback = new() { AllowedPrivateNetworks = ["127.0.0.0/8"], MaxAttempts = 4, BackoffBase = TimeSpan.FromSeconds(30) };

    public PostgreSqlFixture Database { get; } = database;
    public MutableClock Clock { get; } = new();
    public InMemorySecretStore Secrets { get; } = new();
    public QuotaWorld Quota { get; } = new(database);
    public NotificationOptions Options { get; init; } = Loopback;
    public NotificationActor Actor { get; } = new(UserId.New(), AuditActorKind.TenantAdministrator);
    public NotificationActor PlatformActor { get; } = new(UserId.New(), AuditActorKind.PlatformAdmin);

    public PostgreSqlNotificationStore Store() => new(Quota.Options(), Clock);

    public NotificationAdministrationService Admin(INotificationStore? store = null) =>
        new(store ?? Store(), Secrets, Options, Clock, NullLogger<NotificationAdministrationService>.Instance);

    public NotificationTransportService Transports() => new(Store(), Secrets, Clock, NullLogger<NotificationTransportService>.Instance);

    public PostgreSqlNotificationObservationSource Source() =>
        new(Quota.Options(), Quota.Usage(), new AgentConnectivityOptions(), NullLogger<PostgreSqlNotificationObservationSource>.Instance);

    public NotificationEngine Engine(INotificationObservationSource? source = null) =>
        new(Store(), source ?? Source(), Options, Clock, NullLogger<NotificationEngine>.Instance);

    /// <summary>A delivery processor with the real email and webhook senders (the webhook client trusts the test servers' self-signed certificate).</summary>
    public NotificationDeliveryProcessor Processor(NotificationOptions? options = null)
    {
        var store = Store();
        var guard = new NotificationNetworkGuard(Options);
        var http = new NotificationHttpClient(guard, handler => handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true);
        INotificationSender[] senders =
        [
            new EmailNotificationSender(store, Secrets, guard, client => client.ServerCertificateValidationCallback = (_, _, _, _) => true),
            new WebhookNotificationSender(http, Secrets, Clock)
        ];
        return new NotificationDeliveryProcessor(store, senders, options ?? Options, Clock, NullLogger<NotificationDeliveryProcessor>.Instance);
    }

    public async Task<NotificationPolicyView> EnableAsync(TenantId tenant, string[]? eventTypes = null, string severity = "warning", string[]? kinds = null,
        bool recovery = true, int staleHours = 48, int offlineMinutes = 15, int reminderHours = 24, int perHour = 30)
    {
        var result = await Admin().UpdatePolicyAsync(tenant, new PolicyCommand(true, eventTypes ?? [.. NotificationEventTypes.Selectable], severity, kinds ?? ["email", "webhook"], recovery,
            staleHours, offlineMinutes, reminderHours, perHour), Actor, default);
        Assert.True(result.Succeeded);
        return result.Value!;
    }

    public async Task<NotificationChannelView> AddEmailAsync(TenantId tenant, string address = "ops@example.com", string name = "Ops")
    {
        var result = await Admin().CreateChannelAsync(tenant, new CreateChannelCommand("email", name, address, null, null), Actor, default);
        Assert.Equal(NotificationOutcome.Changed, result.Outcome);
        return result.Value!;
    }

    public async Task<NotificationChannelView> AddWebhookAsync(TenantId tenant, string url, string name = "Hook", string? authentication = null, string? secret = null)
    {
        var result = await Admin().CreateChannelAsync(tenant, new CreateChannelCommand("webhook", name, url, authentication, secret), Actor, default);
        Assert.Equal(NotificationOutcome.Changed, result.Outcome);
        return result.Value!;
    }

    /// <summary>Configures the platform SMTP relay to an isolated test server (no encryption, no credentials: the plain-text case the platform allows).</summary>
    public async Task ConfigureSmtpAsync(int port)
    {
        var result = await Transports().UpdateAsync(NotificationTransports.Email, new TransportCommand(true, new Dictionary<string, string?>
        {
            ["host"] = "127.0.0.1", ["port"] = port.ToString(), ["security"] = "none", ["fromAddress"] = "alerts@example.com", ["fromName"] = "Alerts"
        }), PlatformActor, default);
        Assert.True(result.Succeeded);
    }

    public async Task<List<NotificationDelivery>> DeliveriesAsync(TenantId tenant)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.NotificationDeliveries.AsNoTracking().Where(delivery => delivery.TenantId == tenant).OrderBy(delivery => delivery.CreatedAt).ThenBy(delivery => delivery.Id).ToListAsync();
    }

    public async Task<List<NotificationEvent>> EventsAsync(TenantId tenant)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.NotificationEvents.AsNoTracking().Where(notificationEvent => notificationEvent.TenantId == tenant).OrderBy(notificationEvent => notificationEvent.CreatedAt)
            .ThenBy(notificationEvent => notificationEvent.DedupKey).ToListAsync();
    }

    public async Task<List<AuditEvent>> AuditAsync(TenantId? tenant)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.AuditEvents.AsNoTracking().Where(audit => audit.TenantId == tenant && audit.Action.StartsWith("notification_")).OrderBy(audit => audit.At).ToListAsync();
    }

    /// <summary>Delivers everything that is due now, repeating until nothing is, like the worker does.</summary>
    public async Task<int> DeliverAsync(NotificationDeliveryProcessor? processor = null, string owner = "worker-1")
    {
        processor ??= Processor();
        var total = 0;
        int claimed;
        do total += claimed = await processor.ProcessAsync(owner, default); while (claimed >= Options.DeliveryBatchSize);
        return total;
    }

    /// <summary>A heartbeat as an agent would produce it: the agent's last sign of life.</summary>
    public async Task HeartbeatAsync(StoreAgent agent, DateTimeOffset at) => await new PostgreSqlAgentConnectivityStore(Quota.Options()).RecordHeartbeatAsync(
        new HeartbeatWrite(agent.AgentId, agent.TenantId, at, "1.0.0", 1, 0, BackupPlatform.Domain.Agents.AgentPlatform.Linux, null, null, null), default);
}
