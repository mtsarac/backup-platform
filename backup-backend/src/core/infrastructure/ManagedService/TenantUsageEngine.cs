using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.ManagedService;

/// <summary>What the projection row holds for one tenant.</summary>
internal sealed record UsageRow(
    long UsedBytes, long ReservedBytes, QuotaPolicyId PolicyId, int PolicyVersion, QuotaAccountingMode AccountingMode, DateTimeOffset CalculatedAt, long Revision)
{
    public bool IsComputedUnder(QuotaPolicy policy) =>
        PolicyId == policy.Id && PolicyVersion == policy.Version && AccountingMode == policy.AccountingMode;
}

/// <summary>
/// Every statement that reads or writes the usage projection and its reservations. All of it runs on a context the caller already holds,
/// inside the caller's transaction and under the tenant lock taken by <see cref="LockTenantAsync"/>; nothing here opens a transaction of its own.
/// The counting rules are not here: they are <see cref="QuotaUsageAccumulator"/>'s, and this class only feeds it rows.
/// </summary>
/// <remarks>
/// Lock order, shared by every writer: agent row (backup lifecycle) → tenant row (this lock) → projection and reservation rows. Subscription
/// writers take the tenant row first and never an agent row while holding it, so the orders cannot cycle.
/// </remarks>
internal static class TenantUsageEngine
{
    /// <summary>A reservation whose run still does not exist this long after it was made was abandoned by a failed run creation.</summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(10);

