using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Domain.Identity;
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
        var entitlement = await PostgreSqlTenantEntitlementReader.ResolveAsync(context, request.TenantId, request.Now, cancellationToken);
        if (entitlement.DenialCode(EntitlementOperation.StartNewBackupRun) is { } subscriptionCode)
        {
            logger.LogInformation("Backup admission denied for tenant {TenantId}: {Code} ({Status}), trigger {Trigger}, run {RunId}.",
                request.TenantId, subscriptionCode, entitlement.Status, request.TriggerType, request.RunId);
            return BackupAdmissionDecision.Denied(subscriptionCode);
        }
        var policy = await TenantUsageEngine.ResolvePolicyAsync(context, request.TenantId, cancellationToken);
        if (policy.EnforcementMode == QuotaEnforcementMode.None || entitlement.StorageLimit is not { IsUnlimited: false })
        {
            return BackupAdmissionDecision.Admitted;
        }

        if (await TenantUsageEngine.ReleaseStaleReservationsAsync(context, request.TenantId, request.Now, cancellationToken) > 0)
        {
            // The projection may not exist yet; EnsureCurrent below computes the reserved figure from the (already corrected) reservations.
            await TenantUsageEngine.RefreshReservedAsync(context, request.TenantId, cancellationToken);
        }
        var (usage, _) = await TenantUsageEngine.EnsureCurrentAsync(context, request.TenantId, policy, request.Now, cancellationToken);
        var estimate = await EstimateAsync(context, request, policy, cancellationToken);
        var decision = QuotaAdmissionDecision.Decide(entitlement, policy, usage.UsedBytes, usage.ReservedBytes, estimate);
        if (!decision.Allowed)
        {
            logger.LogWarning(
                "Backup admission denied for tenant {TenantId}: {Code}, state {State}, used {UsedBytes} + reserved {ReservedBytes} + estimated {EstimatedBytes} of limit {LimitBytes}, policy {PolicyKey} v{PolicyVersion}, trigger {Trigger}, run {RunId}.",
                request.TenantId, decision.DenialCode, decision.State, usage.UsedBytes, usage.ReservedBytes, estimate, entitlement.StorageLimit!.Value.Bytes,
                policy.Key, policy.Version, request.TriggerType, request.RunId);
            return BackupAdmissionDecision.Denied(decision.DenialCode!);
        }
        if (estimate > 0 && await TenantUsageEngine.InsertReservationAsync(context, request.TenantId, request.RunId, estimate, request.Now, quota.ReservationTimeToLive, cancellationToken) > 0)
        {
            await TenantUsageEngine.RefreshReservedAsync(context, request.TenantId, cancellationToken);
            logger.LogInformation("Quota reservation created for tenant {TenantId}, run {RunId}: {ReservedBytes} bytes (state {State}).",
                request.TenantId, request.RunId, estimate, decision.State);
        }
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

    /// <summary>What the run is expected to add: what the same job's most recent artifact contributes under the policy. No history, or a policy that does not count where the job stores, is 0.</summary>
    private static async Task<long> EstimateAsync(PlatformDbContext context, BackupAdmissionRequest request, QuotaPolicy policy, CancellationToken cancellationToken)
    {
        var previous = await (from artifact in context.BackupArtifacts.AsNoTracking()
                              join attempt in context.BackupRunAttempts.AsNoTracking() on new { artifact.AttemptId, artifact.TenantId } equals new { AttemptId = attempt.Id, attempt.TenantId }
                              join run in context.BackupRuns.AsNoTracking() on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                              where run.TenantId == request.TenantId && run.JobId == request.JobId
                              orderby artifact.CreatedAt descending, artifact.Id descending
                              select (Guid?)artifact.Id).FirstOrDefaultAsync(cancellationToken);
        if (previous is not { } artifactId)
        {
            return 0;
        }
        return await TenantUsageEngine.ReadArtifactAsync(context, request.TenantId, artifactId, cancellationToken) is { } stored
            ? QuotaAccounting.Estimate(policy, stored.Replicas.Select(stored.ToFact))
            : 0;
    }
}
