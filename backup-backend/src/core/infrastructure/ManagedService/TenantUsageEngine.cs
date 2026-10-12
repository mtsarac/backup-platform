using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Storage;
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
    public static async Task<long> ComputeUsedAsync(PlatformDbContext context, TenantId tenantId, QuotaPolicy policy, CancellationToken cancellationToken, Guid? runId = null, Guid? excludeAttemptId = null)
    {
        var accumulator = QuotaAccounting.NewAccumulator(policy);
        var occupying = BackupReplicaStorage.OccupyingStatuses.ToArray();
        var rows = from replica in context.BackupReplicas.AsNoTracking()
                   where replica.TenantId == tenantId && occupying.Contains(replica.Status)
                   join artifact in context.BackupArtifacts.AsNoTracking() on new { replica.ArtifactId, replica.TenantId } equals new { ArtifactId = artifact.Id, artifact.TenantId }
                   where runId == null || context.BackupRunAttempts.Any(attempt => attempt.TenantId == tenantId && attempt.Id == artifact.AttemptId && attempt.RunId == runId)
                   where excludeAttemptId == null || artifact.AttemptId != excludeAttemptId
                   join connection in context.StorageConnections.AsNoTracking() on new { replica.StorageConnectionId, replica.TenantId } equals new { StorageConnectionId = connection.Id, connection.TenantId }
                   select new
                   {
                       replica.ArtifactId, ArtifactSize = artifact.SizeBytes, ReplicaSize = replica.SizeBytes, replica.Status,
                       connection.ProviderType, connection.Ownership, connection.QuotaScope, ConnectionId = connection.Id
                   };
        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            accumulator.Add(new ReplicaFact(row.ArtifactId, row.ArtifactSize, row.ReplicaSize, new ReplicaStorageFacts(row.ProviderType, row.Ownership, row.QuotaScope, row.ConnectionId), row.Status));
        }
        return accumulator.TotalBytes;
    }

    public static async Task<long> ComputeReservedAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken)
    {
        var policy = await ResolvePolicyAsync(context, tenantId, cancellationToken);
        var reservations = await context.QuotaReservations.AsNoTracking().Where(row => row.TenantId == tenantId && row.ReleasedAt == null)
            .Select(row => new { row.RunId, row.Bytes }).ToListAsync(cancellationToken);
        var total = 0L;
        foreach (var reservation in reservations)
        {
            var consumed = await ComputeUsedAsync(context, tenantId, policy, cancellationToken, reservation.RunId);
            var remaining = Math.Max(0, reservation.Bytes - consumed);
            total = total > long.MaxValue - remaining ? long.MaxValue : total + remaining;
        }
        // A repair's hold lives exactly as long as the operation is open, so reconcile and the save hook agree without a release step.
        var repairs = await context.ReplicaRepairOperations.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.ReservedQuotaBytes > 0
                && (row.Status == ReplicaRepairStatus.Pending || row.Status == ReplicaRepairStatus.InProgress))
            .Select(row => row.ReservedQuotaBytes).ToListAsync(cancellationToken);
        foreach (var held in repairs)
        {
            total = total > long.MaxValue - held ? long.MaxValue : total + held;
        }
        return total;
    }

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

    /// <summary>Only unspent reservation bytes remain reserved: copies already persisted are counted in UsedBytes.</summary>
    public static async Task RefreshReservedAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken)
    {
        var reserved = await ComputeReservedAsync(context, tenantId, cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE tenant_storage_usage SET "ReservedBytes" = {reserved}, "Revision" = "Revision" + 1
            WHERE "TenantId" = {tenantId.Value}
            """, cancellationToken);
    }

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
    /// A run can still be claimed or retried without admission while nonterminal, so its reservation cannot expire with its lease.
    /// Only abandoned reservations without a run expire; terminal runs and orphaned admissions are released below.
    /// </summary>
    public static async Task<int> ReleaseStaleReservationsAsync(PlatformDbContext context, TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var at = now.ToUniversalTime();
        var orphanedBefore = at - OrphanGrace;
        var released = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE quota_reservations q SET "ReleasedAt" = {at}, "ReleaseReason" = 'expired'
            WHERE q."TenantId" = {tenantId.Value} AND q."ReleasedAt" IS NULL AND q."ExpiresAt" <= {at}
              AND NOT EXISTS (SELECT 1 FROM backup_runs r
                              WHERE r."Id" = q."RunId" AND r."TenantId" = q."TenantId" AND r."Status" NOT IN (4, 5, 6, 7))
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

    internal static async Task<int?> CountCopiesAsync(
        PlatformDbContext context, TenantId tenantId, BackupConfiguration configuration, QuotaPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var copies = 0;
        foreach (var destination in configuration.ResolveEffectiveDestinations())
        {
            var connection = destination.StorageConnectionId is { } id
                ? await context.StorageConnections.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == id, cancellationToken)
                : await StorageConnectionProvisioner.ResolveDefaultAsync(context, tenantId, destination.StorageProvider, now, cancellationToken);
            if (connection is null) return null;
            if (!policy.IncludedStorageScopes.Matches(
                new ReplicaStorageFacts(connection.ProviderType, connection.Ownership, connection.QuotaScope, connection.Id))) continue;
            if (policy.AccountingMode == QuotaAccountingMode.LogicalArtifactBytes) return 1;
            copies++;
        }
        return copies;
    }

    /// <summary>The hard run's remaining reserved artifact bound; zero fails closed when its reservation is absent or exhausted.</summary>
    internal static async Task<long?> ReadRunMaxArtifactBytesAsync(
        PlatformDbContext context, BackupRun run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var policy = await ResolvePolicyAsync(context, run.TenantId, cancellationToken);
        var entitlement = await PostgreSqlTenantEntitlementReader.ResolveAsync(context, run.TenantId, now, cancellationToken);
        if (policy.EnforcementMode != QuotaEnforcementMode.BlockNewBackups || entitlement.StorageLimit is not { IsUnlimited: false }) return null;
        var copies = await CountCopiesAsync(context, run.TenantId, run.Configuration, policy, now, cancellationToken);
        if (copies == 0) return null;
        if (copies is null) return 0;
        var reserved = await context.QuotaReservations.AsNoTracking()
            .Where(row => row.TenantId == run.TenantId && row.RunId == run.Id && row.ReleasedAt == null)
            .Select(row => (long?)row.Bytes).SingleOrDefaultAsync(cancellationToken) ?? 0;
        var consumed = await ComputeUsedAsync(context, run.TenantId, policy, cancellationToken, run.Id, run.CurrentAttempt.Id);
        return Math.Max(0, reserved - consumed) / copies.Value;
    }

    internal static Task<bool> SupportsQuotaBoundedUploadAsync(
        PlatformDbContext context, TenantId tenantId, AgentId agentId, CancellationToken cancellationToken) =>
        context.AgentCapabilities.AsNoTracking().AnyAsync(record => record.TenantId == tenantId && record.AgentId == agentId
            && record.Identifier == BackupPlatform.Contracts.Backups.BackupTaskProtocol.QuotaBoundedUploadCapability
            && record.Support == BackupPlatform.Domain.Capabilities.CapabilitySupport.Supported, cancellationToken);

    /// <summary>What the scope selectors can see of each of the tenant's storage connections.</summary>
    public static async Task<Dictionary<Guid, ReplicaStorageFacts>> LoadConnectionFactsAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken) =>
        await context.StorageConnections.AsNoTracking().Where(connection => connection.TenantId == tenantId)
            .Select(connection => new { connection.Id, connection.ProviderType, connection.Ownership, connection.QuotaScope })
            .ToDictionaryAsync(connection => connection.Id, connection => new ReplicaStorageFacts(connection.ProviderType, connection.Ownership, connection.QuotaScope, connection.Id), cancellationToken);

    /// <summary>The replicas of one artifact as stored, ready for the accumulator.</summary>
    public static async Task<StoredArtifact?> ReadArtifactAsync(
        PlatformDbContext context, TenantId tenantId, Guid artifactId, CancellationToken cancellationToken)
    {
        var size = await context.BackupArtifacts.AsNoTracking().Where(artifact => artifact.TenantId == tenantId && artifact.Id == artifactId)
            .Select(artifact => (long?)artifact.SizeBytes).SingleOrDefaultAsync(cancellationToken);
        if (size is null)
        {
            return null;
        }
        var replicas = await context.BackupReplicas.AsNoTracking().Where(replica => replica.TenantId == tenantId && replica.ArtifactId == artifactId)
            .Select(replica => new StoredReplica(replica.Id, replica.SizeBytes, replica.StorageConnectionId, replica.Status))
            .ToListAsync(cancellationToken);
        return new StoredArtifact(artifactId, size.Value, replicas, await LoadConnectionFactsAsync(context, tenantId, cancellationToken));
    }
}

internal sealed record StoredReplica(Guid Id, long SizeBytes, Guid StorageConnectionId, BackupReplicaStatus Status);

/// <summary>One artifact's replicas with the connection facts needed to count them.</summary>
internal sealed record StoredArtifact(Guid ArtifactId, long ArtifactSize, List<StoredReplica> Replicas, IReadOnlyDictionary<Guid, ReplicaStorageFacts> ConnectionFacts)
{
    /// <summary>
    /// A replica whose connection is unknown (cannot happen while the foreign key holds) is counted as an unclassified connection, so it can only be
    /// matched by <see cref="QuotaScopeKind.All"/> and is never silently dropped from usage.
    /// </summary>
    public ReplicaFact ToFact(StoredReplica replica) => new(
        ArtifactId, ArtifactSize, replica.SizeBytes,
        ConnectionFacts.TryGetValue(replica.StorageConnectionId, out var facts)
            ? facts
            : new ReplicaStorageFacts(string.Empty, StorageOwnership.TenantManaged, string.Empty, replica.StorageConnectionId),
        replica.Status);
}
