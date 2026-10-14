using BackupPlatform.Domain.Notifications;
using BackupPlatform.Notifications;
using BackupPlatform.UnitTests.Notifications.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static BackupPlatform.UnitTests.Notifications.Support.NotificationTestData;

namespace BackupPlatform.UnitTests.Notifications;

/// <summary>The decisions of the engine against an in-memory tenant: what becomes an event, to whom, and what is held back.</summary>
public sealed class NotificationEngineTests
{
    private static readonly NotificationOptions Options = new();

    private readonly FakeTimeProvider _time = new(Start);
    private readonly NotificationEngine _engine;
    private readonly InMemorySession _session = new();
    private readonly StubSource _source = new();

    public NotificationEngineTests()
    {
        _engine = new NotificationEngine(new FakeNotificationStore(), _source, Options, _time, NullLogger<NotificationEngine>.Instance);
        _session.Policy = EnabledPolicy(Start);
        _session.Channels.Add(Email());
        _session.Channels.Add(Webhook());
    }

    private Task<int> ApplyAsync(params ObservationSet[] sets) => _engine.ApplyAsync(_session, sets, _time.GetUtcNow());

    private static ObservationSet Offline(params string[] keys) =>
        Set(NotificationEventTypes.AgentOffline, true, keys.Select(key => Condition(NotificationEventTypes.AgentOffline, key)).ToArray());

    [Fact]
    public async Task An_event_fans_out_to_every_enabled_channel_of_a_permitted_kind_and_to_no_other()
    {
        _session.Channels.Add(Email("off@example.com", "Off").Also(channel => channel.SetEnabled(false, Start)));

        await ApplyAsync(Offline("agent_offline:a"));

        Assert.Equal([NotificationChannelKind.Email, NotificationChannelKind.Webhook], _session.Deliveries.Select(d => d.ChannelKind).Order());
        Assert.All(_session.Deliveries, d => Assert.Equal(NotificationDeliveryStatus.Pending, d.Status));

        _session.Deliveries.Clear();
        _session.Events.Clear();
        _session.Conditions.Clear();
        _session.Policy = EnabledPolicy(Start, policy => policy.Update(true, NotificationEventTypes.Selectable, NotificationSeverity.Warning, [NotificationChannelKind.Webhook], true, 48, 15, 24, 30, Start));
        await ApplyAsync(Offline("agent_offline:a"));

        Assert.Equal(NotificationChannelKind.Webhook, Assert.Single(_session.Deliveries).ChannelKind);
    }

