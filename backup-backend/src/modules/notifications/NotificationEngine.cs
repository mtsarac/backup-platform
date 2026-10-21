using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Notifications;

/// <summary>
/// Turns observations into durable events and deliveries. It decides nothing about HOW anything is sent and knows nothing about backups, agents
/// or quota: detectors report facts, the engine applies the tenant's policy and the condition state machine, and everything it decides is written
/// in ONE per-tenant transaction (the outbox): either the event, its deliveries and the condition's new state exist, or none of them do. Any number
/// of backend nodes may sweep; the tenant lock plus the unique dedup key make a second sweep of the same fact a no-op.
/// </summary>
public sealed class NotificationEngine(
    INotificationStore store, INotificationObservationSource source, NotificationOptions options, TimeProvider time, ILogger<NotificationEngine> logger)
{
    private static readonly TimeSpan BurstWindow = TimeSpan.FromHours(1);

    /// <summary>One pass over every notification-enabled tenant. One tenant's failure never stops the others. Returns the number of events written.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var events = 0;
        foreach (var policy in await store.ListEnabledPoliciesAsync(cancellationToken))
        {
            try
            {
                events += await SweepTenantAsync(policy, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError("Notification sweep of tenant {TenantId} failed ({ExceptionType}); the next sweep repeats it.", policy.TenantId, exception.GetType().Name);
            }
        }
        return events;
    }

    public async Task<int> SweepTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var policy = await store.GetPolicyAsync(tenantId, cancellationToken);
        if (policy is not { Enabled: true }) return 0;
        return await SweepTenantAsync(policy, cancellationToken);
    }

    public async Task<int> SweepTenantAsync(NotificationPolicy policy, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var sets = await source.CollectAsync(policy, now, cancellationToken);
        if (sets.Count == 0) return 0;
        var written = 0;
        await store.InTenantSessionAsync(policy.TenantId, async session => written = await ApplyAsync(session, sets, now, cancellationToken), cancellationToken);
        return written;
    }

    /// <summary>Applies observations to one tenant inside <paramref name="session"/>. Public so the persistence tests drive the exact production path.</summary>
    public async Task<int> ApplyAsync(INotificationSession session, IReadOnlyList<ObservationSet> sets, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        // The policy is re-read under the tenant lock: a change made after the sweep started is honoured, not raced.
        var policy = await session.GetPolicyAsync();
        if (policy is not { Enabled: true }) return 0;
        // An email address that never confirmed (or unsubscribed) gets no alerts: it could be anyone's inbox.
        var channels = (await session.ListChannelsAsync()).Where(channel => channel.IsDeliverable && policy.ChannelKinds.Contains(channel.Kind)).ToList();
        var emitter = new Emitter(session, policy, channels, options, now);
        var reminder = TimeSpan.FromHours(policy.ReminderIntervalHours);

        foreach (var set in sets)
        {
            if (!set.IsCondition)
            {
                foreach (var observation in set.Observations)
                    await ApplyOccurrenceAsync(session, policy, emitter, observation);
                continue;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var observation in set.Observations)
            {
                if (!seen.Add(observation.Key)) continue;
                var condition = await session.GetConditionAsync(observation.Key);
                if (condition is null)
                {
                    condition = new NotificationCondition(Guid.NewGuid(), policy.TenantId, observation.Key, observation.Type, observation.Subject, now);
                    session.AddCondition(condition);
                }
                var decision = condition.ObserveActive(observation.Subject, observation.Severity, emitter.Admits(observation.Type, observation.Severity), reminder, options.FlapCooldown, now);
                if (decision is NotificationConditionDecision.Raise or NotificationConditionDecision.Remind)
                    await emitter.EmitAsync(NotificationPhase.Raised, observation.Type, observation.Severity, observation.Subject, observation.Facts, condition.EventKey(decision), observation.OccurredAt);
            }
            var ended = (await session.ListActiveConditionsAsync(set.Type)).Where(condition => !seen.Contains(condition.Key)).ToList();
            // A condition that ends because its subject was revoked, deleted or disabled was closed by an administrator: nothing recovered, so nothing is announced.
            IReadOnlySet<Guid> retired = ended.Count == 0 ? new HashSet<Guid>() : await source.RetiredSubjectsAsync(policy.TenantId, set.Type, ended.Where(condition => condition.SubjectId is not null).Select(condition => condition.SubjectId!.Value).ToList(), cancellationToken);
            foreach (var condition in ended)
            {
                var recoveryAdmitted = policy.RecoveryNotifications && emitter.Admits(condition.Type, NotificationSeverity.Critical) && !(condition.SubjectId is { } id && retired.Contains(id));
                if (condition.ObserveResolved(recoveryAdmitted, now) == NotificationConditionDecision.Recover)
                    await emitter.EmitAsync(NotificationPhase.Recovered, condition.Type, NotificationSeverity.Info,
                        new NotificationSubject(condition.SubjectKind, condition.SubjectId, condition.SubjectName), null, condition.EventKey(NotificationConditionDecision.Recover), now);
            }
        }
        return emitter.Written;
    }

    /// <summary>Queues a one-shot observation in the caller's existing tenant-locked transaction; performs no transport I/O.</summary>
    public static async Task<int> ApplyOccurrenceAsync(INotificationSession session, NotificationObservation observation, NotificationOptions options, DateTimeOffset now)
    {
        var policy = await session.GetPolicyAsync();
        if (policy is not { Enabled: true }) return 0;
        var channels = (await session.ListChannelsAsync()).Where(channel => channel.IsDeliverable && policy.ChannelKinds.Contains(channel.Kind)).ToList();
        var emitter = new Emitter(session, policy, channels, options, now);
        await ApplyOccurrenceAsync(session, policy, emitter, observation);
        return emitter.Written;
    }

    private static async Task ApplyOccurrenceAsync(INotificationSession session, NotificationPolicy policy, Emitter emitter, NotificationObservation observation)
    {
        if (!emitter.Admits(observation.Type, observation.Severity) || observation.OccurredAt < policy.ActiveSince || await session.EventExistsAsync(observation.Key)) return;
        await emitter.EmitAsync(NotificationPhase.Raised, observation.Type, observation.Severity, observation.Subject, observation.Facts, observation.Key, observation.OccurredAt);
    }

    private sealed class Emitter(INotificationSession session, NotificationPolicy policy, IReadOnlyList<NotificationChannel> channels, NotificationOptions options, DateTimeOffset now)
    {
        private int? _recent;

        public int Written { get; private set; }

        /// <summary>The policy lets the type through at this severity and at least one enabled channel of a permitted kind exists.</summary>
        public bool Admits(string type, NotificationSeverity severity) => channels.Count > 0 && policy.Admits(type, severity);

        public async Task EmitAsync(
            NotificationPhase phase, string type, NotificationSeverity severity, NotificationSubject subject, IReadOnlyDictionary<string, string>? facts, string dedupKey, DateTimeOffset occurredAt)
        {
            // Burst protection: past the cap the event is still recorded (history shows what was held back) but its deliveries are Suppressed, not sent.
            _recent ??= await session.CountEventsSinceAsync(now - BurstWindow);
            var limited = _recent >= Math.Min(policy.MaxNotificationsPerHour, options.MaxNotificationsPerHourCeiling);
            var notificationEvent = new NotificationEvent(Guid.NewGuid(), policy.TenantId, type, phase, severity, subject, facts, dedupKey, occurredAt, now);
            session.AddEvent(notificationEvent);
            foreach (var channel in channels)
                session.AddDelivery(new NotificationDelivery(Guid.NewGuid(), policy.TenantId, notificationEvent.Id, channel.Id, channel.Kind, channel.Name,
                    NotificationTargets.Display(channel.Kind, channel.Target), now, limited ? NotificationDeliveryCodes.BurstLimited : null));
            _recent++;
            Written++;
        }
    }
}
