using BackupPlatform.Application.Lists;
using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Infrastructure.Lists;
using BackupPlatform.Infrastructure.Notifications;
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
            await context.BackupJobs.LongCountAsync(job => job.Enabled && !context.Agents.Any(agent => agent.TenantId == job.TenantId && agent.Id == job.AgentId && agent.Status == AgentStatus.Revoked), cancellationToken),
            await context.BackupRuns.LongCountAsync(run => run.CompletedAt >= since && (run.Status == BackupStatus.Failed || run.Status == BackupStatus.AgentLost), cancellationToken));
    }

    private sealed class TenantRow
    {
        public required TenantId Id { get; init; }
        public required string Name { get; init; }
        public required int MemberCount { get; init; }
        public required int AgentCount { get; init; }
        public required bool EnrollmentEnabled { get; init; }
    }

    // Tenants carry no creation date, so the list opens A to Z.
    private static readonly SortMap<TenantRow> TenantSorts = SortMap<TenantRow>.Create()
        .Text("name", row => row.Name)
        .Value("memberCount", row => row.MemberCount)
        .Value("agentCount", row => row.AgentCount)
        .Ordinal("enrollmentEnabled", row => row.EnrollmentEnabled, true, false); // "Kayıt açık" before "Kayıt kapalı"

    public async Task<PagedResult<PlatformTenantItem>> ListTenantsAsync(ListQuery query, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var page = await TenantRows(context, context.Tenants.AsNoTracking())
            .ApplySearch(query, row => row.Name)
            .ApplySort(TenantSorts, query, "name", row => row.Id)
            .ToPagedResultAsync(query, cancellationToken);
        return new PagedResult<PlatformTenantItem>(page.Items.Select(ToItem).ToList(), page.TotalCount, page.Page, page.PageSize);
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
            await context.BackupJobs.CountAsync(job => job.TenantId == tenantId && job.Enabled && !context.Agents.Any(agent => agent.TenantId == job.TenantId && agent.Id == job.AgentId && agent.Status == AgentStatus.Revoked), cancellationToken),
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
        var row = await TenantRows(context, context.Tenants.AsNoTracking().Where(row => row.Id == tenantId)).SingleAsync(cancellationToken);
        return new(outcome, ToItem(row));
    }

    private sealed class UserRow
    {
        public required UserId Id { get; init; }
        public required string DisplayName { get; init; }
        public string? Login { get; init; }
        public string? Email { get; init; }
        public required UserRole Role { get; init; }
        public required int MembershipCount { get; init; }
        public required bool IsManuallyDisabled { get; init; }
        public required bool IsSecurityDisabled { get; init; }
        public required bool IsNoMembershipDisabled { get; init; }
        public string? ManualDisableReason { get; init; }
    }

    // Role and status sort by the Turkish label shown: "Kullanıcı Yöneticisi" before "Platform Yöneticisi", "Devre dışı" before "Etkin".
    private static readonly SortMap<UserRow> UserSorts = SortMap<UserRow>.Create()
        .Text("displayName", row => row.DisplayName)
        .Text("username", row => row.Login)
        .Text("email", row => row.Email)
        .Ordinal("role", row => row.Role, UserRole.UserAdmin, UserRole.PlatformAdmin)
        .Value("membershipCount", row => row.MembershipCount)
        .Ordinal("isDisabled", row => row.IsManuallyDisabled || row.IsSecurityDisabled || row.IsNoMembershipDisabled, true, false);

    public async Task<PagedResult<PlatformUserItem>> ListUsersAsync(ListQuery query, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var users = from user in context.Users.AsNoTracking()
                    join credential in context.PasswordCredentials.AsNoTracking() on user.Id equals credential.UserId into joined
                    from credential in joined.DefaultIfEmpty()
                    select new UserRow
                    {
                        Id = user.Id, DisplayName = user.DisplayName, Role = user.Role,
                        IsManuallyDisabled = user.IsManuallyDisabled, IsSecurityDisabled = user.IsSecurityDisabled, IsNoMembershipDisabled = user.IsNoMembershipDisabled,
                        ManualDisableReason = user.ManualDisableReason,
                        Login = credential == null ? null : credential.LoginIdentifier, Email = credential == null ? null : credential.Email,
                        MembershipCount = context.TenantMemberships.Count(membership => membership.UserId == user.Id)
                    };
        var page = await users
            .ApplySearch(query, row => row.DisplayName, row => row.Login, row => row.Email)
            .ApplySort(UserSorts, query, "displayName", row => row.Id)
            .ToPagedResultAsync(query, cancellationToken);
        return new PagedResult<PlatformUserItem>(
            page.Items.Select(row => new PlatformUserItem(row.Id, row.DisplayName, row.Login, row.Email, row.Role, row.MembershipCount, row.IsManuallyDisabled || row.IsSecurityDisabled || row.IsNoMembershipDisabled, row.IsManuallyDisabled, row.IsSecurityDisabled, row.IsNoMembershipDisabled, row.ManualDisableReason)).ToList(),
            page.TotalCount, page.Page, page.PageSize);
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
        var emailChanged = credential.NormalizedEmail != LoginIdentifierNormalizer.NormalizeEmail(email);
        user.ChangeRole(role);
        credential.UpdateIdentity(username, email);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, AuditActions.UserUpdated, null,
            "user", Id(userId), new Dictionary<string, object?> { ["displayName"] = displayName, ["username"] = username, ["email"] = email, ["role"] = role.ToString() }));
        if (emailChanged) await SecurityNotifications.RecordAsync(context, userId, NotificationEventTypes.SecurityEmailChanged, now, cancellationToken);
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
        await SecurityNotifications.RecordAsync(context, userId, NotificationEventTypes.SecurityAccountDisabled, now, cancellationToken);
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
        await SecurityNotifications.RecordAsync(context, userId, NotificationEventTypes.SecurityAccountEnabled, now, cancellationToken);
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
        await SecurityNotifications.RecordAsync(context, targetUserId, NotificationEventTypes.SecurityTemporaryPassword, now, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var count = await context.TenantMemberships.CountAsync(row => row.UserId == targetUserId, cancellationToken);
        return new(PlatformChangeOutcome.Changed, new PlatformUserItem(targetUserId, user.DisplayName, credential.LoginIdentifier, credential.Email, user.Role, count, user.IsDisabled, user.IsManuallyDisabled, user.IsSecurityDisabled, user.IsNoMembershipDisabled, user.ManualDisableReason));
    }

    // Memberships carry no join date, so the list opens A to Z.
    private static readonly SortMap<MembershipRow> MembershipSorts = SortMap<MembershipRow>.Create()
        .Text("displayName", row => row.DisplayName)
        .Text("username", row => row.Login);

    public async Task<PagedResult<PlatformMembershipItem>?> ListMembershipsAsync(TenantId tenantId, ListQuery query, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
        {
            return null;
        }
        var page = await MembershipItems(context, tenantId)
            .ApplySearch(query, row => row.DisplayName, row => row.Login)
            .ApplySort(MembershipSorts, query, "displayName", row => row.UserId)
            .ToPagedResultAsync(query, cancellationToken);
        return new PagedResult<PlatformMembershipItem>(page.Items.Select(ToItem).ToList(), page.TotalCount, page.Page, page.PageSize);
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
            await SecurityNotifications.RecordAsync(context, userId, NotificationEventTypes.SecurityAccountEnabled, now, cancellationToken);
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
                await SecurityNotifications.RecordAsync(context, userId, NotificationEventTypes.SecurityAccountDisabled, now, cancellationToken);
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
    private sealed class AuditRow
    {
        public required AuditEvent Event { get; init; }
        public string? ActorName { get; init; }
        public string? TenantName { get; init; }
    }

    private static readonly SortMap<AuditRow> AuditSorts = SortMap<AuditRow>.Create()
        .Date("at", row => row.Event.At)
        .Text("action", row => row.Event.Action)
        .Text("actor", row => row.ActorName)
        .Text("tenant", row => row.TenantName);

    public async Task<PagedResult<PlatformAuditItem>> ListAuditAsync(TenantId? tenantId, ListQuery query, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var events = context.AuditEvents.AsNoTracking().AsQueryable();
        if (tenantId is not null)
        {
            events = events.Where(auditEvent => auditEvent.TenantId == tenantId);
        }
        var rows = from auditEvent in events
                   select new AuditRow
                   {
                       Event = auditEvent,
                       ActorName = auditEvent.ActorKind == AuditActorKind.System ? "System"
                           : auditEvent.ActorKind == AuditActorKind.Anonymous ? "Anonymous"
                           : context.Users.Where(user => user.Id == auditEvent.ActorUserId).Select(user => user.DisplayName).FirstOrDefault(),
                       TenantName = context.Tenants.Where(tenant => tenant.Id == auditEvent.TenantId).Select(tenant => tenant.Name).FirstOrDefault()
                   };
        var page = await rows
            .ApplySearch(query, row => row.Event.Action, row => row.ActorName, row => row.Event.TargetType, row => row.Event.TargetId, row => row.TenantName)
            .ApplySort(AuditSorts, query, "at", row => row.Event.Id)
            .ToPagedResultAsync(query, cancellationToken);
        return new PagedResult<PlatformAuditItem>(
            page.Items.Select(row => new PlatformAuditItem(
                row.Event.Id, row.Event.At, row.Event.ActorUserId, row.ActorName, row.Event.ActorKind, row.Event.Action,
                row.Event.TenantId, row.TenantName, row.Event.TargetType, row.Event.TargetId, row.Event.DetailsJson)).ToList(),
            page.TotalCount, page.Page, page.PageSize);
    }

    private static string Id(TenantId id) => id.Value.ToString("D");

    private static string Id(UserId id) => id.Value.ToString("D");

    private static IQueryable<TenantRow> TenantRows(PlatformDbContext context, IQueryable<Tenant> tenants) =>
        tenants.Select(tenant => new TenantRow
        {
            Id = tenant.Id, Name = tenant.Name,
            MemberCount = context.TenantMemberships.Count(membership => membership.TenantId == tenant.Id),
            AgentCount = context.Agents.Count(agent => agent.TenantId == tenant.Id),
            EnrollmentEnabled = context.TenantAgentEnrollmentPolicies.Any(policy => policy.TenantId == tenant.Id && policy.IsEnabled)
        });

    private static PlatformTenantItem ToItem(TenantRow row) => new(row.Id, row.Name, row.MemberCount, row.AgentCount, row.EnrollmentEnabled);

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