    [Fact]
    public async Task Observing_the_same_condition_again_creates_nothing_new()
    {
        Assert.Equal(1, await ApplyAsync(Offline("agent_offline:a")));
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, await ApplyAsync(Offline("agent_offline:a")));
        Assert.Single(_session.Events);
    }

    [Fact]
    public async Task A_condition_that_ends_produces_one_recovery_naming_the_same_subject()
    {
        await ApplyAsync(Offline("agent_offline:a"));
        _time.Advance(TimeSpan.FromMinutes(30));

        await ApplyAsync(Offline());
        await ApplyAsync(Offline());

        Assert.Equal([NotificationPhase.Raised, NotificationPhase.Recovered], _session.Events.Select(e => e.Phase));
        Assert.Equal("build-01", _session.Events[1].SubjectName);
        Assert.Equal(NotificationSeverity.Info, _session.Events[1].Severity);
    }

    [Fact]
    public async Task A_condition_an_administrator_closed_ends_silently_while_a_real_recovery_is_still_announced()
    {
        await ApplyAsync(Offline("agent_offline:revoked", "agent_offline:healed"));
        _source.Retired.Add(_session.Conditions.Single(condition => condition.Key == "agent_offline:revoked").SubjectId!.Value);
        _time.Advance(TimeSpan.FromMinutes(30));

        await ApplyAsync(Offline());
        await ApplyAsync(Offline());

        Assert.Equal(2, _session.Events.Count(e => e.Phase == NotificationPhase.Raised));
        var healed = _session.Conditions.Single(condition => condition.Key == "agent_offline:healed");
        var recovery = Assert.Single(_session.Events, e => e.Phase == NotificationPhase.Recovered);
        Assert.Equal(healed.SubjectId, recovery.SubjectId);
        Assert.All(_session.Conditions, condition => Assert.False(condition.IsActive));
    }

    [Fact]
    public async Task An_incomplete_set_is_not_evidence_of_recovery()
    {
        await ApplyAsync(Offline("agent_offline:a"));
        _time.Advance(TimeSpan.FromMinutes(30));

        // A detector that failed reports no set at all (rather than an empty one): nothing is resolved.
        await ApplyAsync();

        Assert.Single(_session.Events);
        Assert.True(_session.Conditions.Single().IsActive);
    }

    [Fact]
    public async Task Recovery_is_silent_when_the_policy_does_not_want_it()
    {
        _session.Policy = EnabledPolicy(Start, recovery: false);
        await ApplyAsync(Offline("agent_offline:a"));
        _time.Advance(TimeSpan.FromMinutes(30));

        await ApplyAsync(Offline());

        Assert.Single(_session.Events);
        Assert.False(_session.Conditions.Single().IsActive);
    }

    [Fact]
    public async Task A_one_shot_failure_is_announced_once_and_only_if_it_happened_after_notifications_were_switched_on()
    {
        var policy = _session.Policy!;
        var before = new NotificationObservation("backup_failed:old", NotificationEventTypes.BackupFailed, NotificationSeverity.Critical, new("backup_job", Guid.NewGuid(), "nightly"), policy.ActiveSince!.Value.AddHours(-1));
        var after = new NotificationObservation("backup_failed:new", NotificationEventTypes.BackupFailed, NotificationSeverity.Critical, new("backup_job", Guid.NewGuid(), "nightly"), Start.AddMinutes(-5));
        var set = Set(NotificationEventTypes.BackupFailed, false, before, after);

        Assert.Equal(1, await ApplyAsync(set));
        Assert.Equal(0, await ApplyAsync(set));

        Assert.Equal("backup_failed:new", _session.Events.Single().DedupKey);
    }

    [Fact]
    public async Task A_policy_that_is_off_or_does_not_list_the_type_or_severity_creates_no_event()
    {
        _session.Policy = EnabledPolicy(Start, policy => policy.Update(true, [NotificationEventTypes.BackupStale], NotificationSeverity.Warning, [NotificationChannelKind.Email], true, 48, 15, 24, 30, Start));
        await ApplyAsync(Offline("agent_offline:a"));
        _session.Policy = EnabledPolicy(Start, policy => policy.Update(true, NotificationEventTypes.Selectable, NotificationSeverity.Critical, [NotificationChannelKind.Email], true, 48, 15, 24, 30, Start));
        await ApplyAsync(Set(NotificationEventTypes.AgentOffline, true, Condition(NotificationEventTypes.AgentOffline, "agent_offline:w", NotificationSeverity.Warning)));
        _session.Policy = EnabledPolicy(Start, policy => policy.Update(false, NotificationEventTypes.Selectable, NotificationSeverity.Info, [NotificationChannelKind.Email], true, 48, 15, 24, 30, Start));
        await ApplyAsync(Offline("agent_offline:b"));

        Assert.Empty(_session.Events);
    }

    [Fact]
    public async Task Nothing_is_created_when_no_channel_could_receive_it()
    {
        _session.Channels.Clear();

        await ApplyAsync(Offline("agent_offline:a"));

        Assert.Empty(_session.Events);
    }

    [Fact]
    public async Task A_burst_beyond_the_hourly_cap_is_recorded_but_its_deliveries_are_suppressed()
    {
        _session.Policy = EnabledPolicy(Start, perHour: 3);
        var keys = Enumerable.Range(0, 6).Select(i => $"agent_offline:{i}").ToArray();

        await ApplyAsync(Offline(keys));

        Assert.Equal(6, _session.Events.Count);
        var byEvent = _session.Deliveries.GroupBy(d => d.EventId).ToDictionary(g => g.Key, g => g.ToList());
        var sent = byEvent.Values.Count(group => group.All(d => d.Status == NotificationDeliveryStatus.Pending));
        var suppressed = byEvent.Values.Count(group => group.All(d => d.Status == NotificationDeliveryStatus.Suppressed && d.ErrorCode == NotificationDeliveryCodes.BurstLimited));
        Assert.Equal(3, sent);
        Assert.Equal(3, suppressed);
    }

    [Fact]
    public async Task Events_already_sent_this_hour_count_towards_the_cap_across_sweeps()
    {
        _session.Policy = EnabledPolicy(Start, perHour: 2);
        _session.PriorEvents.Add(("x", Start.AddMinutes(-10)));
        _session.PriorEvents.Add(("y", Start.AddMinutes(-20)));

        await ApplyAsync(Offline("agent_offline:a"));

        Assert.All(_session.Deliveries, d => Assert.Equal(NotificationDeliveryStatus.Suppressed, d.Status));
    }

    [Fact]
    public async Task A_condition_that_stays_active_is_reminded_at_the_policy_interval()
    {
        _session.Policy = EnabledPolicy(Start, reminderHours: 6);
        await ApplyAsync(Offline("agent_offline:a"));

        _time.Advance(TimeSpan.FromHours(5));
        await ApplyAsync(Offline("agent_offline:a"));
        _time.Advance(TimeSpan.FromHours(1));
        await ApplyAsync(Offline("agent_offline:a"));

        Assert.Equal(2, _session.Events.Count);
        Assert.Equal(2, _session.Events.Select(e => e.DedupKey).Distinct().Count());
    }

    [Fact]
    public async Task A_new_policy_admitting_a_waiting_condition_announces_it_without_waiting_for_the_next_change()
    {
        _session.Policy = EnabledPolicy(Start, policy => policy.Update(false, NotificationEventTypes.Selectable, NotificationSeverity.Warning, [NotificationChannelKind.Email], true, 48, 15, 24, 30, Start));
        await ApplyAsync(Offline("agent_offline:a"));
        Assert.Empty(_session.Events);

        _session.Policy = EnabledPolicy(Start);
        _time.Advance(TimeSpan.FromMinutes(1));
        await ApplyAsync(Offline("agent_offline:a"));

        Assert.Single(_session.Events);
    }
}

internal static class Extensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
