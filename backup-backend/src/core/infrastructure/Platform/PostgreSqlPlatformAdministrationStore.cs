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
                    select new { user.Id, user.DisplayName, user.Role, user.IsManuallyDisabled, user.IsSecurityDisabled, user.IsNoMembershipDisabled, user.ManualDisableReason, Login = credential == null ? null : credential.LoginIdentifier, Email = credential == null ? null : credential.Email };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            users = users.Where(row => row.DisplayName.ToLower().Contains(term) || (row.Login != null && row.Login.ToLower().Contains(term)) || (row.Email != null && row.Email.ToLower().Contains(term)));
        }
        var total = await users.LongCountAsync(cancellationToken);
        var rows = await users.OrderBy(row => row.DisplayName).ThenBy(row => row.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize)
            .Select(row => new
            {
                row.Id, row.DisplayName, row.Login, row.Email, row.Role, row.IsManuallyDisabled, row.IsSecurityDisabled, row.IsNoMembershipDisabled, row.ManualDisableReason,
                MembershipCount = context.TenantMemberships.Count(membership => membership.UserId == row.Id)
            }).ToListAsync(cancellationToken);
        return new PagedResult<PlatformUserItem>(
            rows.Select(row => new PlatformUserItem(row.Id, row.DisplayName, row.Login, row.Email, row.Role, row.MembershipCount, row.IsManuallyDisabled || row.IsSecurityDisabled || row.IsNoMembershipDisabled, row.IsManuallyDisabled, row.IsSecurityDisabled, row.IsNoMembershipDisabled, row.ManualDisableReason)).ToList(),
            total, page, pageSize);
    }

    public async Task<PlatformChange<PlatformUserItem>> CreateUserAsync(
        UserId userId, string displayName, string username, string email, string passwordHash, UserRole role, IReadOnlyList<TenantId> tenantIds, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (await context.Tenants.CountAsync(tenant => tenantIds.Contains(tenant.Id), cancellationToken) != tenantIds.Count)
            return new(PlatformChangeOutcome.TenantNotFound);
        var user = new User(userId, displayName, role);
        var credential = new PasswordCredential(userId, username, passwordHash, email, mustChangePassword: true);
        context.Users.Add(user);
        context.PasswordCredentials.Add(credential);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, AuditActions.UserCreated, null,
            "user", Id(userId), new Dictionary<string, object?> { ["displayName"] = displayName, ["username"] = username, ["email"] = email, ["role"] = role.ToString() }));
        foreach (var tenantId in tenantIds)
        {
            context.TenantMemberships.Add(new TenantMembership(tenantId, userId));
            context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, AuditActions.MembershipAdded, tenantId,
                "membership", Id(userId), new Dictionary<string, object?> { ["userId"] = Id(userId) }));
        }
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { return new(PlatformChangeOutcome.LoginTaken); }
        return new(PlatformChangeOutcome.Changed, new PlatformUserItem(userId, displayName, username, email, role, tenantIds.Count, false, false, false, false, null));
    }

    public async Task<PlatformChange<PlatformUserItem>> UpdateUserAsync(
        UserId userId, string displayName, string username, string email, UserRole role, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM users WHERE \"Id\" = {userId.Value} FOR UPDATE", cancellationToken);
        var user = await context.Users.SingleOrDefaultAsync(row => row.Id == userId, cancellationToken);
        if (user is null) return new(PlatformChangeOutcome.UserNotFound);
        var count = await context.TenantMemberships.CountAsync(row => row.UserId == userId, cancellationToken);
        if (role == UserRole.PlatformAdmin && count != 0) return new(PlatformChangeOutcome.PlatformAdminCannotBeMember);
        if (role == UserRole.UserAdmin && count == 0) return new(PlatformChangeOutcome.UserAdminMembershipRequired);
        var credential = await context.PasswordCredentials.SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);
        if (credential is null) return new(PlatformChangeOutcome.UserNotFound);
        if (user.DisplayName == displayName && user.Role == role && credential.LoginIdentifier == username && credential.Email == email)
            return new(PlatformChangeOutcome.Unchanged, new PlatformUserItem(userId, displayName, username, email, role, count, user.IsDisabled, user.IsManuallyDisabled, user.IsSecurityDisabled, user.IsNoMembershipDisabled, user.ManualDisableReason));
        user.ChangeRole(role);
        credential.UpdateIdentity(username, email);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, AuditActions.UserUpdated, null,
            "user", Id(userId), new Dictionary<string, object?> { ["displayName"] = displayName, ["username"] = username, ["email"] = email, ["role"] = role.ToString() }));
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { return new(PlatformChangeOutcome.LoginTaken); }
        await transaction.CommitAsync(cancellationToken);
        return new(PlatformChangeOutcome.Changed, new PlatformUserItem(userId, displayName, username, email, role, count, user.IsDisabled, user.IsManuallyDisabled, user.IsSecurityDisabled, user.IsNoMembershipDisabled, user.ManualDisableReason));
    }

    public async Task<PlatformChange<PlatformUserItem>> DisableUserAsync(
        UserId userId, string? reason, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // Acquire an exclusive advisory lock to serialize platform admin disable operations globally
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext('platform_admin_disable_guard'))", cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM users WHERE \"Id\" = {userId.Value} FOR UPDATE", cancellationToken);
        var user = await context.Users.SingleOrDefaultAsync(row => row.Id == userId, cancellationToken);
        if (user is null) return new(PlatformChangeOutcome.UserNotFound);
        var credential = await context.PasswordCredentials.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);
        var count = await context.TenantMemberships.CountAsync(row => row.UserId == userId, cancellationToken);

        if (user.Role == UserRole.PlatformAdmin && !user.IsDisabled)
        {
            var activeAdmins = await context.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM users
                WHERE "Role" = {(int)UserRole.PlatformAdmin}
                  AND "IsManuallyDisabled" = false
                  AND "IsSecurityDisabled" = false
                  AND "IsNoMembershipDisabled" = false
                FOR UPDATE
                """).ToListAsync(cancellationToken);
            if (activeAdmins.Count <= 1)
            {
                return new(PlatformChangeOutcome.LastPlatformAdmin);
            }
        }
        if (user.IsManuallyDisabled && user.ManualDisableReason == reason)
        {
            return new(PlatformChangeOutcome.Unchanged, new PlatformUserItem(userId, user.DisplayName, credential?.LoginIdentifier, credential?.Email, user.Role, count, user.IsDisabled, user.IsManuallyDisabled, user.IsSecurityDisabled, user.IsNoMembershipDisabled, user.ManualDisableReason));
        }

        user.DisableManual(reason);
        // Immediately revoke all active sessions in the same transaction
        await context.UserSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, "user.disabled", null,
            "user", Id(userId), new Dictionary<string, object?> { ["userId"] = Id(userId), ["reason"] = reason }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(PlatformChangeOutcome.Changed, new PlatformUserItem(userId, user.DisplayName, credential?.LoginIdentifier, credential?.Email, user.Role, count, user.IsDisabled, user.IsManuallyDisabled, user.IsSecurityDisabled, user.IsNoMembershipDisabled, user.ManualDisableReason));
    }

    public async Task<PlatformChange<PlatformUserItem>> EnableUserAsync(
        UserId userId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM users WHERE \"Id\" = {userId.Value} FOR UPDATE", cancellationToken);
        var user = await context.Users.SingleOrDefaultAsync(row => row.Id == userId, cancellationToken);
        if (user is null) return new(PlatformChangeOutcome.UserNotFound);
        var credential = await context.PasswordCredentials.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);
        var count = await context.TenantMemberships.CountAsync(row => row.UserId == userId, cancellationToken);

        if (!user.IsManuallyDisabled)
        {
            return new(PlatformChangeOutcome.Unchanged, new PlatformUserItem(userId, user.DisplayName, credential?.LoginIdentifier, credential?.Email, user.Role, count, user.IsDisabled, user.IsManuallyDisabled, user.IsSecurityDisabled, user.IsNoMembershipDisabled, user.ManualDisableReason));
        }

        user.EnableManual();
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, "user.enabled", null,
            "user", Id(userId), new Dictionary<string, object?> { ["userId"] = Id(userId) }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(PlatformChangeOutcome.Changed, new PlatformUserItem(userId, user.DisplayName, credential?.LoginIdentifier, credential?.Email, user.Role, count, user.IsDisabled, user.IsManuallyDisabled, user.IsSecurityDisabled, user.IsNoMembershipDisabled, user.ManualDisableReason));
    }

    public async Task<PlatformChange<PlatformUserItem>> AssignTemporaryPasswordAsync(
        UserId targetUserId, string passwordHash, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM users WHERE \"Id\" = {targetUserId.Value} FOR UPDATE", cancellationToken);
        var user = await context.Users.SingleOrDefaultAsync(row => row.Id == targetUserId, cancellationToken);
        if (user is null) return new(PlatformChangeOutcome.UserNotFound);
        var credential = await context.PasswordCredentials.SingleOrDefaultAsync(row => row.UserId == targetUserId, cancellationToken);
        if (credential is null) return new(PlatformChangeOutcome.UserNotFound);
        credential.ReplacePasswordHash(passwordHash, mustChangePassword: true);
        // End all active sessions of the target user in the same transaction
        await context.UserSessions.Where(row => row.UserId == targetUserId).ExecuteDeleteAsync(cancellationToken);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, "user.temporary_password_assigned", null,
            "user", Id(targetUserId), new Dictionary<string, object?> { ["targetUserId"] = Id(targetUserId) }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var count = await context.TenantMemberships.CountAsync(row => row.UserId == targetUserId, cancellationToken);
        return new(PlatformChangeOutcome.Changed, new PlatformUserItem(targetUserId, user.DisplayName, credential.LoginIdentifier, credential.Email, user.Role, count));
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
        TenantId tenantId, UserId userId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM users WHERE \"Id\" = {userId.Value} FOR UPDATE", cancellationToken);
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
        {
            return new(PlatformChangeOutcome.TenantNotFound);
        }
        var targetUser = await context.Users.SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
        if (targetUser is null)
        {
            return new(PlatformChangeOutcome.UserNotFound);
        }
        if (targetUser.Role == UserRole.PlatformAdmin)
        {
            return new(PlatformChangeOutcome.PlatformAdminCannotBeMember);
        }
        if (await context.TenantMemberships.AnyAsync(membership => membership.TenantId == tenantId && membership.UserId == userId, cancellationToken))
        {
            return new(PlatformChangeOutcome.AlreadyMember);
        }
        context.TenantMemberships.Add(new TenantMembership(tenantId, userId));
        // Re-adding a membership clears the NoMembership disable reason
        if (targetUser.IsNoMembershipDisabled)
        {
            targetUser.SetNoMembershipDisabled(false);
        }
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, AuditActions.MembershipAdded, tenantId,
            "membership", Id(userId), new Dictionary<string, object?> { ["userId"] = Id(userId) }));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new(PlatformChangeOutcome.AlreadyMember);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(PlatformChangeOutcome.Changed, await ReadMembershipAsync(context, tenantId, userId, cancellationToken));
    }

    public async Task<PlatformChange<PlatformMembershipItem>> RemoveMembershipAsync(
        TenantId tenantId, UserId userId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM users WHERE \"Id\" = {userId.Value} FOR UPDATE", cancellationToken);
        var membership = await context.TenantMemberships.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.UserId == userId, cancellationToken);
        if (membership is null)
        {
            return new(PlatformChangeOutcome.MembershipNotFound);
        }
        var item = await ReadMembershipAsync(context, tenantId, userId, cancellationToken);
        context.TenantMemberships.Remove(membership);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, AuditActions.MembershipRemoved, tenantId,
            "membership", Id(userId), new Dictionary<string, object?> { ["userId"] = Id(userId) }));

        var targetUser = await context.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (targetUser is { Role: UserRole.UserAdmin })
        {
            var remainingCount = await context.TenantMemberships.CountAsync(m => m.UserId == userId && m.TenantId != tenantId, cancellationToken);
            if (remainingCount == 0)
            {
                targetUser.SetNoMembershipDisabled(true);
                // End all sessions of the user since no membership remains
                await context.UserSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
                context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, "user.disabled", null,
                    "user", Id(userId), new Dictionary<string, object?> { ["userId"] = Id(userId), ["reason"] = "no_membership" }));
            }
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
    }

    private static IQueryable<MembershipRow> MembershipItems(PlatformDbContext context, TenantId tenantId, UserId? userId = null) =>
        from membership in context.TenantMemberships.AsNoTracking()
        where membership.TenantId == tenantId && (userId == null || membership.UserId == userId)
        join user in context.Users.AsNoTracking() on membership.UserId equals user.Id
        join credential in context.PasswordCredentials.AsNoTracking() on user.Id equals credential.UserId into joined
        from credential in joined.DefaultIfEmpty()
        select new MembershipRow { UserId = user.Id, DisplayName = user.DisplayName, Login = credential == null ? null : credential.LoginIdentifier };

    private static PlatformMembershipItem ToItem(MembershipRow row) => new(row.UserId, row.DisplayName, row.Login);
    private static async Task<PlatformMembershipItem> ReadMembershipAsync(
        PlatformDbContext context, TenantId tenantId, UserId userId, CancellationToken cancellationToken) =>
        ToItem(await MembershipItems(context, tenantId, userId).SingleAsync(cancellationToken));
}
