using BackupPlatform.Application.Support;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Support;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Support;

/// <summary>Read side of support access, under explicit platform operations. Nothing here authorizes: the callers' policies and the support gate do.</summary>
public sealed class PostgreSqlSupportQueryStore(DbContextOptions<PlatformDbContext> options) : ISupportQueryStore
{
    private static readonly string[] SummaryActions =
    [
        SupportAuditActions.ArtifactViewed, SupportAuditActions.DownloadStarted, SupportAuditActions.DownloadCompleted, SupportAuditActions.DownloadFailed,
        SupportAuditActions.RestoreRequested, SupportAuditActions.RestoreCancelRequested
    ];

    public async Task<SupportSessionRow?> GetAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var session = await context.SupportAccessSessions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == sessionId, cancellationToken);
        return session is null ? null : (await RowsAsync(context, [session], cancellationToken)).Single();
    }

    public async Task<SupportPage<SupportSessionRow>> ListAsync(SupportSessionFilter filter, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var sessions = context.SupportAccessSessions.AsNoTracking().AsQueryable();
        if (filter.TenantId is { } tenantId) sessions = sessions.Where(session => session.TenantId == tenantId);
        if (filter.Admin is { } admin) sessions = sessions.Where(session => session.PlatformAdminId == admin);
        if (filter.Status is { } status)
        {
            // The status the clock sees: an Active row past its expiry is Expired even before the worker records it.
            sessions = status switch
            {
                SupportSessionStatus.Active => sessions.Where(session => session.Status == SupportSessionStatus.Active && session.ExpiresAt > now),
                SupportSessionStatus.Expired => sessions.Where(session => session.Status == SupportSessionStatus.Expired || (session.Status == SupportSessionStatus.Active && session.ExpiresAt <= now)),
                _ => sessions.Where(session => session.Status == status)
            };
        }
        var total = await sessions.LongCountAsync(cancellationToken);
        var page = await sessions.OrderByDescending(session => session.StartedAt).ThenBy(session => session.Id)
            .Skip(checked((filter.Page - 1) * filter.PageSize)).Take(filter.PageSize).ToListAsync(cancellationToken);
        return new SupportPage<SupportSessionRow>(await RowsAsync(context, page, cancellationToken), total, filter.Page, filter.PageSize);
    }

    public async Task<SupportPage<SupportAuditRow>> ListAuditAsync(Guid sessionId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await AuditPageAsync(context, context.AuditEvents.AsNoTracking().Where(auditEvent => auditEvent.SupportSessionId == sessionId), page, pageSize, cancellationToken);
    }

    public async Task<SupportPage<DownloadSession>> ListDownloadsAsync(TenantId tenantId, Guid sessionId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var downloads = context.DownloadSessions.AsNoTracking().Where(session => session.TenantId == tenantId && session.SupportSessionId == sessionId);
        var total = await downloads.LongCountAsync(cancellationToken);
        var rows = await downloads.OrderByDescending(session => session.CreatedAt).ThenBy(session => session.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize).ToListAsync(cancellationToken);
        return new SupportPage<DownloadSession>(rows, total, page, pageSize);
    }

    public async Task<SupportPage<SupportTenantSessionRow>> ListTenantSessionsAsync(TenantId tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var sessions = context.SupportAccessSessions.AsNoTracking().Where(session => session.TenantId == tenantId);
        var total = await sessions.LongCountAsync(cancellationToken);
        var rows = await sessions.OrderByDescending(session => session.StartedAt).ThenBy(session => session.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize).ToListAsync(cancellationToken);
        return new SupportPage<SupportTenantSessionRow>(await TenantRowsAsync(context, tenantId, rows, cancellationToken), total, page, pageSize);
    }

    public async Task<SupportTenantSessionRow?> GetTenantSessionAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var session = await context.SupportAccessSessions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == sessionId && row.TenantId == tenantId, cancellationToken);
        return session is null ? null : (await TenantRowsAsync(context, tenantId, [session], cancellationToken)).Single();
    }

    public async Task<SupportPage<SupportAuditRow>?> ListTenantActivityAsync(TenantId tenantId, Guid sessionId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (!await context.SupportAccessSessions.AnyAsync(row => row.Id == sessionId && row.TenantId == tenantId, cancellationToken)) return null;
        return await AuditPageAsync(context, context.AuditEvents.AsNoTracking().Where(auditEvent => auditEvent.SupportSessionId == sessionId && auditEvent.TenantId == tenantId),
            page, pageSize, cancellationToken);
    }

    private static async Task<SupportPage<SupportAuditRow>> AuditPageAsync(PlatformDbContext context, IQueryable<Domain.Audit.AuditEvent> events, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        var total = await events.LongCountAsync(cancellationToken);
        var rows = await events.OrderByDescending(auditEvent => auditEvent.At).ThenBy(auditEvent => auditEvent.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize).ToListAsync(cancellationToken);
        var actorIds = rows.Select(row => row.ActorUserId).Distinct().ToArray();
        var names = actorIds.Length == 0 ? [] : await context.Users.AsNoTracking().Where(user => actorIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken);
        return new SupportPage<SupportAuditRow>(
            [.. rows.Select(row => new SupportAuditRow(row.Id, row.At, row.Action, row.ActorUserId.Value, names.GetValueOrDefault(row.ActorUserId), row.TargetType, row.TargetId, row.DetailsJson))],
            total, page, pageSize);
    }

    private static async Task<IReadOnlyList<SupportSessionRow>> RowsAsync(PlatformDbContext context, IReadOnlyList<SupportAccessSession> sessions, CancellationToken cancellationToken)
    {
        if (sessions.Count == 0) return [];
        var tenantIds = sessions.Select(session => session.TenantId).Distinct().ToArray();
        var userIds = sessions.Select(session => session.PlatformAdminId).Concat(sessions.Where(session => session.ClosedBy is not null).Select(session => session.ClosedBy!)).Distinct().ToArray();
        var tenants = await context.Tenants.AsNoTracking().Where(tenant => tenantIds.Contains(tenant.Id)).ToDictionaryAsync(tenant => tenant.Id, tenant => tenant.Name, cancellationToken);
        var users = await context.Users.AsNoTracking().Where(user => userIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken);
        return [.. sessions.Select(session => new SupportSessionRow(session, tenants.GetValueOrDefault(session.TenantId), users.GetValueOrDefault(session.PlatformAdminId),
            session.ClosedBy is null ? null : users.GetValueOrDefault(session.ClosedBy)))];
    }

    private static async Task<IReadOnlyList<SupportTenantSessionRow>> TenantRowsAsync(PlatformDbContext context, TenantId tenantId, IReadOnlyList<SupportAccessSession> sessions,
        CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(context, sessions, cancellationToken);
        if (rows.Count == 0) return [];
        var ids = sessions.Select(session => (Guid?)session.Id).ToArray();
        var counts = await context.AuditEvents.AsNoTracking()
            .Where(auditEvent => auditEvent.TenantId == tenantId && ids.Contains(auditEvent.SupportSessionId) && SummaryActions.Contains(auditEvent.Action))
            .GroupBy(auditEvent => new { auditEvent.SupportSessionId, auditEvent.Action, auditEvent.TargetType })
            .Select(group => new { group.Key.SupportSessionId, group.Key.Action, group.Key.TargetType, Count = group.Count(), Distinct = group.Select(auditEvent => auditEvent.TargetId).Distinct().Count() })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row =>
        {
            var mine = counts.Where(count => count.SupportSessionId == row.Session.Id).ToList();
            int Of(string action, bool distinct = false) => mine.Where(count => count.Action == action).Sum(count => distinct ? count.Distinct : count.Count);
            return new SupportTenantSessionRow(row, new SupportActivitySummary(
                Of(SupportAuditActions.ArtifactViewed, distinct: true), Of(SupportAuditActions.DownloadStarted), Of(SupportAuditActions.DownloadCompleted), Of(SupportAuditActions.DownloadFailed),
                Of(SupportAuditActions.RestoreRequested), Of(SupportAuditActions.RestoreCancelRequested)));
        })];
    }
}
