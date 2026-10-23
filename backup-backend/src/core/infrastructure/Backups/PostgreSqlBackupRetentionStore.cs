using System.Security.Cryptography;
using System.Text;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>
/// Every decision is made by the database, never by process memory, so any number of backend nodes can run the retention workers at once.
/// Artifact-level changes (the retention scan, a manual delete, a cancel) serialise on the artifact row lock; the delete workers claim
/// replica rows with <c>FOR UPDATE SKIP LOCKED</c> and never touch the artifact lock, so the two lock orders cannot cycle. Replica rows carry
/// the PostgreSQL row version as concurrency token: a cancel that read a replica as DeletePending can therefore never overwrite a claim that
/// committed in between, it fails and is re-run against the committed truth.
/// </summary>
public sealed class PostgreSqlBackupRetentionStore(DbContextOptions<PlatformDbContext> options, BackupRetentionOptions? retention = null) : IBackupRetentionStore
{
    private readonly BackupRetentionOptions retentionOptions = retention ?? new BackupRetentionOptions();
    private const int MaxScanBatch = 1000;
    private const int MaxClaimBatch = 500;
    private const int MaxListLimit = 200;
    private const int LookupChunk = 500;
    private const int MaxPrunePerCall = 10_000;
    // A mutation re-runs when a delete worker changed a replica between the read and the write; the lock keeps every other writer out.
    private const int MutationAttempts = 3;

