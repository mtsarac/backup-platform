using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Notifications;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.Infrastructure.Notifications;

/// <summary>
/// Notification persistence under explicit platform operations; the endpoints and workers that call it are the authorization boundary and always pass the
/// tenant they authorized, so a tenant-scoped lookup of another tenant's id finds nothing.
/// </summary>
/// <remarks>
/// Every tenant write takes the tenant's notification lock (<c>pg_advisory_xact_lock</c>) first, so on any node channel changes, policy changes and the
/// engine's decisions for one tenant are serialized, and an event, its deliveries and the condition's new state commit together (the outbox). Audit
/// events are written in the same transaction as the change they describe. Deliveries are claimed with <c>FOR UPDATE SKIP LOCKED</c> under a lease, and
/// every result is a fenced update (owner + attempt), so concurrent workers never process one delivery at the same time and a stale worker cannot
/// overwrite its successor.
/// </remarks>
public sealed class PostgreSqlNotificationStore(DbContextOptions<PlatformDbContext> options, TimeProvider time) : INotificationStore
{
    private const string UniqueViolation = "23505";

    private PlatformDbContext Open() => PlatformDbContext.CreateForPlatformOperations(options);

    private static Task LockAsync(PlatformDbContext context, string scope, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({scope}, 0))", cancellationToken);

    private static string TenantLock(TenantId tenantId) => "notifications:" + tenantId.Value.ToString("D");

