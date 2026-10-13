using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.Infrastructure.Platform;

/// <summary>
/// Cross-tenant administration under explicit platform operations. Tenant isolation is bypassed through
/// <see cref="PlatformDbContext.CreateForPlatformOperations"/> only; the PlatformAdmin-only endpoints are the
/// authorization boundary. Every write adds its <see cref="AuditEvent"/> to the same SaveChanges as the change itself.
/// </summary>
public sealed class PostgreSqlPlatformAdministrationStore(DbContextOptions<PlatformDbContext> options) : IPlatformAdministrationStore
{
    public async Task<PlatformCounts> GetCountsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var since = now.AddHours(-24);
        return new PlatformCounts(
            await context.Tenants.LongCountAsync(cancellationToken),
            await context.Users.LongCountAsync(cancellationToken),
            await context.BackupJobs.LongCountAsync(job => job.Enabled, cancellationToken),
            await context.BackupRuns.LongCountAsync(run => run.CompletedAt >= since && (run.Status == BackupStatus.Failed || run.Status == BackupStatus.AgentLost), cancellationToken));
    }

    public async Task<PagedResult<PlatformTenantItem>> ListTenantsAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var tenants = context.Tenants.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            tenants = tenants.Where(tenant => tenant.Name.ToLower().Contains(term));
        }
        var total = await tenants.LongCountAsync(cancellationToken);
        var items = await TenantItems(context, tenants.OrderBy(tenant => tenant.Name).ThenBy(tenant => tenant.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize)).ToListAsync(cancellationToken);
        return new PagedResult<PlatformTenantItem>(items, total, page, pageSize);
    }

    public async Task<PlatformTenantDetail?> GetTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(row => row.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return null;
        }
        var policy = await context.TenantAgentEnrollmentPolicies.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId, cancellationToken);
        return new PlatformTenantDetail(
            tenant.Id, tenant.Name,
            await context.TenantMemberships.CountAsync(membership => membership.TenantId == tenantId, cancellationToken),
            await context.BackupJobs.CountAsync(job => job.TenantId == tenantId && job.Enabled, cancellationToken),
            policy?.IsEnabled ?? false, policy?.ChangedAt);
    }

    public async Task<PlatformTenantItem> CreateTenantAsync(string name, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var tenant = new Tenant(TenantId.New(), name);
        context.Tenants.Add(tenant);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, AuditActions.TenantCreated, tenant.Id,
            "tenant", Id(tenant.Id), new Dictionary<string, object?> { ["name"] = tenant.Name }));
        await context.SaveChangesAsync(cancellationToken);
        return new PlatformTenantItem(tenant.Id, tenant.Name, 0, 0, false);
    }

    public async Task<PlatformChange<PlatformTenantItem>> RenameTenantAsync(
        TenantId tenantId, string name, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var tenant = await context.Tenants.SingleOrDefaultAsync(row => row.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return new(PlatformChangeOutcome.TenantNotFound);
        }
        var previous = tenant.Name;
        tenant.Rename(name);
        var outcome = PlatformChangeOutcome.Unchanged;
        if (!string.Equals(previous, tenant.Name, StringComparison.Ordinal))
        {
            context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, AuditActions.TenantRenamed, tenantId,
                "tenant", Id(tenantId), new Dictionary<string, object?> { ["previousName"] = previous, ["name"] = tenant.Name }));
            await context.SaveChangesAsync(cancellationToken);
            outcome = PlatformChangeOutcome.Changed;
        }
        var item = await TenantItems(context, context.Tenants.AsNoTracking().Where(row => row.Id == tenantId)).SingleAsync(cancellationToken);
        return new(outcome, item);
    }

    public async Task<PagedResult<PlatformUserItem>> ListUsersAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var users = from user in context.Users.AsNoTracking()
                    join credential in context.PasswordCredentials.AsNoTracking() on user.Id equals credential.UserId into joined
                    from credential in joined.DefaultIfEmpty()
                    select new { user.Id, user.DisplayName, Login = credential == null ? null : credential.LoginIdentifier };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            users = users.Where(row => row.DisplayName.ToLower().Contains(term) || (row.Login != null && row.Login.ToLower().Contains(term)));
        }
        var total = await users.LongCountAsync(cancellationToken);
        var rows = await users.OrderBy(row => row.DisplayName).ThenBy(row => row.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize)
            .Select(row => new
            {
                row.Id, row.DisplayName, row.Login,
                IsPlatformAdmin = context.PlatformAdmins.Any(admin => admin.UserId == row.Id),
                MembershipCount = context.TenantMemberships.Count(membership => membership.UserId == row.Id)
            }).ToListAsync(cancellationToken);
        return new PagedResult<PlatformUserItem>(
            rows.Select(row => new PlatformUserItem(row.Id, row.DisplayName, row.Login, row.IsPlatformAdmin, row.MembershipCount)).ToList(),
            total, page, pageSize);
    }

    public async Task<PlatformChange<PlatformUserItem>> CreateUserAsync(
        UserId userId, string displayName, string loginIdentifier, string passwordHash, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var credential = new PasswordCredential(userId, loginIdentifier, passwordHash);
        if (await context.PasswordCredentials.AnyAsync(row => row.NormalizedLoginIdentifier == credential.NormalizedLoginIdentifier, cancellationToken))
        {
            return new(PlatformChangeOutcome.LoginTaken);
        }
        var user = new User(userId, displayName);
        context.Users.Add(user);
        context.PasswordCredentials.Add(credential);
        // The audit event never carries the password or its hash.
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, AuditActions.UserCreated, null,
            "user", Id(userId), new Dictionary<string, object?> { ["displayName"] = user.DisplayName, ["loginIdentifier"] = credential.LoginIdentifier }));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new(PlatformChangeOutcome.LoginTaken);
        }
        return new(PlatformChangeOutcome.Changed, new PlatformUserItem(userId, user.DisplayName, credential.LoginIdentifier, false, 0));
    }

    public async Task<PagedResult<PlatformMembershipItem>?> ListMembershipsAsync(
        TenantId tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
        {
            return null;
        }
        var query = MembershipItems(context, tenantId);
        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.DisplayName).ThenBy(row => row.UserId)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<PlatformMembershipItem>(rows.Select(ToItem).ToList(), total, page, pageSize);
    }

    public async Task<PlatformChange<PlatformMembershipItem>> AddMembershipAsync(
        TenantId tenantId, UserId userId, TenantRole role, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
        {
            return new(PlatformChangeOutcome.TenantNotFound);
        }
        if (!await context.Users.AnyAsync(user => user.Id == userId, cancellationToken))
        {
            return new(PlatformChangeOutcome.UserNotFound);
        }
        if (await context.TenantMemberships.AnyAsync(membership => membership.TenantId == tenantId && membership.UserId == userId, cancellationToken))
        {
            return new(PlatformChangeOutcome.AlreadyMember);
        }
        context.TenantMemberships.Add(new TenantMembership(tenantId, userId, role));
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, AuditActions.MembershipAdded, tenantId,
            "membership", Id(userId), new Dictionary<string, object?> { ["userId"] = Id(userId), ["role"] = role.ToString() }));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new(PlatformChangeOutcome.AlreadyMember);
        }
        return new(PlatformChangeOutcome.Changed, await ReadMembershipAsync(context, tenantId, userId, cancellationToken));
    }

    public async Task<PlatformChange<PlatformMembershipItem>> ChangeMembershipRoleAsync(
        TenantId tenantId, UserId userId, TenantRole role, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var membership = await context.TenantMemberships.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.UserId == userId, cancellationToken);
        if (membership is null)
        {
            return new(PlatformChangeOutcome.MembershipNotFound);
        }
        var previous = membership.Role;
        var outcome = PlatformChangeOutcome.Unchanged;
        if (previous != role)
        {
            membership.ChangeRole(role);
            context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, AuditActions.MembershipRoleChanged, tenantId,
                "membership", Id(userId), new Dictionary<string, object?> { ["userId"] = Id(userId), ["previousRole"] = previous.ToString(), ["role"] = role.ToString() }));
            await context.SaveChangesAsync(cancellationToken);
            outcome = PlatformChangeOutcome.Changed;
        }
        return new(outcome, await ReadMembershipAsync(context, tenantId, userId, cancellationToken));
    }

    public async Task<PlatformChange<PlatformMembershipItem>> RemoveMembershipAsync(
        TenantId tenantId, UserId userId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var membership = await context.TenantMemberships.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.UserId == userId, cancellationToken);
        if (membership is null)
        {
            return new(PlatformChangeOutcome.MembershipNotFound);
        }
        var item = await ReadMembershipAsync(context, tenantId, userId, cancellationToken);
        context.TenantMemberships.Remove(membership);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, AuditActions.MembershipRemoved, tenantId,
            "membership", Id(userId), new Dictionary<string, object?> { ["userId"] = Id(userId), ["role"] = membership.Role.ToString() }));
        await context.SaveChangesAsync(cancellationToken);
        return new(PlatformChangeOutcome.Changed, item);
    }

    public async Task<PagedResult<PlatformAuditItem>> ListAuditAsync(TenantId? tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var events = context.AuditEvents.AsNoTracking().AsQueryable();
        if (tenantId is not null)
        {
            events = events.Where(auditEvent => auditEvent.TenantId == tenantId);
        }
        var total = await events.LongCountAsync(cancellationToken);
        var rows = await events.OrderByDescending(auditEvent => auditEvent.At).ThenBy(auditEvent => auditEvent.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize).ToListAsync(cancellationToken);
        var actorIds = rows.Select(row => row.ActorUserId).Distinct().ToArray();
        var tenantIds = rows.Where(row => row.TenantId is not null).Select(row => row.TenantId!).Distinct().ToArray();
        var actors = actorIds.Length == 0 ? [] : await context.Users.AsNoTracking().Where(user => actorIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken);
        var tenants = tenantIds.Length == 0 ? [] : await context.Tenants.AsNoTracking().Where(tenant => tenantIds.Contains(tenant.Id))
            .ToDictionaryAsync(tenant => tenant.Id, tenant => tenant.Name, cancellationToken);
        return new PagedResult<PlatformAuditItem>(
            rows.Select(row => new PlatformAuditItem(
                row.Id, row.At, row.ActorUserId, row.ActorKind switch
                {
                    AuditActorKind.System => "System",
                    AuditActorKind.Anonymous => "Anonymous",
                    _ => actors.GetValueOrDefault(row.ActorUserId)
                }, row.ActorKind, row.Action,
                row.TenantId, row.TenantId is null ? null : tenants.GetValueOrDefault(row.TenantId), row.TargetType, row.TargetId, row.DetailsJson)).ToList(),
            total, page, pageSize);
    }

    private static string Id(TenantId id) => id.Value.ToString("D");

    private static string Id(UserId id) => id.Value.ToString("D");

    private static IQueryable<PlatformTenantItem> TenantItems(PlatformDbContext context, IQueryable<Tenant> tenants) =>
        tenants.Select(tenant => new PlatformTenantItem(
            tenant.Id, tenant.Name,
            context.TenantMemberships.Count(membership => membership.TenantId == tenant.Id),
            context.Agents.Count(agent => agent.TenantId == tenant.Id),
            context.TenantAgentEnrollmentPolicies.Any(policy => policy.TenantId == tenant.Id && policy.IsEnabled)));

    private sealed class MembershipRow
    {
        public required UserId UserId { get; init; }
        public required string DisplayName { get; init; }
        public string? Login { get; init; }
        public required TenantRole Role { get; init; }
    }

    private static IQueryable<MembershipRow> MembershipItems(PlatformDbContext context, TenantId tenantId, UserId? userId = null) =>
        from membership in context.TenantMemberships.AsNoTracking()
        where membership.TenantId == tenantId && (userId == null || membership.UserId == userId)
        join user in context.Users.AsNoTracking() on membership.UserId equals user.Id
        join credential in context.PasswordCredentials.AsNoTracking() on user.Id equals credential.UserId into joined
        from credential in joined.DefaultIfEmpty()
        select new MembershipRow { UserId = user.Id, DisplayName = user.DisplayName, Login = credential == null ? null : credential.LoginIdentifier, Role = membership.Role };

    private static PlatformMembershipItem ToItem(MembershipRow row) => new(row.UserId, row.DisplayName, row.Login, row.Role);

    private static async Task<PlatformMembershipItem> ReadMembershipAsync(
        PlatformDbContext context, TenantId tenantId, UserId userId, CancellationToken cancellationToken) =>
        ToItem(await MembershipItems(context, tenantId, userId).SingleAsync(cancellationToken));
}
