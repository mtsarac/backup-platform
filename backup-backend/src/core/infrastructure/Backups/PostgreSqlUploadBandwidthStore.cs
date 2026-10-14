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
        return new UploadBandwidthSettings(
            rows.SingleOrDefault(limit => limit.Scope == UploadBandwidthScope.Tenant)?.BytesPerSecond,
            rows.Where(limit => limit.Scope == UploadBandwidthScope.Agent).ToDictionary(limit => limit.ScopeId, limit => limit.BytesPerSecond),
            rows.Where(limit => limit.Scope == UploadBandwidthScope.Job).ToDictionary(limit => limit.ScopeId, limit => limit.BytesPerSecond));
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
        var jobId = await RunningJobAsync(context, lease.TenantId, lease.AgentId, lease.AttemptId, lease.LeaseToken, now, cancellationToken);
        if (jobId is null) return null;
        var limits = await LimitsAsync(context, lease.TenantId, lease.AgentId, jobId.Value, cancellationToken);
        if (action == UploadSliceAction.Observe) return limits;
        if (action == UploadSliceAction.Release || limits.Tenant is null)
        {
            // Nothing to divide: no lock either. A slice left over from before the tenant limit was removed is simply dropped.
            await context.UploadBandwidthGrants.Where(grant => grant.TenantId == lease.TenantId && grant.AttemptId == lease.AttemptId).ExecuteDeleteAsync(cancellationToken);
            return limits;
        }
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"upload-bandwidth:" + lease.TenantId.Value.ToString("N")}, 0))", cancellationToken);
        // The lease was checked before the lock; it is checked again here so a slice is never granted to an attempt that ended while this call waited.
        if (await RunningJobAsync(context, lease.TenantId, lease.AgentId, lease.AttemptId, lease.LeaseToken, now, cancellationToken) is null) return null;
        // Pruned before this attempt's own grant is loaded: a grant that lapsed because renewals were missed is simply re-created.
        await context.UploadBandwidthGrants.Where(grant => grant.TenantId == lease.TenantId && grant.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        var mine = await context.UploadBandwidthGrants.SingleOrDefaultAsync(grant => grant.TenantId == lease.TenantId && grant.AttemptId == lease.AttemptId, cancellationToken);
        // Only uploads that are alive count: a grant whose attempt ended or lost its lease frees its slice at once, not when it expires.
        var others = await (from grant in context.UploadBandwidthGrants.AsNoTracking()
                            join attempt in context.BackupRunAttempts.AsNoTracking() on grant.AttemptId equals attempt.Id
                            where grant.TenantId == lease.TenantId && grant.AttemptId != lease.AttemptId && grant.ExpiresAt > now
                                && attempt.Status == BackupStatus.Running && attempt.LeaseExpiresAt > now
                            select grant.BytesPerSecond).ToListAsync(cancellationToken);
        var share = UploadBandwidth.Share(limits.Tenant.Value, others.Count, others.Sum());
        if (mine is null) context.UploadBandwidthGrants.Add(new UploadBandwidthGrant(lease.TenantId, lease.AttemptId, share, now, now + grantTtl));
        else mine.Renew(share, now, now + grantTtl);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return limits with { TenantShare = share };
    }

    /// <summary>Whether any ceiling applies to a run of this job on this agent; an agent that cannot throttle must not start such a run.</summary>
    internal static Task<bool> AnyAsync(PlatformDbContext context, TenantId tenantId, AgentId agentId, Guid jobId, CancellationToken cancellationToken) =>
        context.UploadBandwidthLimits.AsNoTracking().AnyAsync(limit => limit.TenantId == tenantId && (
            limit.Scope == UploadBandwidthScope.Tenant && limit.ScopeId == tenantId.Value
            || limit.Scope == UploadBandwidthScope.Agent && limit.ScopeId == agentId.Value
            || limit.Scope == UploadBandwidthScope.Job && limit.ScopeId == jobId), cancellationToken);

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

    private static async Task<AttemptUploadLimits> LimitsAsync(PlatformDbContext context, TenantId tenantId, AgentId agentId, Guid jobId, CancellationToken cancellationToken)
    {
        var rows = await context.UploadBandwidthLimits.AsNoTracking().Where(limit => limit.TenantId == tenantId && (
            limit.Scope == UploadBandwidthScope.Tenant && limit.ScopeId == tenantId.Value
            || limit.Scope == UploadBandwidthScope.Agent && limit.ScopeId == agentId.Value
            || limit.Scope == UploadBandwidthScope.Job && limit.ScopeId == jobId)).ToListAsync(cancellationToken);
        return new AttemptUploadLimits(
            rows.SingleOrDefault(limit => limit.Scope == UploadBandwidthScope.Job)?.BytesPerSecond,
            rows.SingleOrDefault(limit => limit.Scope == UploadBandwidthScope.Agent)?.BytesPerSecond,
            rows.SingleOrDefault(limit => limit.Scope == UploadBandwidthScope.Tenant)?.BytesPerSecond);
    }
}
