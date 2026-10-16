using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService.Entitlement;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Infrastructure.ManagedService;

/// <summary>
/// The admission gate for a NEW BackupRun: tenant standing (M1 entitlement) and quota, decided and reserved in one transaction under the
/// tenant lock, so two admissions for one tenant can never both read the same free capacity. Backup orchestration sees only
/// <see cref="IBackupAdmissionPolicy"/>; every plan, policy and usage detail stays here.
/// </summary>
/// <remarks>
/// Read order inside the transaction: lock the tenant → subscription entitlement → effective policy → release stale reservations → current
/// projection (recomputed from the sources when missing or stale, so a deployment that has not backfilled yet still decides on correct usage) →
/// decision → reservation. A tenant with no limit, or whose policy enforces nothing, is decided without touching the projection.
/// The run and schedule stores call it inside their own (agent-locked) transaction, so the reservation and the run are created or discarded together;
/// <see cref="AdmitNewRunAsync"/> is the same decision in a transaction of its own.
/// </remarks>
public sealed class PostgreSqlBackupAdmissionPolicy(
    DbContextOptions<PlatformDbContext> options, QuotaOptions quota, ILogger<PostgreSqlBackupAdmissionPolicy> logger) : IBackupAdmissionPolicy, ITransactionBoundBackupAdmission
{
    public async Task<BackupAdmissionDecision> AdmitNewRunAsync(BackupAdmissionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var decision = await DecideAsync(context, request, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return decision;
    }

    /// <summary>The stores' path: the decision and its reservation join the transaction that creates the run.</summary>
    Task<BackupAdmissionDecision> ITransactionBoundBackupAdmission.AdmitAsync(PlatformDbContext context, BackupAdmissionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        return context.Database.CurrentTransaction is null
            ? throw new InvalidOperationException("Transaction-bound admission requires the caller's open transaction.")
            : DecideAsync(context, request, cancellationToken);
    }

    private async Task<BackupAdmissionDecision> DecideAsync(PlatformDbContext context, BackupAdmissionRequest request, CancellationToken cancellationToken)
    {
        if (!await TenantUsageEngine.LockTenantAsync(context, request.TenantId, cancellationToken))
        {
            return BackupAdmissionDecision.Denied(BackupAdmissionDenialCodes.SubscriptionRequired);
        }
        // Plan and quota system disabled for now: all backups are admitted unconditionally.
        logger.LogDebug("Admission granted for run {RunId} under tenant {TenantId} (quota TTL {Ttl}).", request.RunId, request.TenantId, quota.ReservationTimeToLive);
        return BackupAdmissionDecision.Admitted;
    }

    /// <summary>
    /// Decides the additional counted bytes of a repair. Without <paramref name="reserveOn"/> it only reads (an early answer); with it, the caller
    /// holds the tenant lock in its transaction, and an allowed decision sets the hold on the operation so the check and the reservation are one
    /// step. The operation's own current hold is excluded, so a repair that is re-admitted never competes with itself.
    /// </summary>
    internal static async Task<BackupAdmissionDecision> AdmitReplicaRepairAsync(
        PlatformDbContext context,
        TenantId tenantId,
        BackupArtifact artifact,
        string storageProvider,
        Guid storageConnectionId,
        StorageConnection? connection,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        ReplicaRepairOperation? reserveOn = null)
    {
        var entitlement = await PostgreSqlTenantEntitlementReader.ResolveAsync(context, tenantId, now, cancellationToken);
        var policy = await TenantUsageEngine.ResolvePolicyAsync(context, tenantId, cancellationToken);
        long additionalBytes = 0;

        if (policy.EnforcementMode != QuotaEnforcementMode.None && entitlement.StorageLimit is { IsUnlimited: false })
        {
            var ownership = connection?.Ownership ?? StorageOwnership.PlatformManaged;
            var quotaScope = connection?.QuotaScope ?? StorageQuotaScopes.DefaultFor(ownership);
            var targetFacts = new ReplicaStorageFacts(storageProvider, ownership, quotaScope, storageConnectionId);

            if (policy.AccountingMode == QuotaAccountingMode.PhysicalReplicaBytes)
            {
                if (policy.IncludedStorageScopes.Matches(targetFacts))
                    additionalBytes = artifact.SizeBytes;
            }
            else if (policy.AccountingMode == QuotaAccountingMode.LogicalArtifactBytes)
            {
                var connectionFacts = await TenantUsageEngine.LoadConnectionFactsAsync(context, tenantId, cancellationToken);
                var hasReplicaInScope = artifact.Replicas.Any(r =>
                    r.Status != BackupReplicaStatus.Deleted &&
                    connectionFacts.TryGetValue(r.StorageConnectionId, out var facts) &&
                    policy.IncludedStorageScopes.Matches(facts));

                if (!hasReplicaInScope && policy.IncludedStorageScopes.Matches(targetFacts))
                    additionalBytes = artifact.SizeBytes;
            }

            if (additionalBytes > 0)
            {
                var (usage, _) = await TenantUsageEngine.EnsureCurrentAsync(context, tenantId, policy, now, cancellationToken);
                var ownHold = reserveOn is not null && context.Entry(reserveOn).State != EntityState.Detached ? reserveOn.ReservedQuotaBytes : 0;
                var decision = QuotaAdmissionDecision.Decide(entitlement, policy, usage.UsedBytes, Math.Max(0, usage.ReservedBytes - ownHold), additionalBytes);
                if (!decision.Allowed)
                    return BackupAdmissionDecision.Denied(decision.DenialCode!);
            }
        }

        reserveOn?.ReserveQuota(additionalBytes);
        return BackupAdmissionDecision.Admitted;
    }

    public async Task ReleaseAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await TenantUsageEngine.LockTenantAsync(context, tenantId, cancellationToken))
        {
            return;
        }
        if (await TenantUsageEngine.ReleaseReservationAsync(context, tenantId, runId, QuotaReservation.ReasonNotCreated, DateTimeOffset.UtcNow, cancellationToken) > 0)
        {
            await TenantUsageEngine.RefreshReservedAsync(context, tenantId, cancellationToken);
            logger.LogInformation("Quota reservation released for tenant {TenantId}, run {RunId}: the run was not created.", tenantId, runId);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>History supplies only the artifact size; the current resolved destination snapshot supplies its counted copies.</summary>
    private static async Task<long?> EstimateAsync(PlatformDbContext context, BackupAdmissionRequest request, QuotaPolicy policy, CancellationToken cancellationToken)
    {
        var configuration = request.Configuration ?? await context.BackupJobs.AsNoTracking()
            .Where(job => job.TenantId == request.TenantId && job.Id == request.JobId)
            .Select(job => job.Configuration).SingleAsync(cancellationToken);
        var copies = await TenantUsageEngine.CountCopiesAsync(context, request.TenantId, configuration, policy, request.Now, cancellationToken);
        if (copies == 0) return 0;
        if (copies is null) return null;
        var size = await (from artifact in context.BackupArtifacts.AsNoTracking()
                          join attempt in context.BackupRunAttempts.AsNoTracking() on new { artifact.AttemptId, artifact.TenantId } equals new { AttemptId = attempt.Id, attempt.TenantId }
                          join run in context.BackupRuns.AsNoTracking() on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                          where run.TenantId == request.TenantId && run.JobId == request.JobId
                          orderby artifact.CreatedAt descending, artifact.Id descending
                          select (long?)artifact.SizeBytes).FirstOrDefaultAsync(cancellationToken);
        return size is not > 0 ? null : size > long.MaxValue / copies.Value ? long.MaxValue : size.Value * copies.Value;
    }
}