    /// <summary>Serializes everything that decides or changes one tenant's usage. FOR NO KEY UPDATE does not block the foreign-key checks of unrelated inserts. False when the tenant does not exist.</summary>
    public static async Task<bool> LockTenantAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken)
    {
        var locked = await context.Database.SqlQuery<Guid>(
            $"SELECT \"Id\" AS \"Value\" FROM tenants WHERE \"Id\" = {tenantId.Value} FOR NO KEY UPDATE").ToListAsync(cancellationToken);
        return locked.Count == 1;
    }

    /// <summary>
    /// The policy that defines usage for the tenant: the one its current plan references when that policy is enabled, otherwise the default.
    /// A tenant without a subscription uses the default policy, so its usage is still tracked.
    /// </summary>
    public static async Task<QuotaPolicy> ResolvePolicyAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken)
    {
        var reference = await (from subscription in context.TenantSubscriptions.AsNoTracking()
                               where subscription.TenantId == tenantId
                               join plan in context.Plans.AsNoTracking() on subscription.PlanId equals plan.Id
                               orderby subscription.Status == SubscriptionStatus.Active || subscription.Status == SubscriptionStatus.Suspended ? 0 : 1,
                                   subscription.CreatedAt descending, subscription.Id descending
                               select plan.QuotaPolicyReference).FirstOrDefaultAsync(cancellationToken);
        var candidates = await context.QuotaPolicies.AsNoTracking()
            .Where(policy => policy.Enabled && (policy.IsDefault || (reference != null && policy.Key == reference)))
            .ToListAsync(cancellationToken);
        return ChoosePolicy(candidates, reference);
    }

    /// <summary>The one selection rule, shared by the per-tenant resolution and the bulk read: the referenced policy when it is among the enabled ones, otherwise the default.</summary>
    public static QuotaPolicy ChoosePolicy(IReadOnlyCollection<QuotaPolicy> enabledPolicies, string? reference) =>
        enabledPolicies.FirstOrDefault(policy => reference is not null && policy.Key == reference)
            ?? enabledPolicies.SingleOrDefault(policy => policy.IsDefault)
            ?? throw new InvalidOperationException("The default quota policy is missing.");

    public static async Task<UsageRow?> ReadAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken)
    {
        var usage = await context.TenantStorageUsages.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId, cancellationToken);
        return usage is null
            ? null
            : new UsageRow(usage.UsedBytes, usage.ReservedBytes, usage.PolicyId, usage.PolicyVersion, usage.AccountingMode, usage.CalculatedAt, usage.Revision);
    }

    /// <summary>Usage from the artifacts and replicas, counted by <paramref name="policy"/>. Streams the tenant's occupying replicas once; nothing is summed in SQL, so the rule exists only in the accumulator.</summary>
    public static async Task<long> ComputeUsedAsync(PlatformDbContext context, TenantId tenantId, QuotaPolicy policy, CancellationToken cancellationToken)
    {
        var accumulator = QuotaAccounting.NewAccumulator(policy);
        var occupying = BackupReplicaStorage.OccupyingStatuses.ToArray();
        var rows = from replica in context.BackupReplicas.AsNoTracking()
                   where replica.TenantId == tenantId && occupying.Contains(replica.Status)
                   join artifact in context.BackupArtifacts.AsNoTracking() on new { replica.ArtifactId, replica.TenantId } equals new { ArtifactId = artifact.Id, artifact.TenantId }
                   select new
                   {
                       replica.ArtifactId, ArtifactSize = artifact.SizeBytes, ReplicaSize = replica.SizeBytes,
                       replica.StorageProvider, replica.ExecutionLocation, replica.Status
                   };
        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            accumulator.Add(new ReplicaFact(row.ArtifactId, row.ArtifactSize, row.ReplicaSize, new ReplicaStorageFacts(row.StorageProvider, row.ExecutionLocation), row.Status));
        }
        return accumulator.TotalBytes;
    }

    public static async Task<long> ComputeReservedAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken) =>
        await context.QuotaReservations.AsNoTracking().Where(row => row.TenantId == tenantId && row.ReleasedAt == null)
            .SumAsync(row => (long?)row.Bytes, cancellationToken) ?? 0;

    /// <summary>Replaces the projection with the given figures (insert or overwrite). Idempotent.</summary>
    public static Task WriteAsync(
        PlatformDbContext context, TenantId tenantId, QuotaPolicy policy, long usedBytes, long reservedBytes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var accounting = (int)policy.AccountingMode;
        var policyId = policy.Id.Value;
        var calculatedAt = now.ToUniversalTime();
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO tenant_storage_usage ("TenantId", "UsedBytes", "ReservedBytes", "CalculatedAt", "AccountingMode", "PolicyId", "PolicyVersion", "Revision")
            VALUES ({tenantId.Value}, {usedBytes}, {reservedBytes}, {calculatedAt}, {accounting}, {policyId}, {policy.Version}, 0)
            ON CONFLICT ("TenantId") DO UPDATE SET
                "UsedBytes" = EXCLUDED."UsedBytes", "ReservedBytes" = EXCLUDED."ReservedBytes", "CalculatedAt" = EXCLUDED."CalculatedAt",
                "AccountingMode" = EXCLUDED."AccountingMode", "PolicyId" = EXCLUDED."PolicyId", "PolicyVersion" = EXCLUDED."PolicyVersion",
                "Revision" = tenant_storage_usage."Revision" + 1
            """, cancellationToken);
    }

    /// <summary>Marks the projection as just verified against its sources without changing a figure.</summary>
    public static Task TouchAsync(PlatformDbContext context, TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var calculatedAt = now.ToUniversalTime();
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tenant_storage_usage SET \"CalculatedAt\" = {calculatedAt} WHERE \"TenantId\" = {tenantId.Value}", cancellationToken);
    }

    /// <summary>
    /// The projection as it must be to be trusted: a missing row, or one computed under another policy or version, is recomputed from the
    /// sources first. Requires the tenant lock.
    /// </summary>
    public static async Task<(UsageRow Row, bool Recomputed)> EnsureCurrentAsync(
        PlatformDbContext context, TenantId tenantId, QuotaPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await ReadAsync(context, tenantId, cancellationToken);
        if (row is not null && row.IsComputedUnder(policy))
        {
            return (row, false);
        }
        var used = await ComputeUsedAsync(context, tenantId, policy, cancellationToken);
        var reserved = await ComputeReservedAsync(context, tenantId, cancellationToken);
        await WriteAsync(context, tenantId, policy, used, reserved, now, cancellationToken);
        return ((await ReadAsync(context, tenantId, cancellationToken))!, true);
    }

    /// <summary>Adds a signed difference to UsedBytes, saturating at both ends of the valid range (a drifted counter never blocks the change that discovers it). Idempotence is the caller's: it passes a difference derived from stored vs saved state.</summary>
    public static Task AdjustUsedAsync(PlatformDbContext context, TenantId tenantId, long delta, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE tenant_storage_usage
            SET "UsedBytes" = GREATEST(0, LEAST("UsedBytes"::numeric + {delta}::numeric, 9223372036854775807))::bigint, "Revision" = "Revision" + 1
            WHERE "TenantId" = {tenantId.Value}
            """, cancellationToken);

    /// <summary>ReservedBytes is always the sum of the unreleased reservations, so the figure is recomputed from them rather than adjusted.</summary>
    public static Task RefreshReservedAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE tenant_storage_usage
            SET "ReservedBytes" = COALESCE((SELECT SUM(r."Bytes") FROM quota_reservations r WHERE r."TenantId" = {tenantId.Value} AND r."ReleasedAt" IS NULL), 0),
                "Revision" = "Revision" + 1
            WHERE "TenantId" = {tenantId.Value}
            """, cancellationToken);

    public static Task<int> InsertReservationAsync(
        PlatformDbContext context, TenantId tenantId, Guid runId, long bytes, DateTimeOffset now, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        var createdAt = now.ToUniversalTime();
        var expiresAt = createdAt + timeToLive;
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO quota_reservations ("RunId", "TenantId", "Bytes", "CreatedAt", "ExpiresAt")
            VALUES ({runId}, {tenantId.Value}, {bytes}, {createdAt}, {expiresAt})
            ON CONFLICT ("RunId") DO NOTHING
            """, cancellationToken);
    }

    /// <summary>Releases one run's reservation. Idempotent: an already released or unknown run changes nothing. Returns the rows released.</summary>
    public static Task<int> ReleaseReservationAsync(
        PlatformDbContext context, TenantId tenantId, Guid runId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var releasedAt = now.ToUniversalTime();
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE quota_reservations SET "ReleasedAt" = {releasedAt}, "ReleaseReason" = {reason}
            WHERE "RunId" = {runId} AND "TenantId" = {tenantId.Value} AND "ReleasedAt" IS NULL
            """, cancellationToken);
    }

    /// <summary>
    /// Releases reservations nothing will ever release: expired ones, ones of a run that already ended (a missed callback), and ones of a run that was
    /// never created. A reservation past its time-to-live is kept while its run holds a live lease (Leased or Running attempt whose lease has not run
    /// out): a renewing agent is proof the run is alive. The exemption is bounded by that lease, so a run that stops renewing is released again by the
    /// next call. Returns how many were released; the caller refreshes ReservedBytes.
    /// </summary>
    public static async Task<int> ReleaseStaleReservationsAsync(PlatformDbContext context, TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var at = now.ToUniversalTime();
        var orphanedBefore = at - OrphanGrace;
        var released = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE quota_reservations q SET "ReleasedAt" = {at}, "ReleaseReason" = 'expired'
            WHERE q."TenantId" = {tenantId.Value} AND q."ReleasedAt" IS NULL AND q."ExpiresAt" <= {at}
              AND NOT EXISTS (SELECT 1 FROM backup_run_attempts a
                              WHERE a."RunId" = q."RunId" AND a."TenantId" = q."TenantId" AND a."Status" IN (2, 3) AND a."LeaseExpiresAt" > {at})
            """, cancellationToken);
        released += await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE quota_reservations q SET "ReleasedAt" = {at}, "ReleaseReason" = 'run_finished'
            FROM backup_runs r
            WHERE q."TenantId" = {tenantId.Value} AND q."ReleasedAt" IS NULL AND r."Id" = q."RunId" AND r."TenantId" = q."TenantId" AND r."Status" IN (4, 5, 6, 7)
            """, cancellationToken);
        released += await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE quota_reservations q SET "ReleasedAt" = {at}, "ReleaseReason" = 'orphaned'
            WHERE q."TenantId" = {tenantId.Value} AND q."ReleasedAt" IS NULL AND q."CreatedAt" <= {orphanedBefore}
              AND NOT EXISTS (SELECT 1 FROM backup_runs r WHERE r."Id" = q."RunId" AND r."TenantId" = q."TenantId")
            """, cancellationToken);
        return released;
    }

    /// <summary>The replicas of one artifact as stored, ready for the accumulator.</summary>
    public static async Task<(long ArtifactSize, List<StoredReplica> Replicas)?> ReadArtifactAsync(
        PlatformDbContext context, TenantId tenantId, Guid artifactId, CancellationToken cancellationToken)
    {
        var size = await context.BackupArtifacts.AsNoTracking().Where(artifact => artifact.TenantId == tenantId && artifact.Id == artifactId)
            .Select(artifact => (long?)artifact.SizeBytes).SingleOrDefaultAsync(cancellationToken);
        if (size is null)
        {
            return null;
        }
        var replicas = await context.BackupReplicas.AsNoTracking().Where(replica => replica.TenantId == tenantId && replica.ArtifactId == artifactId)
            .Select(replica => new StoredReplica(replica.Id, replica.SizeBytes, replica.StorageProvider, replica.ExecutionLocation, replica.Status))
            .ToListAsync(cancellationToken);
        return (size.Value, replicas);
    }
}

internal sealed record StoredReplica(Guid Id, long SizeBytes, string StorageProvider, BackupStorageExecutionLocation ExecutionLocation, BackupReplicaStatus Status)
{
    public ReplicaFact ToFact(Guid artifactId, long artifactSize) =>
        new(artifactId, artifactSize, SizeBytes, new ReplicaStorageFacts(StorageProvider, ExecutionLocation), Status);
}
