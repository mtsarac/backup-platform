using BackupPlatform.Application.Lists;
using BackupPlatform.Application.Platform;
using BackupPlatform.Infrastructure.Lists;
using BackupPlatform.Domain.Storage;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
public sealed class PostgreSqlBackupRetentionPlatformStore(DbContextOptions<PlatformDbContext> options, BackupRetentionOptions? retention = null) : IBackupRetentionPlatformStore
{
    public async Task<BackupArtifactRecord?> GetArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
        return await RetentionArtifacts.GetAsync(context, tenantId, artifactId, cancellationToken);
    }

    private readonly BackupRetentionOptions retentionOptions = retention ?? new BackupRetentionOptions();

    private const int MaxScanBatch = 1000;

    private const int MaxClaimBatch = 500;

    private const int LookupChunk = 500;

    private const int MaxPrunePerCall = 10_000;

    public async Task<BackupRetentionApplication> ApplyExpiredRetentionAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var scan = PlatformOperationsDbContext.Create(options);
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
                await using var context = PlatformOperationsDbContext.Create(options);
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                // Somebody else (another node's scan, a manual delete, a cancel) owns the artifact right now: it is either handled by them or
                // picked up by the next scan, never worth waiting for.
                if (!await RetentionArtifacts.LockArtifactAsync(context, candidate.TenantId, candidate.Id, skipLocked: true, cancellationToken)) continue;
                var artifact = await context.BackupArtifacts.Include(candidateArtifact => candidateArtifact.Replicas)
                    .SingleOrDefaultAsync(entity => entity.TenantId == candidate.TenantId && entity.Id == candidate.Id, cancellationToken);
                if (artifact is null) continue;
                // The domain decides expiry and pin again, now that nobody can change the artifact under it.
                var previouslyApplied = artifact.RetentionAppliedAt;
                var applied = artifact.ApplyRetention(now);
                if (previouslyApplied is null && artifact.RetentionAppliedAt is not null)
                    context.RecordAudit(AuditEvent.Record(
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
            catch (DbUpdateException exception) when (BackupRunQueries.IsUniqueViolation(exception))
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
        await using var context = PlatformOperationsDbContext.Create(options);
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
            if (replica.ExecutionLocation == BackupStorageExecutionLocation.Backend && StorageProviderTypes.External(replica.StorageProvider))
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
        await using var context = PlatformOperationsDbContext.Create(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var replica = await context.BackupReplicas.SingleOrDefaultAsync(entity => entity.Id == replicaId, cancellationToken);
        if (replica is null || !replica.CompleteDelete(leaseToken, now)) return false;
        if (replica.StorageProvider == StorageProviderTypes.GoogleDrive)
        {
            await context.GoogleDriveFileMetadata.Where(m =>
                m.TenantId == replica.TenantId && m.ConnectionId == replica.StorageConnectionId && m.StorageKey == replica.Key)
                .ExecuteDeleteAsync(cancellationToken);
        }
        context.RecordAudit(AuditEvent.Record(
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
        await using var context = PlatformOperationsDbContext.Create(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var replica = await context.BackupReplicas.SingleOrDefaultAsync(entity => entity.Id == replicaId, cancellationToken);
        if (replica is null || !replica.FailDelete(leaseToken, now, failureCode, nextAttemptAt)) return false;
        var safeCode = failureCode is BackupDeleteFailureCodes.StorageNotConfigured or BackupDeleteFailureCodes.DeleteNotSupported or
            BackupDeleteFailureCodes.InvalidLocation or BackupDeleteFailureCodes.AccessDenied or BackupDeleteFailureCodes.StorageUnavailable or
            BackupDeleteFailureCodes.IoError or BackupDeleteFailureCodes.DeleteNotVerified or BackupDeleteFailureCodes.Unexpected
            ? failureCode : BackupDeleteFailureCodes.Unexpected;
        context.RecordAudit(AuditEvent.Record(
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

    public async Task<IReadOnlySet<string>> FindReferencedKeysAsync(string storageProvider, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        if (keys.Count == 0) return referenced;
        await using var context = PlatformOperationsDbContext.Create(options);
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
        await using var context = PlatformOperationsDbContext.Create(options);
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
        await using var context = PlatformOperationsDbContext.Create(options);
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

    private static string SafeStorageProvider(string provider) => provider is StorageProviderTypes.Local or StorageProviderTypes.Central || StorageProviderTypes.External(provider) ? provider : "other";

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

        await using var context = PlatformOperationsDbContext.Create(options);
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
        await using var context = PlatformOperationsDbContext.Create(options);
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
        await using var context = PlatformOperationsDbContext.Create(options);
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
        await using var context = PlatformOperationsDbContext.Create(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var cleanup = await context.BackupStorageCleanups.SingleOrDefaultAsync(c => c.Id == cleanupId, cancellationToken);
        if (cleanup is null || !cleanup.Complete(leaseToken, now)) return false;

        var auditExists = await context.AuditEvents.AnyAsync(audit => audit.Action == AuditActions.BackupStorageOrphanDeleteCompleted &&
            audit.TargetId == cleanup.TargetIdentity && audit.TenantId == cleanup.TenantId, cancellationToken);
        if (!auditExists)
        {
            context.RecordAudit(AuditEvent.Record(
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
}
