using BackupPlatform.Domain.Notifications;
using BackupPlatform.UnitTests.Notifications.Support;
using static BackupPlatform.UnitTests.Notifications.Support.NotificationTestData;

namespace BackupPlatform.UnitTests.Notifications;

public sealed class NotificationConditionTests
{
    private static readonly NotificationSubject Subject = new("agent", Guid.NewGuid(), "build-01");
    private static readonly TimeSpan NoReminder = TimeSpan.Zero;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    private static NotificationCondition New() => new(Guid.NewGuid(), Tenant, "agent_offline:1", NotificationEventTypes.AgentOffline, Subject, Start);

    private static NotificationConditionDecision Active(NotificationCondition condition, DateTimeOffset at, NotificationSeverity severity = NotificationSeverity.Critical,
        bool admitted = true, TimeSpan? reminder = null) =>
        condition.ObserveActive(Subject, severity, admitted, reminder ?? NoReminder, Cooldown, at);

    [Fact]
    public void A_steady_condition_notifies_once_however_often_it_is_observed()
    {
        var condition = New();

        Assert.Equal(NotificationConditionDecision.Raise, Active(condition, Start));
        for (var minute = 1; minute <= 120; minute++)
            Assert.Equal(NotificationConditionDecision.None, Active(condition, Start.AddMinutes(minute)));
    }

    [Fact]
    public void A_still_active_condition_is_reminded_once_per_interval_only()
    {
        var condition = New();
        var interval = TimeSpan.FromHours(24);
        Active(condition, Start, reminder: interval);

        Assert.Equal(NotificationConditionDecision.None, Active(condition, Start.AddHours(23), reminder: interval));
        Assert.Equal(NotificationConditionDecision.Remind, Active(condition, Start.AddHours(24), reminder: interval));
        Assert.Equal(NotificationConditionDecision.None, Active(condition, Start.AddHours(25), reminder: interval));
        Assert.Equal(NotificationConditionDecision.Remind, Active(condition, Start.AddHours(48), reminder: interval));
    }

    [Fact]
    public void Escalation_notifies_again_but_a_later_improvement_stays_quiet()
    {
        var condition = New();
        Active(condition, Start, NotificationSeverity.Warning);

        Assert.Equal(NotificationConditionDecision.Raise, Active(condition, Start.AddMinutes(5), NotificationSeverity.Critical));
        Assert.Equal(NotificationConditionDecision.None, Active(condition, Start.AddMinutes(10), NotificationSeverity.Warning));
        Assert.Equal(NotificationConditionDecision.None, Active(condition, Start.AddMinutes(15), NotificationSeverity.Critical));
    }

    [Fact]
    public void Recovery_is_reported_only_for_a_condition_that_was_notified()
    {
        var notified = New();
        Active(notified, Start);
        var silent = New();
        Active(silent, Start, admitted: false);

        Assert.Equal(NotificationConditionDecision.Recover, notified.ObserveResolved(recoveryAdmitted: true, Start.AddHours(1)));
        Assert.Equal(NotificationConditionDecision.None, silent.ObserveResolved(recoveryAdmitted: true, Start.AddHours(1)));
        Assert.False(notified.IsActive);
        Assert.Equal(NotificationConditionDecision.None, notified.ObserveResolved(true, Start.AddHours(2)));
    }

    [Fact]
    public void Switching_recovery_notifications_off_ends_the_condition_without_a_message()
    {
        var condition = New();
        Active(condition, Start);

        Assert.Equal(NotificationConditionDecision.None, condition.ObserveResolved(recoveryAdmitted: false, Start.AddHours(1)));
        Assert.False(condition.IsActive);
    }

    [Fact]
    public void A_flap_inside_the_cooldown_is_withheld_and_announced_once_the_cooldown_passed()
    {
        var condition = New();
        Active(condition, Start);
        condition.ObserveResolved(true, Start.AddMinutes(2));

        Assert.Equal(NotificationConditionDecision.None, Active(condition, Start.AddMinutes(5)));
        Assert.Equal(1, condition.SuppressedCount);
        // It is still down after the cooldown: the withheld notification is sent now, not lost.
        Assert.Equal(NotificationConditionDecision.Raise, Active(condition, Start.AddMinutes(16)));
        Assert.Equal("agent_offline:1#2.1", condition.EventKey(NotificationConditionDecision.Raise));
    }

    [Fact]
    public void A_condition_the_policy_does_not_admit_is_announced_when_the_policy_starts_admitting_it()
    {
        var condition = New();

        Assert.Equal(NotificationConditionDecision.None, Active(condition, Start, admitted: false));
        Assert.Equal(NotificationConditionDecision.Raise, Active(condition, Start.AddHours(1), admitted: true));
    }

    [Fact]
    public void Every_event_of_every_activation_has_its_own_dedup_key()
    {
        var condition = New();
        var keys = new List<string>();
        for (var round = 0; round < 3; round++)
        {
            var at = Start.AddHours(round * 2);
            Active(condition, at);
            keys.Add(condition.EventKey(NotificationConditionDecision.Raise));
            condition.ObserveResolved(true, at.AddHours(1));
            keys.Add(condition.EventKey(NotificationConditionDecision.Recover));
        }

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
