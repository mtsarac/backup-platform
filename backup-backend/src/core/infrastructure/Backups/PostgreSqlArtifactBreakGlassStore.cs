using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Notifications;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>
/// Break-glass writes under explicit platform operations; the endpoints' PlatformAdmin policy plus the domain's dual-control rules are the authorization. Every write
/// adds its audit event (ids and the stated reason, never key material) to the same SaveChanges; the request row is locked for every decision so two admins
/// cannot decide it at once, and using it (see <c>PostgreSqlDownloadStore.CreateAsync</c>) takes the same lock.
/// </summary>
public sealed class PostgreSqlArtifactBreakGlassStore(DbContextOptions<PlatformDbContext> options) : IArtifactBreakGlassStore
{
    public async Task<BreakGlassOpenResult> OpenAsync(BreakGlassOpenCommand command, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        // The tenant is part of the lookup: another tenant's artifact is indistinguishable from none.
        var artifact = await context.BackupArtifacts.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == command.TenantId && row.Id == command.ArtifactId, cancellationToken);
        if (artifact is null) return new(BreakGlassOpenOutcome.ArtifactNotFound);
        if (artifact.Format != BackupArtifact.BpeFormat) return new(BreakGlassOpenOutcome.NotEncrypted);
        if (!await context.TenantWrapKeys.AsNoTracking().AnyAsync(key => key.TenantId == command.TenantId && key.KeyId == artifact.EncryptionKeyId && key.State != TenantWrapKeyState.Retired, cancellationToken))
            return new(BreakGlassOpenOutcome.KeyUnavailable);
        var request = ArtifactBreakGlassRequest.Open(command.RequestId, command.TenantId, command.ArtifactId, command.Requester, command.Reason, command.Now, command.Ttl);
        context.ArtifactBreakGlassRequests.Add(request);
        context.AuditEvents.Add(Audit(AuditActions.BreakGlassRequested, request, command.Requester, command.Now, new Dictionary<string, object?>
        {
            ["reason"] = request.Reason, ["expiresAt"] = request.ExpiresAt, ["keyId"] = artifact.EncryptionKeyId
        }));
        await context.SaveChangesAsync(cancellationToken);
        return new(BreakGlassOpenOutcome.Opened, request);
    }

    public Task<BreakGlassDecisionResult> ApproveAsync(Guid requestId, UserId by, DateTimeOffset now, CancellationToken cancellationToken) =>
        DecideAsync(requestId, by, now, "approve", (request, at) => request.Approve(by, at), AuditActions.BreakGlassApproved, cancellationToken);

    public Task<BreakGlassDecisionResult> DenyAsync(Guid requestId, UserId by, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        DecideAsync(requestId, by, now, "deny", (request, at) => request.Deny(by, reason, at), AuditActions.BreakGlassDenied, cancellationToken);

    private async Task<BreakGlassDecisionResult> DecideAsync(Guid requestId, UserId by, DateTimeOffset now, string attempted,
        Func<ArtifactBreakGlassRequest, DateTimeOffset, BreakGlassDenial?> decide, string action, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var request = await LockAsync(context, requestId, cancellationToken);
        if (request is null) return new(BreakGlassDecisionOutcome.NotFound);
        var denial = decide(request, now);
        if (denial is { } refused)
        {
            // A lapsed window is recorded as such, and the refusal itself is part of the trail (a self-approval attempt is exactly what an auditor looks for).
            if (request.Expire(now)) context.AuditEvents.Add(Audit(AuditActions.BreakGlassExpired, request, UserId.System, now, null, AuditActorKind.System));
            context.AuditEvents.Add(Audit(AuditActions.BreakGlassRefused, request, by, now, new Dictionary<string, object?>
            {
                ["attempted"] = attempted, ["result"] = "refused", ["reason"] = BreakGlassDenials.Code(refused)
            }));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(BreakGlassDecisionOutcome.Refused, refused, request);
        }
        context.AuditEvents.Add(Audit(action, request, by, now, new Dictionary<string, object?>
        {
            ["requestedBy"] = request.RequestedBy.Value, ["decidedBy"] = by.Value, ["reason"] = request.DecisionReason, ["result"] = request.Status.ToString().ToLowerInvariant()
        }));
        if (request.Status == BreakGlassStatus.Approved) await QueueOwnerNoticeAsync(context, request, by, now, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(BreakGlassDecisionOutcome.Decided, null, request);
    }

    public async Task<BreakGlassRow?> GetAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var request = await context.ArtifactBreakGlassRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == requestId, cancellationToken);
        return request is null ? null : (await RowsAsync(context, [request], cancellationToken)).Single();
    }

    public async Task<BreakGlassPage<BreakGlassRow>> ListAsync(BreakGlassFilter filter, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var requests = context.ArtifactBreakGlassRequests.AsNoTracking().AsQueryable();
        if (filter.TenantId is { } tenantId) requests = requests.Where(request => request.TenantId == tenantId);
        if (filter.Requester is { } requester) requests = requests.Where(request => request.RequestedBy == requester);
        if (filter.Status is { } status)
        {
            // The status the clock sees: a Pending or Approved row past its expiry is Expired even before the worker records it.
            var now = filter.Now;
            requests = status switch
            {
                BreakGlassStatus.Pending or BreakGlassStatus.Approved => requests.Where(request => request.Status == status && request.ExpiresAt > now),
                BreakGlassStatus.Expired => requests.Where(request => request.Status == BreakGlassStatus.Expired ||
                    (request.Status == BreakGlassStatus.Pending || request.Status == BreakGlassStatus.Approved) && request.ExpiresAt <= now),
                _ => requests.Where(request => request.Status == status)
            };
        }
        var total = await requests.LongCountAsync(cancellationToken);
        var page = await requests.OrderByDescending(request => request.CreatedAt).ThenBy(request => request.Id)
            .Skip(checked((filter.Page - 1) * filter.PageSize)).Take(filter.PageSize).ToListAsync(cancellationToken);
        return new BreakGlassPage<BreakGlassRow>(await RowsAsync(context, page, cancellationToken), total, filter.Page, filter.PageSize);
    }

    public async Task<int> ExpireDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var ids = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM artifact_break_glass_requests WHERE "Status" IN ('Pending','Approved') AND "ExpiresAt" <= {now}
            ORDER BY "ExpiresAt", "Id" LIMIT {limit} FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
        if (ids.Count == 0) return 0;
        var requests = await context.ArtifactBreakGlassRequests.Where(request => ids.Contains(request.Id)).ToListAsync(cancellationToken);
        var expired = 0;
        foreach (var request in requests)
        {
            if (!request.Expire(now)) continue;
            context.AuditEvents.Add(Audit(AuditActions.BreakGlassExpired, request, UserId.System, now, null, AuditActorKind.System));
            expired++;
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return expired;
    }

    /// <summary>Takes the row lock every decision and the single use need, and returns the tracked request; null when unknown.</summary>
    internal static async Task<ArtifactBreakGlassRequest?> LockAsync(PlatformDbContext context, Guid requestId, CancellationToken cancellationToken)
    {
        var locked = await context.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM artifact_break_glass_requests WHERE \"Id\" = {requestId} FOR UPDATE").ToListAsync(cancellationToken);
        return locked.Count == 0 ? null : await context.ArtifactBreakGlassRequests.SingleAsync(row => row.Id == requestId, cancellationToken);
    }

    internal static AuditEvent Audit(string action, ArtifactBreakGlassRequest request, UserId actor, DateTimeOffset at, Dictionary<string, object?>? details,
        AuditActorKind kind = AuditActorKind.PlatformAdmin)
    {
        details ??= [];
        details["requestId"] = request.Id;
        details["artifactId"] = request.ArtifactId;
        return AuditEvent.Record(at, actor, kind, action, request.TenantId, "backup_artifact", request.ArtifactId.ToString("D"), details);
    }
    private static async Task QueueOwnerNoticeAsync(PlatformDbContext context, ArtifactBreakGlassRequest request, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var scope = "notifications:" + request.TenantId.Value.ToString("D");
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({scope}, 0))", cancellationToken);
        var owners = await context.TenantMemberships.AsNoTracking().Where(membership => membership.TenantId == request.TenantId && membership.Role == TenantRole.Owner)
            .Select(membership => membership.UserId).ToListAsync(cancellationToken);
        var channels = await context.NotificationChannels.AsNoTracking().Where(channel => channel.TenantId == request.TenantId &&
            channel.Kind == NotificationChannelKind.Email && channel.Enabled && channel.RecipientVerifiedAt != null &&
            channel.SecurityOwnerUserId != null && owners.Contains(channel.SecurityOwnerUserId)).ToListAsync(cancellationToken);
        var notice = new NotificationEvent(Guid.NewGuid(), request.TenantId, NotificationEventTypes.BreakGlassAccess, NotificationPhase.Raised, NotificationSeverity.Critical,
            new NotificationSubject("backup_artifact", request.ArtifactId, $"Backup artifact {request.ArtifactId:N}"), null,
            $"{NotificationEventTypes.BreakGlassAccess}:{request.Id:N}", now, now);
        context.NotificationEvents.Add(notice);
        foreach (var channel in channels)
            context.NotificationDeliveries.Add(new NotificationDelivery(Guid.NewGuid(), request.TenantId, notice.Id, channel.Id, channel.Kind, channel.Name,
                NotificationTargets.Display(channel.Kind, channel.Target), now));
        context.AuditEvents.Add(Audit(channels.Count == 0 ? AuditActions.BreakGlassOwnerNotificationUnavailable : AuditActions.BreakGlassOwnerNotificationQueued,
            request, actor, now, new Dictionary<string, object?>
            {
                ["notificationEventId"] = notice.Id, ["deliveryCount"] = channels.Count, ["result"] = channels.Count == 0 ? "no_deliverable_owner_channel" : "queued"
            }));
    }


    private static async Task<IReadOnlyList<BreakGlassRow>> RowsAsync(PlatformDbContext context, IReadOnlyList<ArtifactBreakGlassRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count == 0) return [];
        var tenantIds = requests.Select(request => request.TenantId).Distinct().ToArray();
        var userIds = requests.Select(request => request.RequestedBy).Concat(requests.Where(request => request.DecidedBy is not null).Select(request => request.DecidedBy!)).Distinct().ToArray();
        var tenants = await context.Tenants.AsNoTracking().Where(tenant => tenantIds.Contains(tenant.Id)).ToDictionaryAsync(tenant => tenant.Id, tenant => tenant.Name, cancellationToken);
        var users = await context.Users.AsNoTracking().Where(user => userIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken);
        return [.. requests.Select(request => new BreakGlassRow(request, tenants.GetValueOrDefault(request.TenantId), users.GetValueOrDefault(request.RequestedBy),
            request.DecidedBy is null ? null : users.GetValueOrDefault(request.DecidedBy)))];
    }
}