    public async Task<BackupRetentionApplication> ApplyExpiredRetentionAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var scan = PlatformDbContext.CreateForPlatformOperations(options);
        // Unlocked and possibly stale on purpose: it only nominates candidates, the locked re-read below decides.
        var candidates = await scan.BackupArtifacts.AsNoTracking()
            .Where(artifact => artifact.RetentionExpiresAt <= now && artifact.RetentionPinnedAt == null && artifact.RetentionAppliedAt == null)
            .OrderBy(artifact => artifact.RetentionExpiresAt).ThenBy(artifact => artifact.Id).Take(Math.Clamp(limit, 1, MaxScanBatch))
            .Select(artifact => new { artifact.TenantId, artifact.Id }).ToListAsync(cancellationToken);
        var moved = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var context = PlatformDbContext.CreateForPlatformOperations(options);
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                // Somebody else (another node's scan, a manual delete, a cancel) owns the artifact right now: it is either handled by them or
                // picked up by the next scan, never worth waiting for.
                if (!await LockArtifactAsync(context, candidate.TenantId, candidate.Id, skipLocked: true, cancellationToken)) continue;
                var artifact = await context.BackupArtifacts.Include(candidateArtifact => candidateArtifact.Replicas)
                    .SingleOrDefaultAsync(entity => entity.TenantId == candidate.TenantId && entity.Id == candidate.Id, cancellationToken);
                if (artifact is null) continue;
                // The domain decides expiry and pin again, now that nobody can change the artifact under it.
                var previouslyApplied = artifact.RetentionAppliedAt;
                var applied = artifact.ApplyRetention(now);
                if (previouslyApplied is null && artifact.RetentionAppliedAt is not null)
                    context.AuditEvents.Add(AuditEvent.Record(
                        now, UserId.System, AuditActorKind.System, AuditActions.BackupArtifactRetentionApplied, artifact.TenantId,
                        "backup_artifact", artifact.Id.ToString("D"),
                        new Dictionary<string, object?>
                        {
                            ["result"] = applied > 0 ? "delete_pending" : "no_available_replicas",
                            ["reason"] = "RetentionExpired",
                            ["replicasMoved"] = applied
                        }));
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                moved += applied;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A claim or another writer won this artifact; the next scan sees the committed state.
            }
            catch (DbUpdateException exception) when (PostgreSqlBackupRunStore.IsUniqueViolation(exception))
            {
                // Same: a competing writer got there first; one artifact never fails the batch.
            }
        }
        return new BackupRetentionApplication(candidates.Count, moved);
    }

    public async Task<IReadOnlyList<ClaimedReplicaDeletion>> ClaimDueDeletionsAsync(string owner, DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken,
        bool dispatchExternalToAgents = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        now = now.ToUniversalTime();
        var take = Math.Clamp(limit, 1, MaxClaimBatch);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // The locks are taken by this statement; a row another claimer (or a cancel) holds is skipped, never waited for, so no replica is handed out twice.
        // A replica a restore has pinned is not a candidate at all, so a pinned replica whose delete is long due cannot crowd the batch.
        var ids = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_replicas
            WHERE (("Status" = 'DeletePending' AND "DeleteAfter" <= {now})
               OR ("Status" = 'DeleteFailed' AND "DeleteNextAttemptAt" <= {now})
               OR ("Status" = 'Deleting' AND ("DeleteLeaseExpiresAt" <= {now}
                   OR ("DeleteLeaseToken" IS NULL AND "DeleteNextAttemptAt" <= {now}
                       AND ("ExecutionLocation" = 'Agent' OR ({dispatchExternalToAgents} AND "StorageProvider" IN ('sftp', 'google-drive')))))))
              AND NOT EXISTS (SELECT 1 FROM restore_replica_pins pin WHERE pin."ReplicaId" = backup_replicas."Id" AND pin."ReleasedAt" IS NULL)
              AND NOT EXISTS (SELECT 1 FROM replica_repair_operations repair WHERE repair."SourceReplicaId" = backup_replicas."Id" AND repair."TenantId" = backup_replicas."TenantId" AND repair."Status" IN ('Pending', 'InProgress'))
              AND NOT EXISTS (SELECT 1 FROM backup_artifacts artifact JOIN backup_run_attempts attempt
                  ON attempt."Id" = artifact."AttemptId" AND attempt."TenantId" = artifact."TenantId"
                  WHERE artifact."Id" = backup_replicas."ArtifactId" AND artifact."TenantId" = backup_replicas."TenantId" AND attempt."Status" IN (1, 2, 3, 9))
              AND NOT EXISTS (SELECT 1 FROM download_sessions hold WHERE hold."ReplicaId" = backup_replicas."Id" AND hold."TenantId" = backup_replicas."TenantId"
                  AND hold."ReleasedAt" IS NULL AND hold."ExpiresAt" > {now} AND (hold."LeaseExpiresAt" IS NULL OR hold."LeaseExpiresAt" > {now}))
            ORDER BY CASE "Status" WHEN 'DeletePending' THEN "DeleteAfter" WHEN 'DeleteFailed' THEN "DeleteNextAttemptAt" ELSE COALESCE("DeleteLeaseExpiresAt", "DeleteNextAttemptAt") END, "Id"
            LIMIT {take}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
        if (ids.Count == 0) return [];
        // The check above ran on that statement's snapshot, which can predate a pin a restore committed right before it released its own lock on the row (a restore locks the
        // replica, inserts the pin and commits; it never changes the replica row, so this statement does not re-evaluate it). Now that this transaction holds the row locks, no
        // restore can pin any of these replicas until it ends: a pin that exists now is visible to a NEW statement, and a replica carrying one is left alone.
        var pinned = await context.Database.SqlQuery<Guid>($"""
            SELECT "ReplicaId" AS "Value" FROM restore_replica_pins WHERE "ReleasedAt" IS NULL AND "ReplicaId" = ANY({ids.ToArray()})
            UNION
            SELECT "ReplicaId" AS "Value" FROM download_sessions WHERE "ReleasedAt" IS NULL AND "ExpiresAt" > {now}
                AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" > {now}) AND "ReplicaId" = ANY({ids.ToArray()})
                UNION
                SELECT "SourceReplicaId" AS "Value" FROM replica_repair_operations WHERE "Status" IN ('Pending', 'InProgress') AND "SourceReplicaId" = ANY({ids.ToArray()})
            """).ToListAsync(cancellationToken);
        if (pinned.Count != 0) ids = [.. ids.Where(id => !pinned.Contains(id))];
        if (ids.Count == 0) return [];
        var position = ids.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index);
        var replicas = await context.BackupReplicas.Where(replica => ids.Contains(replica.Id)).ToListAsync(cancellationToken);
        var claimed = new List<ClaimedReplicaDeletion>(replicas.Count);
        var changed = false;
        foreach (var replica in replicas.OrderBy(replica => position[replica.Id]))
        {
            if (replica.ExecutionLocation == BackupStorageExecutionLocation.Backend && replica.StorageProvider is "sftp" or "google-drive" or "s3" or "smb")
            {
                // A timed-out attempt can still have a remote PUT in flight; do not publish absence ahead of that writer.
                // Taken for this transaction only (the row stays locked until commit, so a writer waits for it): a live writer lease means skip.
                var transferLock = BackupTransferLock.Id(replica.StorageConnectionId, replica.Key);
                if (!await BackupTransferLeases.TryTakeAsync(context, transferLock, Guid.NewGuid(), TimeSpan.Zero, cancellationToken)) continue;
            }
            if (replica.ExecutionLocation == BackupStorageExecutionLocation.Agent || dispatchExternalToAgents && replica.AgentExecuted)
            {
                changed |= replica.DispatchAgentDeletion(replica.DeleteOperationId ?? Guid.NewGuid(), now,
                    now + retentionOptions.DeleteRetryDelay(Math.Max(1, replica.DeleteAttempts)));
                continue;
            }
            // The domain re-validates due-ness on the locked, freshly read row.
            if (!replica.BeginDelete(Guid.NewGuid(), owner, now, lease)) continue;
            changed = true;
            claimed.Add(new ClaimedReplicaDeletion(replica.Id, replica.TenantId, replica.ArtifactId, replica.StorageProvider, replica.StorageTarget,
                replica.Key, replica.DeleteLeaseToken!.Value, replica.DeleteAttempts));
        }
        if (!changed) return [];
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task<bool> CompleteDeletionAsync(Guid replicaId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var replica = await context.BackupReplicas.SingleOrDefaultAsync(entity => entity.Id == replicaId, cancellationToken);
        if (replica is null || !replica.CompleteDelete(leaseToken, now)) return false;
        if (replica.StorageProvider == "google-drive")
        {
            await context.GoogleDriveFileMetadata.Where(m =>
                m.TenantId == replica.TenantId && m.ConnectionId == replica.StorageConnectionId && m.StorageKey == replica.Key)
                .ExecuteDeleteAsync(cancellationToken);
        }
        context.AuditEvents.Add(AuditEvent.Record(
            now, UserId.System, AuditActorKind.System, AuditActions.BackupReplicaPhysicalDeleteCompleted, replica.TenantId,
            "backup_replica", replicaId.ToString("D"),
            new Dictionary<string, object?>
            {
                ["artifactId"] = replica.ArtifactId.ToString("D"),
                ["storageProvider"] = SafeStorageProvider(replica.StorageProvider),
                ["storageConnectionId"] = replica.StorageConnectionId.ToString("D"),
                ["result"] = "deleted",
                ["absenceVerified"] = true,
                ["attempt"] = replica.DeleteAttempts
            }));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> FailDeletionAsync(Guid replicaId, Guid leaseToken, DateTimeOffset now, string failureCode, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var replica = await context.BackupReplicas.SingleOrDefaultAsync(entity => entity.Id == replicaId, cancellationToken);
        if (replica is null || !replica.FailDelete(leaseToken, now, failureCode, nextAttemptAt)) return false;
        var safeCode = failureCode is BackupDeleteFailureCodes.StorageNotConfigured or BackupDeleteFailureCodes.DeleteNotSupported or
            BackupDeleteFailureCodes.InvalidLocation or BackupDeleteFailureCodes.AccessDenied or BackupDeleteFailureCodes.StorageUnavailable or
            BackupDeleteFailureCodes.IoError or BackupDeleteFailureCodes.DeleteNotVerified or BackupDeleteFailureCodes.Unexpected
            ? failureCode : BackupDeleteFailureCodes.Unexpected;
        context.AuditEvents.Add(AuditEvent.Record(
            now, UserId.System, AuditActorKind.System, AuditActions.BackupReplicaPhysicalDeleteFailed, replica.TenantId,
            "backup_replica", replicaId.ToString("D"),
            new Dictionary<string, object?>
            {
                ["artifactId"] = replica.ArtifactId.ToString("D"),
                ["result"] = "retry_scheduled",
                ["absenceVerified"] = false,
                ["failureCode"] = safeCode,
                ["attempt"] = replica.DeleteAttempts,
                ["nextAttemptAt"] = replica.DeleteNextAttemptAt
            }));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<BackupArtifactDeleteResult> RequestDeleteAsync(TenantId tenantId, Guid artifactId, Guid? requestedByUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = await MutateArtifactAsync(tenantId, artifactId, artifact => artifact.RequestDelete(now, requestedByUserId), cancellationToken,
            (context, artifact, outcome) =>
            {
                if (outcome != BackupArtifactDeleteOutcome.Requested) return;
                var actorId = requestedByUserId.HasValue ? new UserId(requestedByUserId.Value) : UserId.System;
                var actorKind = requestedByUserId.HasValue ? AuditActorKind.TenantAdministrator : AuditActorKind.System;
                context.AuditEvents.Add(AuditEvent.Record(
                    now, actorId, actorKind, AuditActions.BackupArtifactDeleteRequested, tenantId,
                    "backup_artifact", artifactId.ToString("D"),
                    new Dictionary<string, object?> { ["result"] = "delete_pending", ["reason"] = "Manual" }));
            });
        if (result is null) return new BackupArtifactDeleteResult(BackupArtifactDeleteKind.NotFound, null);
        var kind = result.Outcome switch
        {
            BackupArtifactDeleteOutcome.Requested => BackupArtifactDeleteKind.Requested,
            BackupArtifactDeleteOutcome.AlreadyDeleted => BackupArtifactDeleteKind.AlreadyDeleted,
            _ => BackupArtifactDeleteKind.AlreadyInProgress
        };
        return new BackupArtifactDeleteResult(kind, result.Record);
    }

    public async Task<BackupArtifactCancelResult> CancelDeleteAsync(TenantId tenantId, Guid artifactId, DateTimeOffset now, CancellationToken cancellationToken, Guid? requestedByUserId = null)
    {
        var result = await MutateArtifactAsync(tenantId, artifactId, artifact => artifact.CancelDelete(now), cancellationToken,
            (context, artifact, restored) =>
            {
                if (restored == 0) return;
                var actorId = requestedByUserId.HasValue ? new UserId(requestedByUserId.Value) : UserId.System;
                var actorKind = requestedByUserId.HasValue ? AuditActorKind.TenantAdministrator : AuditActorKind.System;
                context.AuditEvents.Add(AuditEvent.Record(
                    now, actorId, actorKind, AuditActions.BackupArtifactDeleteCancelled, tenantId,
                    "backup_artifact", artifactId.ToString("D"),
                    new Dictionary<string, object?> { ["result"] = "restored", ["replicasRestored"] = restored }));
            });
        if (result is null) return new BackupArtifactCancelResult(BackupArtifactCancelKind.NotFound, null);
        return new BackupArtifactCancelResult(result.Outcome > 0 ? BackupArtifactCancelKind.Restored : BackupArtifactCancelKind.NothingToCancel, result.Record);
    }

    public async Task<BackupArtifactRecord?> GetArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var artifact = await context.BackupArtifacts.AsNoTracking().Include(entity => entity.Replicas)
            .SingleOrDefaultAsync(entity => entity.TenantId == tenantId && entity.Id == artifactId, cancellationToken);
        if (artifact is null) return null;
        var owners = await ResolveOwnersAsync(context, tenantId, [artifact.AttemptId], cancellationToken);
        return ToRecord(artifact, owners);
    }

    public async Task<IReadOnlyList<BackupArtifactRecord>> ListArtifactsAsync(TenantId tenantId, Guid? jobId, int limit, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var artifacts = await context.BackupArtifacts.AsNoTracking().Include(entity => entity.Replicas)
            .Where(entity => entity.TenantId == tenantId && (jobId == null || context.BackupRunAttempts.Any(attempt =>
                attempt.Id == entity.AttemptId && attempt.TenantId == entity.TenantId && context.BackupRuns.Any(run =>
                    run.Id == attempt.RunId && run.TenantId == attempt.TenantId && run.JobId == jobId))))
            .OrderByDescending(entity => entity.AcceptedAt ?? entity.CreatedAt).ThenBy(entity => entity.Id)
            .Take(Math.Clamp(limit, 1, MaxListLimit)).AsSplitQuery().ToListAsync(cancellationToken);
        var owners = await ResolveOwnersAsync(context, tenantId, artifacts.Select(artifact => artifact.AttemptId).Distinct().ToArray(), cancellationToken);
        return artifacts.Select(artifact => ToRecord(artifact, owners)).ToList();
    }

    public async Task<BackupArtifactPage<BackupArtifactRecord>> QueryArtifactsAsync(TenantId tenantId, BackupArtifactQuery query, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var artifacts = context.BackupArtifacts.AsNoTracking().Where(artifact => artifact.TenantId == tenantId);
        if (query.JobId is not null || query.AgentId is not null)
            artifacts = artifacts.Where(artifact => context.BackupRunAttempts.Any(attempt =>
                attempt.Id == artifact.AttemptId && attempt.TenantId == tenantId && context.BackupRuns.Any(run =>
                    run.Id == attempt.RunId && run.TenantId == tenantId &&
                    (query.JobId == null || run.JobId == query.JobId) &&
                    (query.AgentId == null || run.AgentId == new AgentId(query.AgentId.Value)))));
        if (query.From is { } from) artifacts = artifacts.Where(artifact => artifact.CreatedAt >= from.ToUniversalTime());
        if (query.To is { } to) artifacts = artifacts.Where(artifact => artifact.CreatedAt <= to.ToUniversalTime());
        artifacts = FilterState(artifacts, query.State);
        var totalCount = await artifacts.LongCountAsync(cancellationToken);
        var offset = (long)(query.Page - 1) * query.PageSize;
        if (offset >= totalCount) return new([], totalCount, query.Page, query.PageSize);
        var rows = await artifacts.Include(artifact => artifact.Replicas)
            .OrderByDescending(artifact => artifact.AcceptedAt ?? artifact.CreatedAt).ThenBy(artifact => artifact.Id)
            .Skip(checked((int)offset)).Take(query.PageSize).AsSplitQuery().ToListAsync(cancellationToken);
        var owners = await ResolveOwnersAsync(context, tenantId, rows.Select(artifact => artifact.AttemptId).Distinct().ToArray(), cancellationToken);
        return new(rows.Select(artifact => ToRecord(artifact, owners)).ToList(), totalCount, query.Page, query.PageSize);
    }

    private static IQueryable<BackupArtifact> FilterState(IQueryable<BackupArtifact> artifacts, string? state) => state switch
    {
        "Available" => artifacts.Where(artifact => artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.Available)),
        "Deleted" => artifacts.Where(artifact => artifact.Replicas.Any() && artifact.Replicas.All(replica => replica.Status == BackupReplicaStatus.Deleted)),
        "DeleteFailed" => artifacts.Where(artifact => !artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.Available) &&
            artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.DeleteFailed && replica.DeleteFailureCode != null)),
        "Deleting" => artifacts.Where(artifact => !artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.Available ||
            replica.Status == BackupReplicaStatus.DeleteFailed && replica.DeleteFailureCode != null) &&
            artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.Deleting)),
        "DeletePending" => artifacts.Where(artifact => !artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.Available ||
            replica.Status == BackupReplicaStatus.Deleting || replica.Status == BackupReplicaStatus.DeleteFailed && replica.DeleteFailureCode != null) &&
            (!artifact.Replicas.Any() || artifact.Replicas.Any(replica => replica.Status != BackupReplicaStatus.Deleted))),
        _ => artifacts
    };

    public async Task<IReadOnlySet<string>> FindReferencedKeysAsync(string storageProvider, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        if (keys.Count == 0) return referenced;
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        foreach (var chunk in keys.Distinct(StringComparer.Ordinal).Chunk(LookupChunk))
        {
            // Any tenant, any status: a file a Deleted replica still names was removed on purpose and must not be mistaken for an orphan of another run.
            var found = await context.BackupReplicas.AsNoTracking()
                .Where(replica => replica.StorageProvider == storageProvider && chunk.Contains(replica.Key))
                .Select(replica => replica.Key).Distinct().ToListAsync(cancellationToken);
            referenced.UnionWith(found);
            var targets = await context.ReplicaRepairOperations.AsNoTracking()
                .Where(repair => repair.StorageProvider == storageProvider && repair.TargetKey != null && chunk.Contains(repair.TargetKey)
                    && (repair.Status == ReplicaRepairStatus.Pending || repair.Status == ReplicaRepairStatus.InProgress))
                .Select(repair => repair.TargetKey!).ToListAsync(cancellationToken);
            referenced.UnionWith(targets);
        }
        return referenced;
    }

    public async Task<IReadOnlyDictionary<Guid, BackupAttemptActivity>> GetAttemptActivityAsync(IReadOnlyCollection<Guid> attemptIds, CancellationToken cancellationToken)
    {
        var activity = new Dictionary<Guid, BackupAttemptActivity>();
        if (attemptIds.Count == 0) return activity;
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        foreach (var chunk in attemptIds.Distinct().Chunk(LookupChunk))
        {
            var rows = await (from attempt in context.BackupRunAttempts.AsNoTracking()
                              join run in context.BackupRuns.AsNoTracking() on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                              where chunk.Contains(attempt.Id)
                              select new
                              {
                                  AttemptId = attempt.Id, attempt.TenantId, run.JobId, attempt.RunId,
                                  AttemptStatus = attempt.Status, RunStatus = run.Status,
                                  RepairIsActive = context.ReplicaRepairOperations.Any(repair => repair.TenantId == attempt.TenantId
                                      && (repair.Status == ReplicaRepairStatus.Pending || repair.Status == ReplicaRepairStatus.InProgress)
                                      && context.BackupArtifacts.Any(artifact => artifact.TenantId == attempt.TenantId
                                          && artifact.Id == repair.ArtifactId && artifact.AttemptId == attempt.Id))
                              })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
                activity[row.AttemptId] = new BackupAttemptActivity(row.AttemptId, row.TenantId, row.JobId, row.RunId, row.AttemptStatus, row.RunStatus)
                {
                    RepairIsActive = row.RepairIsActive
                };
        }
        return activity;
    }

    public async Task<int> PruneScheduleOccurrencesAsync(DateTimeOffset decidedBefore, int limit, CancellationToken cancellationToken)
    {
        var cutoff = decidedBefore.ToUniversalTime();
        var take = Math.Clamp(limit, 1, MaxPrunePerCall);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        // One statement, so eligibility cannot go stale. A row is kept while it is the newest of its job (the scheduler's watermark: it decides
        // which instants were already handled) and while the run it names is still in flight. Statuses 4..7 are the terminal ones.
        return await context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM backup_schedule_occurrences
            WHERE "Id" IN (
                SELECT o."Id" FROM backup_schedule_occurrences o
                WHERE o."DecidedAt" < {cutoff}
                  AND EXISTS (SELECT 1 FROM backup_schedule_occurrences newer
                              WHERE newer."TenantId" = o."TenantId" AND newer."JobId" = o."JobId" AND newer."ScheduledAt" > o."ScheduledAt")
                  AND NOT EXISTS (SELECT 1 FROM backup_runs r
                                  WHERE r."Id" = o."RunId" AND r."TenantId" = o."TenantId" AND r."Status" NOT IN (4, 5, 6, 7))
                ORDER BY o."DecidedAt", o."Id"
                LIMIT {take})
            """, cancellationToken);
    }

    private sealed record Mutation<TOutcome>(TOutcome Outcome, BackupArtifactRecord Record);

    /// <summary>
    /// Runs <paramref name="mutate"/> on the artifact under its row lock. A <see cref="DbUpdateConcurrencyException"/> means a delete worker
    /// claimed a replica between the read and the write; the transaction is rolled back and re-run from a fresh read so the outcome reflects what
    /// committed (a cancel then finds nothing left to cancel). Null: no such artifact in this tenant.
    /// </summary>
    private async Task<Mutation<TOutcome>?> MutateArtifactAsync<TOutcome>(TenantId tenantId, Guid artifactId, Func<BackupArtifact, TOutcome> mutate, CancellationToken cancellationToken, Action<PlatformDbContext, BackupArtifact, TOutcome>? onCommit = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var context = PlatformDbContext.CreateForPlatformOperations(options);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (!await LockArtifactAsync(context, tenantId, artifactId, skipLocked: false, cancellationToken)) return null;
            var artifact = await context.BackupArtifacts.Include(entity => entity.Replicas)
                .SingleOrDefaultAsync(entity => entity.TenantId == tenantId && entity.Id == artifactId, cancellationToken);
            if (artifact is null) return null;
            var outcome = mutate(artifact);
            onCommit?.Invoke(context, artifact, outcome);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MutationAttempts)
            {
                continue;
            }
            await transaction.CommitAsync(cancellationToken);
            var owners = await ResolveOwnersAsync(context, tenantId, [artifact.AttemptId], cancellationToken);
            return new Mutation<TOutcome>(outcome, ToRecord(artifact, owners));
        }
    }

    private static string SafeStorageProvider(string provider) => provider is "local" or "central" or "sftp" or "google-drive" or "s3" or "smb" ? provider : "other";

    public async Task<ClaimedStorageCleanup?> BeginSweptDeletionAsync(
        string storageProvider, string storageName, TenantId? storageScope, ManagedStorageKey key, string objectKey,
        DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageName);
        if (!ManagedBackupLayout.TryParseStorageKey(objectKey, out var parsed) || parsed != key) return null;
        if (storageScope is { } scope && scope.Value != key.TenantId) return null;
        now = now.ToUniversalTime();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{storageProvider}\n{storageName}\n{objectKey}"));
        var targetIdentity = Convert.ToHexStringLower(digest);
        var tenantId = new TenantId(key.TenantId);

        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = BitConverter.ToInt64(digest);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);

        // Terminal check: if already completed and audited, do not issue fresh intent
        var existing = await context.BackupStorageCleanups
            .SingleOrDefaultAsync(c => c.StorageProvider == storageProvider && c.StorageName == storageName && c.TargetIdentity == targetIdentity, cancellationToken);
        if (existing is not null)
        {
            if (existing.Status == BackupStorageCleanupStatus.Completed) return null;
            if (existing.Claim(Guid.NewGuid(), now, leaseDuration))
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ClaimedStorageCleanup(
                    existing.Id, existing.TenantId, existing.JobId, existing.RunId, existing.AttemptId,
                    existing.StorageProvider, existing.StorageName, existing.TargetIdentity, existing.Key, existing.IsPartial, existing.LeaseToken!.Value);
            }
            return null;
        }

        var activity = await (from attempt in context.BackupRunAttempts
                              join run in context.BackupRuns on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                              where attempt.Id == key.AttemptId && attempt.TenantId == tenantId &&
                                  run.Id == key.RunId && run.JobId == key.JobId
                              select attempt.Id).SingleOrDefaultAsync(cancellationToken);
        if (activity == Guid.Empty) return null;

        var cleanup = new BackupStorageCleanup(
            Guid.NewGuid(), tenantId, key.JobId, key.RunId, key.AttemptId, storageProvider, storageName, targetIdentity,
            objectKey, key.IsPartial, now, Guid.NewGuid(), now + leaseDuration);
        context.BackupStorageCleanups.Add(cleanup);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ClaimedStorageCleanup(
            cleanup.Id, cleanup.TenantId, cleanup.JobId, cleanup.RunId, cleanup.AttemptId,
            cleanup.StorageProvider, cleanup.StorageName, cleanup.TargetIdentity, cleanup.Key, cleanup.IsPartial, cleanup.LeaseToken!.Value);
    }

    public async Task<IReadOnlyList<PendingStorageCleanup>> ListPendingSweptDeletionsAsync(
        string storageProvider, string storageName, TenantId? storageScope, DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageName);
        now = now.ToUniversalTime();
        var take = Math.Clamp(limit, 1, MaxClaimBatch);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var query = context.BackupStorageCleanups.AsNoTracking()
            .Where(c => c.StorageProvider == storageProvider && c.StorageName == storageName && c.Status == BackupStorageCleanupStatus.Pending
                && (c.LeaseExpiresAt == null || c.LeaseExpiresAt <= now));
        if (storageScope is { } scope)
        {
            query = query.Where(c => c.TenantId == scope);
        }
        var list = await query.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Take(take)
            .Select(c => new PendingStorageCleanup(
                c.Id, c.TenantId, c.JobId, c.RunId, c.AttemptId, c.StorageProvider, c.StorageName, c.TargetIdentity, c.Key, c.IsPartial))
            .ToListAsync(cancellationToken);
        return list;
    }

    public async Task<ClaimedStorageCleanup?> ClaimPendingSweptDeletionAsync(
        Guid cleanupId, TenantId? storageScope, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var cleanup = await context.BackupStorageCleanups
            .SingleOrDefaultAsync(c => c.Id == cleanupId && (storageScope == null || c.TenantId == storageScope), cancellationToken);
        if (cleanup is null || !cleanup.Claim(Guid.NewGuid(), now, leaseDuration)) return null;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ClaimedStorageCleanup(
            cleanup.Id, cleanup.TenantId, cleanup.JobId, cleanup.RunId, cleanup.AttemptId,
            cleanup.StorageProvider, cleanup.StorageName, cleanup.TargetIdentity, cleanup.Key, cleanup.IsPartial, cleanup.LeaseToken!.Value);
    }

    public async Task<bool> CompleteSweptDeletionAsync(Guid cleanupId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var cleanup = await context.BackupStorageCleanups.SingleOrDefaultAsync(c => c.Id == cleanupId, cancellationToken);
        if (cleanup is null || !cleanup.Complete(leaseToken, now)) return false;

        var auditExists = await context.AuditEvents.AnyAsync(audit => audit.Action == AuditActions.BackupStorageOrphanDeleteCompleted &&
            audit.TargetId == cleanup.TargetIdentity && audit.TenantId == cleanup.TenantId, cancellationToken);
        if (!auditExists)
        {
            context.AuditEvents.Add(AuditEvent.Record(
                now, UserId.System, AuditActorKind.System, AuditActions.BackupStorageOrphanDeleteCompleted, cleanup.TenantId,
                "backup_storage_object", cleanup.TargetIdentity,
                new Dictionary<string, object?>
                {
                    ["attemptId"] = cleanup.AttemptId.ToString("D"),
                    ["runId"] = cleanup.RunId.ToString("D"),
                    ["jobId"] = cleanup.JobId.ToString("D"),
                    ["storageProvider"] = SafeStorageProvider(cleanup.StorageProvider),
                    ["result"] = "deleted",
                    ["absenceVerified"] = true,
                    ["kind"] = cleanup.IsPartial ? "partial_upload" : "orphan"
                }));
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Locks the artifact row, tenant-qualified so another tenant's id is simply not found. With <paramref name="skipLocked"/> a row somebody else
    /// holds counts as not obtained instead of blocking.
    /// </summary>
    private static async Task<bool> LockArtifactAsync(PlatformDbContext context, TenantId tenantId, Guid artifactId, bool skipLocked, CancellationToken cancellationToken)
    {
        List<Guid> locked;
        if (skipLocked)
        {
            locked = await context.Database.SqlQuery<Guid>(
                $"SELECT \"Id\" AS \"Value\" FROM backup_artifacts WHERE \"Id\" = {artifactId} AND \"TenantId\" = {tenantId.Value} FOR UPDATE SKIP LOCKED").ToListAsync(cancellationToken);
        }
        else
        {
            locked = await context.Database.SqlQuery<Guid>(
                $"SELECT \"Id\" AS \"Value\" FROM backup_artifacts WHERE \"Id\" = {artifactId} AND \"TenantId\" = {tenantId.Value} FOR UPDATE").ToListAsync(cancellationToken);
        }
        return locked.Count > 0;
    }

    private sealed record ArtifactOwner(Guid RunId, Guid JobId, string JobName, string DatabaseName, string? AgentName, AgentId AgentId, BackupConfiguration Snapshot);

    private static async Task<Dictionary<Guid, ArtifactOwner>> ResolveOwnersAsync(PlatformDbContext context, TenantId tenantId, Guid[] attemptIds, CancellationToken cancellationToken)
    {
        var rows = await (from attempt in context.BackupRunAttempts.AsNoTracking()
                          join run in context.BackupRuns.AsNoTracking() on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                          join job in context.BackupJobs.AsNoTracking() on new { run.JobId, run.TenantId } equals new { JobId = job.Id, job.TenantId }
                          join agent in context.Agents.AsNoTracking() on new { run.AgentId, run.TenantId } equals new { AgentId = agent.Id, agent.TenantId }
                          where attempt.TenantId == tenantId && attemptIds.Contains(attempt.Id)
                          select new { AttemptId = attempt.Id, attempt.RunId, run.JobId, JobName = job.Name,
                              Snapshot = run.Configuration, AgentName = agent.HostName, run.AgentId }).ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.AttemptId, row => new ArtifactOwner(row.RunId, row.JobId, row.JobName, row.Snapshot.Database, row.AgentName, row.AgentId, row.Snapshot));
    }

    private static BackupArtifactRecord ToRecord(BackupArtifact artifact, Dictionary<Guid, ArtifactOwner> owners)
    {
        // Tenant-qualified joins resolve only safe display labels, never configuration credentials or paths.
        if (!owners.TryGetValue(artifact.AttemptId, out var owner)) throw new InvalidOperationException("The artifact's run could not be resolved.");
        return new BackupArtifactRecord(artifact, owner.RunId, owner.JobId, owner.JobName, owner.DatabaseName, owner.AgentName, owner.AgentId.Value, owner.Snapshot);
    }
}