    // ---- channels -----------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<NotificationChannelRecord>?> ListChannelsAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = Open();
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken)) return null;
        var channels = await context.NotificationChannels.AsNoTracking().Where(channel => channel.TenantId == tenantId)
            .OrderBy(channel => channel.NormalizedName).ThenBy(channel => channel.Id).ToListAsync(cancellationToken);
        var records = new List<NotificationChannelRecord>(channels.Count);
        foreach (var channel in channels) records.Add(new(channel, await LastAsync(context, tenantId, channel.Id, cancellationToken)));
        return records;
    }

    public async Task<NotificationChannelRecord?> GetChannelAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken)
    {
        await using var context = Open();
        var channel = await context.NotificationChannels.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == id, cancellationToken);
        return channel is null ? null : new(channel, await LastAsync(context, tenantId, id, cancellationToken));
    }

    private static async Task<LastDeliverySummary?> LastAsync(PlatformDbContext context, TenantId tenantId, Guid channelId, CancellationToken cancellationToken)
    {
        var last = await context.NotificationDeliveries.AsNoTracking().Where(delivery => delivery.TenantId == tenantId && delivery.ChannelId == channelId)
            .OrderByDescending(delivery => delivery.CreatedAt).ThenByDescending(delivery => delivery.Id).FirstOrDefaultAsync(cancellationToken);
        return last is null ? null : new(last.Status, last.SentAt ?? last.CompletedAt ?? last.UpdatedAt, last.ErrorCode);
    }

    public async Task<ChannelMutationResult> CreateChannelAsync(NotificationChannel channel, NotificationActor actor, int maximumChannels, CancellationToken cancellationToken)
    {
        await using var context = Open();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(context, TenantLock(channel.TenantId), cancellationToken);
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == channel.TenantId, cancellationToken)) return new(NotificationOutcome.TenantNotFound);
        if (await context.NotificationChannels.CountAsync(row => row.TenantId == channel.TenantId, cancellationToken) >= maximumChannels) return new(NotificationOutcome.LimitReached);
        context.NotificationChannels.Add(channel);
        AddAudit(context, actor, NotificationAuditActions.ChannelCreated, channel, null);
        if (await SaveAsync(context, cancellationToken) is { } conflict) return new(conflict);
        await transaction.CommitAsync(cancellationToken);
        return new(NotificationOutcome.Changed, new(channel, null));
    }

    public async Task<ChannelMutationResult> MutateChannelAsync(
        TenantId tenantId, Guid id, NotificationActor actor, Func<NotificationChannel, ChannelChange?> mutate, CancellationToken cancellationToken)
    {
        await using var context = Open();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(context, TenantLock(tenantId), cancellationToken);
        var channel = await context.NotificationChannels.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == id, cancellationToken);
        if (channel is null) return new(NotificationOutcome.NotFound);
        var change = mutate(channel);
        if (change?.AuditAction == NotificationAuditActions.ChannelSecurityOwnerChanged &&
            (channel.SecurityOwnerUserId is { } designated && designated != actor.UserId || !await context.TenantMemberships.AnyAsync(
                membership => membership.TenantId == tenantId && membership.UserId == actor.UserId && membership.Role == TenantRole.Owner, cancellationToken)))
            throw new InvalidNotificationException("securityOwnerUserId", "owner_required");
        if (change is null) return new(NotificationOutcome.Unchanged, new(channel, await LastAsync(context, tenantId, id, cancellationToken)));
        AddAudit(context, actor, change.AuditAction, channel, change.Details);
        if (await SaveAsync(context, cancellationToken) is { } conflict) return new(conflict);
        await transaction.CommitAsync(cancellationToken);
        return new(NotificationOutcome.Changed, new(channel, await LastAsync(context, tenantId, id, cancellationToken)), change.ObsoleteSecret);
    }

    public async Task<ChannelMutationResult> DeleteChannelAsync(TenantId tenantId, Guid id, NotificationActor actor, CancellationToken cancellationToken)
    {
        await using var context = Open();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(context, TenantLock(tenantId), cancellationToken);
        var channel = await context.NotificationChannels.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == id, cancellationToken);
        if (channel is null) return new(NotificationOutcome.NotFound);
        context.NotificationChannels.Remove(channel);
        AddAudit(context, actor, NotificationAuditActions.ChannelDeleted, channel, null);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(NotificationOutcome.Changed, null, channel.SecretReference);
    }

    private void AddAudit(PlatformDbContext context, NotificationActor actor, string action, NotificationChannel channel, IReadOnlyDictionary<string, object?>? extra)
    {
        var details = new Dictionary<string, object?> { ["kind"] = channel.Kind.ToString(), ["name"] = channel.Name, ["enabled"] = channel.Enabled, ["authentication"] = channel.Authentication.ToString() };
        if (extra is not null) foreach (var (key, value) in extra) details[key] = value;
        context.AuditEvents.Add(AuditEvent.Record(time.GetUtcNow(), actor.UserId, actor.Kind, action, channel.TenantId, NotificationAuditActions.ChannelTarget, channel.Id.ToString("D"), details));
    }

    /// <summary>Saves; a unique-index violation becomes the matching conflict outcome (any other failure propagates).</summary>
    private static async Task<NotificationOutcome?> SaveAsync(PlatformDbContext context, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: UniqueViolation, ConstraintName: "ux_notification_channels_name" })
        {
            return NotificationOutcome.NameTaken;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: UniqueViolation, ConstraintName: "ux_notification_channels_target" })
        {
            return NotificationOutcome.TargetTaken;
        }
        catch (DbUpdateConcurrencyException)
        {
            return NotificationOutcome.ConcurrentUpdate;
        }
    }

    // ---- policy -------------------------------------------------------------------------------------------------------------------------

    public async Task<NotificationPolicy?> GetPolicyAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = Open();
        return await context.NotificationPolicies.AsNoTracking().SingleOrDefaultAsync(policy => policy.TenantId == tenantId, cancellationToken);
    }

    public async Task<(NotificationPolicy Policy, bool Changed)?> UpdatePolicyAsync(
        TenantId tenantId, NotificationActor actor, Func<NotificationPolicy, bool> apply, CancellationToken cancellationToken)
    {
        await using var context = Open();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(context, TenantLock(tenantId), cancellationToken);
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken)) return null;
        var existing = await context.NotificationPolicies.SingleOrDefaultAsync(policy => policy.TenantId == tenantId, cancellationToken);
        var policy = existing ?? NotificationPolicy.Defaults(tenantId, time.GetUtcNow());
        if (!apply(policy)) return (policy, false);
        if (existing is null) context.NotificationPolicies.Add(policy);
        context.AuditEvents.Add(AuditEvent.Record(time.GetUtcNow(), actor.UserId, actor.Kind, NotificationAuditActions.PolicyUpdated, tenantId, NotificationAuditActions.PolicyTarget,
            policy.Id.ToString("D"), new Dictionary<string, object?>
            {
                ["enabled"] = policy.Enabled, ["eventTypes"] = policy.EventTypes, ["minimumSeverity"] = policy.MinimumSeverity.ToString(),
                ["channelKinds"] = policy.ChannelKinds.Select(kind => kind.ToString()).ToArray(), ["recoveryNotifications"] = policy.RecoveryNotifications,
                ["staleAfterHours"] = policy.StaleAfterHours, ["agentOfflineAfterMinutes"] = policy.AgentOfflineAfterMinutes,
                ["reminderIntervalHours"] = policy.ReminderIntervalHours, ["maxNotificationsPerHour"] = policy.MaxNotificationsPerHour
            }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (policy, true);
    }

    public async Task<IReadOnlyList<NotificationPolicy>> ListEnabledPoliciesAsync(CancellationToken cancellationToken)
    {
        await using var context = Open();
        return await context.NotificationPolicies.AsNoTracking().Where(policy => policy.Enabled).OrderBy(policy => policy.TenantId).ToListAsync(cancellationToken);
    }

    // ---- history ------------------------------------------------------------------------------------------------------------------------

    public async Task<PagedResult<NotificationDeliveryRecord>> ListDeliveriesAsync(TenantId tenantId, DeliveryFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var context = Open();
        var query = from delivery in context.NotificationDeliveries.AsNoTracking()
                    join notificationEvent in context.NotificationEvents.AsNoTracking() on new { delivery.EventId, delivery.TenantId } equals new { EventId = notificationEvent.Id, notificationEvent.TenantId }
                    where delivery.TenantId == tenantId
                    select new { delivery, notificationEvent };
        if (filter.Status is { } status) query = query.Where(row => row.delivery.Status == status);
        if (filter.Kind is { } kind) query = query.Where(row => row.delivery.ChannelKind == kind);
        if (filter.EventType is { } eventType) query = query.Where(row => row.notificationEvent.Type == eventType);
        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderByDescending(row => row.delivery.CreatedAt).ThenByDescending(row => row.delivery.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize).ToListAsync(cancellationToken);
        return new(rows.Select(row => new NotificationDeliveryRecord(row.delivery, row.notificationEvent.Type, row.notificationEvent.Phase, row.notificationEvent.Severity,
            row.notificationEvent.SubjectKind, row.notificationEvent.SubjectName)).ToList(), total, page, pageSize);
    }

    // ---- transports ---------------------------------------------------------------------------------------------------------------------

    public async Task<NotificationTransportSettings?> GetTransportAsync(string key, CancellationToken cancellationToken)
    {
        await using var context = Open();
        return await context.NotificationTransports.AsNoTracking().SingleOrDefaultAsync(settings => settings.Key == key, cancellationToken);
    }

    public async Task<TransportMutationResult> MutateTransportAsync(
        string key, NotificationActor actor, Func<NotificationTransportSettings, TransportChange?> mutate, CancellationToken cancellationToken)
    {
        await using var context = Open();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(context, "notifications:transport:" + key, cancellationToken);
        var existing = await context.NotificationTransports.SingleOrDefaultAsync(settings => settings.Key == key, cancellationToken);
        var settings = existing ?? new NotificationTransportSettings(key, time.GetUtcNow());
        var change = mutate(settings);
        if (change is null) return new(NotificationOutcome.Unchanged, settings);
        if (existing is null) context.NotificationTransports.Add(settings);
        context.AuditEvents.Add(AuditEvent.Record(time.GetUtcNow(), actor.UserId, actor.Kind, change.AuditAction, null, NotificationAuditActions.TransportTarget, key,
            new Dictionary<string, object?> { ["enabled"] = settings.Enabled, ["hasSecret"] = settings.HasSecret }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(NotificationOutcome.Changed, settings, change.ObsoleteSecret);
    }

    // ---- engine -------------------------------------------------------------------------------------------------------------------------

    public async Task<bool> InTenantSessionAsync(TenantId tenantId, Func<INotificationSession, Task> work, CancellationToken cancellationToken)
    {
        await using var context = Open();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(context, TenantLock(tenantId), cancellationToken);
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken)) return false;
        await work(new Session(context, tenantId, time));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Enlists in a lifecycle write's transaction, sharing the same tenant lock and policy/delivery rules as a sweep.</summary>
    internal static async Task RecordOccurrenceAsync(PlatformDbContext context, TenantId tenantId, NotificationObservation observation, NotificationOptions options, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("An occurrence requires an existing transaction.");
        await LockAsync(context, TenantLock(tenantId), cancellationToken);
        await NotificationEngine.ApplyOccurrenceAsync(new Session(context, tenantId, TimeProvider.System), observation, options, now);
    }

    private sealed class Session(PlatformDbContext context, TenantId tenantId, TimeProvider time) : INotificationSession
    {
        public async Task<NotificationPolicy?> GetPolicyAsync() => await context.NotificationPolicies.AsNoTracking().SingleOrDefaultAsync(policy => policy.TenantId == tenantId);

        public async Task<IReadOnlyList<NotificationChannel>> ListChannelsAsync() =>
            await context.NotificationChannels.AsNoTracking().Where(channel => channel.TenantId == tenantId).OrderBy(channel => channel.NormalizedName).ToListAsync();

        public async Task<NotificationChannel?> GetChannelAsync(Guid id) =>
            await context.NotificationChannels.AsNoTracking().SingleOrDefaultAsync(channel => channel.TenantId == tenantId && channel.Id == id);

        public async Task<NotificationCondition?> GetConditionAsync(string key) =>
            await context.NotificationConditions.SingleOrDefaultAsync(condition => condition.TenantId == tenantId && condition.Key == key);

        public async Task<IReadOnlyList<NotificationCondition>> ListActiveConditionsAsync(string type) =>
            await context.NotificationConditions.Where(condition => condition.TenantId == tenantId && condition.IsActive && condition.Type == type).ToListAsync();

        public void AddCondition(NotificationCondition condition) => context.NotificationConditions.Add(condition);

        public Task<bool> EventExistsAsync(string dedupKey) =>
            context.NotificationEvents.AnyAsync(notificationEvent => notificationEvent.TenantId == tenantId && notificationEvent.DedupKey == dedupKey);

        public Task<int> CountEventsSinceAsync(DateTimeOffset since) =>
            context.NotificationEvents.CountAsync(notificationEvent => notificationEvent.TenantId == tenantId && notificationEvent.CreatedAt >= since && notificationEvent.Phase != NotificationPhase.Test);

        public Task<int> CountTestsSinceAsync(Guid? channelId, DateTimeOffset since) =>
            context.NotificationDeliveries.CountAsync(delivery => delivery.TenantId == tenantId && (channelId == null || delivery.ChannelId == channelId) && delivery.CreatedAt >= since
                && context.NotificationEvents.Any(notificationEvent => notificationEvent.Id == delivery.EventId && notificationEvent.TenantId == delivery.TenantId && notificationEvent.Phase == NotificationPhase.Test));

        public void AddEvent(NotificationEvent notificationEvent) => context.NotificationEvents.Add(notificationEvent);

        public void AddDelivery(NotificationDelivery delivery) => context.NotificationDeliveries.Add(delivery);

        public void Audit(NotificationActor actor, string action, string targetType, string targetId, IReadOnlyDictionary<string, object?> details) =>
            context.AuditEvents.Add(AuditEvent.Record(time.GetUtcNow(), actor.UserId, actor.Kind, action, tenantId, targetType, targetId, details));
    }

    // ---- delivery -----------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<DeliveryWork>> ClaimDueAsync(string owner, DateTimeOffset now, int batchSize, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken)
    {
        await using var context = Open();
        // A delivery whose worker died on its last allowed attempt is finished, not retried forever.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notification_deliveries SET "Status" = 'Failed', "ErrorCode" = {NotificationDeliveryCodes.AttemptsExhausted}, "LeaseOwner" = NULL, "LeaseExpiresAt" = NULL,
                   "CompletedAt" = {now}, "UpdatedAt" = {now}
            WHERE "Status" = 'InProgress' AND "LeaseExpiresAt" <= {now} AND "AttemptCount" >= {maxAttempts}
            """, cancellationToken);
        var leaseUntil = now + lease;
        var ids = await context.Database.SqlQuery<Guid>($"""
            UPDATE notification_deliveries d SET "Status" = 'InProgress', "LeaseOwner" = {owner}, "LeaseExpiresAt" = {leaseUntil}, "AttemptCount" = d."AttemptCount" + 1,
                   "LastAttemptAt" = {now}, "NextAttemptAt" = NULL, "UpdatedAt" = {now}
            WHERE d."Id" IN (
                SELECT "Id" FROM notification_deliveries
                WHERE ("Status" = 'Pending' AND "NextAttemptAt" <= {now}) OR ("Status" = 'InProgress' AND "LeaseExpiresAt" <= {now})
                ORDER BY COALESCE("NextAttemptAt", "LeaseExpiresAt"), "Id"
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING d."Id" AS "Value"
            """).ToListAsync(cancellationToken);
        if (ids.Count == 0) return [];
        var deliveries = await context.NotificationDeliveries.AsNoTracking().Where(delivery => ids.Contains(delivery.Id)).ToListAsync(cancellationToken);
        var eventIds = deliveries.Select(delivery => delivery.EventId).Distinct().ToList();
        var channelIds = deliveries.Select(delivery => delivery.ChannelId).Distinct().ToList();
        var tenantIds = deliveries.Select(delivery => delivery.TenantId).Distinct().ToList();
        var events = await context.NotificationEvents.AsNoTracking().Where(notificationEvent => eventIds.Contains(notificationEvent.Id)).ToDictionaryAsync(notificationEvent => notificationEvent.Id, cancellationToken);
        var channels = await context.NotificationChannels.AsNoTracking().Where(channel => channelIds.Contains(channel.Id)).ToDictionaryAsync(channel => channel.Id, cancellationToken);
        var policies = await context.NotificationPolicies.AsNoTracking().Where(policy => tenantIds.Contains(policy.TenantId)).ToDictionaryAsync(policy => policy.TenantId, cancellationToken);
        var securityTenants = events.Values.Where(e => e.Type == NotificationEventTypes.BreakGlassAccess).Select(e => e.TenantId).Distinct().ToList();
        var securityOwners = securityTenants.Count == 0 ? [] : await context.TenantMemberships.AsNoTracking()
            .Where(membership => securityTenants.Contains(membership.TenantId) && membership.Role == TenantRole.Owner).ToListAsync(cancellationToken);
        return deliveries.Select(delivery =>
        {
            var e = events[delivery.EventId];
            channels.TryGetValue(delivery.ChannelId, out var channel);
            policies.TryGetValue(delivery.TenantId, out var policy);
            // A channel or policy switched off after the fan-out stops what has not been sent yet; a test goes out whatever the policy says.
            var securityNotice = e.Type == NotificationEventTypes.BreakGlassAccess;
            var policyAllows = securityNotice || e.Phase == NotificationPhase.Test || policy is { Enabled: true } && policy.ChannelKinds.Contains(delivery.ChannelKind);
            DeliverySkip? skip = channel is null || channel.TenantId != delivery.TenantId ? new(NotificationDeliveryStatus.Cancelled, NotificationDeliveryCodes.ChannelRemoved)
                : !channel.Enabled ? new(NotificationDeliveryStatus.Suppressed, NotificationDeliveryCodes.ChannelDisabled)
                : channel.AwaitsConfirmation && e.Type != NotificationEventTypes.RecipientVerification ? new(NotificationDeliveryStatus.Suppressed, NotificationDeliveryCodes.RecipientUnverified)
                : securityNotice && (channel.Kind != NotificationChannelKind.Email || channel.SecurityOwnerUserId is not { } designated ||
                    !securityOwners.Any(membership => membership.TenantId == delivery.TenantId && membership.UserId == designated))
                    ? new(NotificationDeliveryStatus.Cancelled, NotificationDeliveryCodes.OwnerRecipientUnavailable)
                : !policyAllows ? new(NotificationDeliveryStatus.Cancelled, NotificationDeliveryCodes.PolicyDisabled) : null;
            return new DeliveryWork(delivery.Id, delivery.TenantId, delivery.AttemptCount, delivery.ChannelKind, delivery.ChannelId, skip is null ? channel!.Target : null,
                channel?.Authentication ?? WebhookAuthentication.None, skip is null ? channel!.SecretReference : null,
                new NotificationEventSnapshot(e.Id, e.Type, e.Phase, e.Severity, e.SubjectKind, e.SubjectId, e.SubjectName, e.Facts, e.OccurredAt), skip);
        }).ToList();
    }

    public async Task<bool> CompleteAsync(Guid deliveryId, string owner, int attempt, DeliveryResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = Open();
        var terminal = result.Status != NotificationDeliveryStatus.Pending;
        DateTimeOffset? next = terminal ? null : result.NextAttemptAt ?? now;
        DateTimeOffset? sentAt = result.Status == NotificationDeliveryStatus.Sent ? now : null;
        DateTimeOffset? completedAt = terminal ? now : null;
        var rows = await context.NotificationDeliveries
            .Where(delivery => delivery.Id == deliveryId && delivery.Status == NotificationDeliveryStatus.InProgress && delivery.LeaseOwner == owner && delivery.AttemptCount == attempt)
            .ExecuteUpdateAsync(set => set
                .SetProperty(delivery => delivery.Status, result.Status)
                .SetProperty(delivery => delivery.ErrorCode, result.Code)
                .SetProperty(delivery => delivery.NextAttemptAt, next)
                .SetProperty(delivery => delivery.SentAt, sentAt)
                .SetProperty(delivery => delivery.CompletedAt, completedAt)
                .SetProperty(delivery => delivery.LeaseOwner, (string?)null)
                .SetProperty(delivery => delivery.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(delivery => delivery.UpdatedAt, now), cancellationToken);
        return rows == 1;
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        await using var context = Open();
        // Events with a delivery still in flight are kept whatever their age; deliveries go with their event (cascade).
        var removed = await context.NotificationEvents
            .Where(notificationEvent => notificationEvent.CreatedAt < olderThan
                && !context.NotificationDeliveries.Any(delivery => delivery.EventId == notificationEvent.Id && delivery.TenantId == notificationEvent.TenantId
                    && (delivery.Status == NotificationDeliveryStatus.Pending || delivery.Status == NotificationDeliveryStatus.InProgress)))
            .ExecuteDeleteAsync(cancellationToken);
        await context.NotificationConditions.Where(condition => !condition.IsActive && condition.ResolvedAt < olderThan).ExecuteDeleteAsync(cancellationToken);
        return removed;
    }
}
