using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>
/// Limits are rows; "unlimited" is no row. A tenant's slices are decided under one transaction-scoped advisory lock per tenant, so any number of backend
/// nodes serialize on the database and none keeps state in memory: a restart, a second node or a crashed agent changes nothing but when a slice expires.
/// </summary>
public sealed class PostgreSqlUploadBandwidthStore(DbContextOptions<PlatformDbContext> options) : IUploadBandwidthStore
{
    public async Task<UploadBandwidthSettings> GetAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var rows = await context.UploadBandwidthLimits.AsNoTracking().Where(limit => limit.TenantId == tenantId).ToListAsync(cancellationToken);
        var assignments = await context.BandwidthProfileAssignments.AsNoTracking().Where(a => a.TenantId == tenantId).ToListAsync(cancellationToken);
        return new UploadBandwidthSettings(
            rows.SingleOrDefault(limit => limit.Scope == UploadBandwidthScope.Tenant)?.BytesPerSecond,
            rows.Where(limit => limit.Scope == UploadBandwidthScope.Agent).ToDictionary(limit => limit.ScopeId, limit => limit.BytesPerSecond),
            rows.Where(limit => limit.Scope == UploadBandwidthScope.Job).ToDictionary(limit => limit.ScopeId, limit => limit.BytesPerSecond),
            assignments.SingleOrDefault(a => a.Scope == UploadBandwidthScope.Tenant)?.ProfileId,
            assignments.Where(a => a.Scope == UploadBandwidthScope.Agent).ToDictionary(a => a.ScopeId, a => a.ProfileId),
            assignments.Where(a => a.Scope == UploadBandwidthScope.Job).ToDictionary(a => a.ScopeId, a => a.ProfileId));
    }

    public async Task<bool> SetAsync(TenantId tenantId, UploadBandwidthScope scope, Guid scopeId, long? bytesPerSecond, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var context = PlatformDbContext.CreateForPlatformOperations(options);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var owned = scope switch
            {
                UploadBandwidthScope.Tenant => scopeId == tenantId.Value && await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken),
                UploadBandwidthScope.Agent => await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == new AgentId(scopeId), cancellationToken),
                UploadBandwidthScope.Job => await context.BackupJobs.AnyAsync(job => job.TenantId == tenantId && job.Id == scopeId, cancellationToken),
                _ => false
            };
            if (!owned) return false;
            var existing = await context.UploadBandwidthLimits.SingleOrDefaultAsync(limit => limit.TenantId == tenantId && limit.Scope == scope && limit.ScopeId == scopeId, cancellationToken);
            var before = existing?.BytesPerSecond;
            if (before == bytesPerSecond) return true;
            if (bytesPerSecond is not { } value) context.UploadBandwidthLimits.Remove(existing!);
            else if (existing is null) context.UploadBandwidthLimits.Add(new UploadBandwidthLimit(tenantId, scope, scopeId, value, now));
            else existing.Change(value, now);
            context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.TenantAdministrator, AuditActions.UploadBandwidthLimitChanged, tenantId,
                scope switch { UploadBandwidthScope.Tenant => "tenant", UploadBandwidthScope.Agent => "agent", _ => "backup_job" }, scopeId.ToString("D"),
                new Dictionary<string, object?>
                {
                    ["result"] = "succeeded", ["scope"] = scope.ToString(), ["previousBytesPerSecond"] = before, ["bytesPerSecond"] = bytesPerSecond
                }));
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException exception) when (attempt == 0 && PostgreSqlBackupRunStore.IsUniqueViolation(exception))
            {
                // A concurrent first write of the same limit won the race; apply this one over it.
            }
        }
    }

    public async Task<AttemptUploadLimits?> GrantAsync(BackupLeaseCommand lease, UploadSliceAction action, TimeSpan grantTtl, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var now = lease.Now;
        var uploadId = lease.UploadId ?? lease.AttemptId;
        var jobId = await RunningJobAsync(context, lease.TenantId, lease.AgentId, lease.AttemptId, lease.LeaseToken, now, cancellationToken);
        if (jobId is null) return null;
        var limits = await LimitsAsync(context, lease.TenantId, lease.AgentId, jobId.Value, now, cancellationToken);
        if (action == UploadSliceAction.Observe) return limits;
        if (action == UploadSliceAction.Release || limits.Tenant is null)
        {
            // Nothing to divide: no lock either. A slice left over from before the tenant limit was removed is simply dropped.
            await context.UploadBandwidthGrants.Where(grant => grant.TenantId == lease.TenantId && grant.UploadId == uploadId).ExecuteDeleteAsync(cancellationToken);
            return limits;
        }
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"upload-bandwidth:" + lease.TenantId.Value.ToString("N")}, 0))", cancellationToken);
        // The lease was checked before the lock; it is checked again here so a slice is never granted to an attempt that ended while this call waited.
        if (await RunningJobAsync(context, lease.TenantId, lease.AgentId, lease.AttemptId, lease.LeaseToken, now, cancellationToken) is null) return null;
        // Slices are leases between uploaders on DIFFERENT nodes, so their expiry is judged by the database clock alone: a node whose own clock runs ahead must not
        // see another node's live slice as expired and hand itself the whole limit. (The attempt's own lease above is checked against the caller's clock like
        // every other lifecycle call.)
        var dbNow = await context.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
        // Pruned before this upload's own grant is loaded: a grant that lapsed because renewals were missed is simply re-created.
        await context.UploadBandwidthGrants.Where(grant => grant.TenantId == lease.TenantId && grant.ExpiresAt <= dbNow).ExecuteDeleteAsync(cancellationToken);
        var mine = await context.UploadBandwidthGrants.SingleOrDefaultAsync(grant => grant.TenantId == lease.TenantId && grant.UploadId == uploadId, cancellationToken);
        // Only uploads that are alive count: a grant whose attempt ended or lost its lease frees its slice at once, not when it expires.
        var others = await (from grant in context.UploadBandwidthGrants.AsNoTracking()
                            join attempt in context.BackupRunAttempts.AsNoTracking() on grant.AttemptId equals attempt.Id
                            where grant.TenantId == lease.TenantId && grant.UploadId != uploadId && grant.ExpiresAt > dbNow
                                && attempt.Status == BackupStatus.Running && attempt.LeaseExpiresAt > now
                            select grant.BytesPerSecond).ToListAsync(cancellationToken);
        var share = UploadBandwidth.Share(limits.Tenant.Value, others.Count, others.Sum());
        if (mine is null) context.UploadBandwidthGrants.Add(new UploadBandwidthGrant(lease.TenantId, uploadId, lease.AttemptId, share, dbNow, dbNow + grantTtl));
        else mine.Renew(share, dbNow, dbNow + grantTtl);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return limits with { TenantShare = share };
    }

    /// <summary>Whether any ceiling applies to a run of this job on this agent; an agent that cannot throttle must not start such a run.</summary>
    /// <summary>Whether any ceiling applies to a run of this job on this agent; an agent that cannot throttle must not start such a run.</summary>
    internal static async Task<bool> AnyAsync(PlatformDbContext context, TenantId tenantId, AgentId agentId, Guid jobId, CancellationToken cancellationToken)
    {
        var hasStatic = await context.UploadBandwidthLimits.AsNoTracking().AnyAsync(limit => limit.TenantId == tenantId && (
            limit.Scope == UploadBandwidthScope.Tenant && limit.ScopeId == tenantId.Value
            || limit.Scope == UploadBandwidthScope.Agent && limit.ScopeId == agentId.Value
            || limit.Scope == UploadBandwidthScope.Job && limit.ScopeId == jobId), cancellationToken);
        if (hasStatic) return true;
        return await context.BandwidthProfileAssignments.AsNoTracking().AnyAsync(assignment => assignment.TenantId == tenantId && (
            assignment.Scope == UploadBandwidthScope.Tenant && assignment.ScopeId == tenantId.Value
            || assignment.Scope == UploadBandwidthScope.Agent && assignment.ScopeId == agentId.Value
            || assignment.Scope == UploadBandwidthScope.Job && assignment.ScopeId == jobId), cancellationToken);
    }

    internal static Task<bool> SupportsAsync(PlatformDbContext context, TenantId tenantId, AgentId agentId, CancellationToken cancellationToken) =>
        context.AgentCapabilities.AsNoTracking().AnyAsync(record => record.TenantId == tenantId && record.AgentId == agentId
            && record.Identifier == BackupPlatform.Contracts.Backups.BackupTaskProtocol.UploadBandwidthLimitCapability
            && record.Support == BackupPlatform.Domain.Capabilities.CapabilitySupport.Supported, cancellationToken);

    private static async Task<Guid?> RunningJobAsync(PlatformDbContext context, TenantId tenantId, AgentId agentId, Guid attemptId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken) =>
        leaseToken == Guid.Empty ? null : await (from attempt in context.BackupRunAttempts.AsNoTracking()
                                                 join run in context.BackupRuns.AsNoTracking() on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                                                 where attempt.TenantId == tenantId && attempt.AgentId == agentId && attempt.Id == attemptId
                                                     && attempt.Status == BackupStatus.Running && attempt.LeaseToken == leaseToken && attempt.LeaseExpiresAt > now
                                                 select (Guid?)run.JobId).SingleOrDefaultAsync(cancellationToken);

    private static async Task<AttemptUploadLimits> LimitsAsync(PlatformDbContext context, TenantId tenantId, AgentId agentId, Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await context.UploadBandwidthLimits.AsNoTracking().Where(limit => limit.TenantId == tenantId && (
            limit.Scope == UploadBandwidthScope.Tenant && limit.ScopeId == tenantId.Value
            || limit.Scope == UploadBandwidthScope.Agent && limit.ScopeId == agentId.Value
            || limit.Scope == UploadBandwidthScope.Job && limit.ScopeId == jobId)).ToListAsync(cancellationToken);

        var staticJob = rows.SingleOrDefault(limit => limit.Scope == UploadBandwidthScope.Job)?.BytesPerSecond;
        var staticAgent = rows.SingleOrDefault(limit => limit.Scope == UploadBandwidthScope.Agent)?.BytesPerSecond;
        var staticTenant = rows.SingleOrDefault(limit => limit.Scope == UploadBandwidthScope.Tenant)?.BytesPerSecond;

        // Profile resolution: Job > Agent > Tenant
        var assignments = await context.BandwidthProfileAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && (
            a.Scope == UploadBandwidthScope.Job && a.ScopeId == jobId
            || a.Scope == UploadBandwidthScope.Agent && a.ScopeId == agentId.Value
            || a.Scope == UploadBandwidthScope.Tenant && a.ScopeId == tenantId.Value)).ToListAsync(cancellationToken);

        var assignedProfileId = assignments.SingleOrDefault(a => a.Scope == UploadBandwidthScope.Job)?.ProfileId
            ?? assignments.SingleOrDefault(a => a.Scope == UploadBandwidthScope.Agent)?.ProfileId
            ?? assignments.SingleOrDefault(a => a.Scope == UploadBandwidthScope.Tenant)?.ProfileId;

        TimeSpan? nextTransition = null;
        if (assignedProfileId is { } profileId)
        {
            var profile = await context.BandwidthProfiles.Include(p => p.Windows).AsNoTracking().SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == profileId, cancellationToken);
            if (profile != null)
            {
                var (profileLimit, transitionInstant) = profile.Evaluate(now);
                if (transitionInstant != null)
                {
                    var remaining = transitionInstant.Value - now;
                    if (remaining > TimeSpan.Zero) nextTransition = remaining;
                }
                if (profileLimit != null)
                {
                    var scope = assignments.First(a => a.ProfileId == profileId).Scope;
                    if (scope == UploadBandwidthScope.Job) staticJob = staticJob is null ? profileLimit : Math.Min(staticJob.Value, profileLimit.Value);
                    else if (scope == UploadBandwidthScope.Agent) staticAgent = staticAgent is null ? profileLimit : Math.Min(staticAgent.Value, profileLimit.Value);
                    else if (scope == UploadBandwidthScope.Tenant) staticTenant = staticTenant is null ? profileLimit : Math.Min(staticTenant.Value, profileLimit.Value);
                }
            }
        }

        return new AttemptUploadLimits(staticJob, staticAgent, staticTenant, NextTransition: nextTransition);
    }

    public async Task<IReadOnlyList<BandwidthProfileView>> ListProfilesAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var profiles = await context.BandwidthProfiles.Include(p => p.Windows).AsNoTracking()
            .Where(p => p.TenantId == tenantId).OrderBy(p => p.Name).ToListAsync(cancellationToken);
        return profiles.Select(ToView).ToList();
    }

    public async Task<BandwidthProfileView?> GetProfileAsync(TenantId tenantId, Guid profileId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var profile = await context.BandwidthProfiles.Include(p => p.Windows).AsNoTracking()
            .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == profileId, cancellationToken);
        return profile == null ? null : ToView(profile);
    }

    public async Task<BandwidthProfileView> SaveProfileAsync(TenantId tenantId, Guid? profileId, string name, string timeZoneId,
        IEnumerable<TransferScheduleWindowDto> windows, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        BandwidthProfile profile;
        var isNew = false;
        if (profileId is null || profileId == Guid.Empty)
        {
            profile = new BandwidthProfile(Guid.NewGuid(), tenantId, name, timeZoneId, now);
            isNew = true;
            context.BandwidthProfiles.Add(profile);
        }
        else
        {
            profile = await context.BandwidthProfiles.Include(p => p.Windows)
                .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == profileId.Value, cancellationToken)
                ?? throw new KeyNotFoundException($"Bandwidth profile '{profileId}' not found.");
            profile.Update(name, timeZoneId, now);
        }

        var domainWindows = windows.Select(w => new TransferScheduleWindow(Guid.NewGuid(), profile.Id, w.DayOfWeek, w.StartTime, w.EndTime, w.BytesPerSecond)).ToList();
        profile.SetWindows(domainWindows);

        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.TenantAdministrator,
            isNew ? AuditActions.BandwidthProfileCreated : AuditActions.BandwidthProfileUpdated, tenantId,
            "bandwidth_profile", profile.Id.ToString("D"),
            new Dictionary<string, object?> { ["name"] = profile.Name, ["timeZoneId"] = profile.TimeZoneId, ["windowCount"] = domainWindows.Count }));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToView(profile);
    }

    public async Task<bool> DeleteProfileAsync(TenantId tenantId, Guid profileId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var profile = await context.BandwidthProfiles.SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == profileId, cancellationToken);
        if (profile is null) return false;

        await context.BandwidthProfileAssignments.Where(a => a.TenantId == tenantId && a.ProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
        context.BandwidthProfiles.Remove(profile);

        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.TenantAdministrator,
            AuditActions.BandwidthProfileDeleted, tenantId, "bandwidth_profile", profile.Id.ToString("D"),
            new Dictionary<string, object?> { ["name"] = profile.Name }));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AssignProfileAsync(TenantId tenantId, UploadBandwidthScope scope, Guid scopeId, Guid? profileId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var owned = scope switch
        {
            UploadBandwidthScope.Tenant => scopeId == tenantId.Value && await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken),
            UploadBandwidthScope.Agent => await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == new AgentId(scopeId), cancellationToken),
            UploadBandwidthScope.Job => await context.BackupJobs.AnyAsync(job => job.TenantId == tenantId && job.Id == scopeId, cancellationToken),
            _ => false
        };
        if (!owned) return false;

        if (profileId != null && !await context.BandwidthProfiles.AnyAsync(p => p.TenantId == tenantId && p.Id == profileId.Value, cancellationToken))
            return false;

        var existing = await context.BandwidthProfileAssignments.SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Scope == scope && a.ScopeId == scopeId, cancellationToken);
        if (profileId is null)
        {
            if (existing != null) context.BandwidthProfileAssignments.Remove(existing);
        }
        else if (existing is null)
        {
            context.BandwidthProfileAssignments.Add(new BandwidthProfileAssignment(tenantId, scope, scopeId, profileId.Value, now));
        }
        else
        {
            existing.ChangeProfile(profileId.Value, now);
        }

        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.TenantAdministrator,
            AuditActions.BandwidthProfileAssigned, tenantId,
            scope switch { UploadBandwidthScope.Tenant => "tenant", UploadBandwidthScope.Agent => "agent", _ => "backup_job" }, scopeId.ToString("D"),
            new Dictionary<string, object?> { ["scope"] = scope.ToString(), ["profileId"] = profileId }));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static BandwidthProfileView ToView(BandwidthProfile profile) =>
        new(profile.Id, profile.Name, profile.TimeZoneId,
            profile.Windows.Select(w => new TransferScheduleWindowDto(w.DayOfWeek, w.StartTime, w.EndTime, w.BytesPerSecond)).ToList());
}
