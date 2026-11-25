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
using BackupPlatform.Application.Tenancy;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>
/// Artifact listing, lookup and manual delete of the request's tenant, on the filtered context. Retention scans, delete workers and the storage sweeper are
/// <see cref="PostgreSqlBackupRetentionPlatformStore"/>.
/// </summary>
public sealed class PostgreSqlBackupRetentionStore(DbContextOptions<PlatformDbContext> options, ITenantContext tenant) : IBackupRetentionStore
{
    private PlatformDbContext Open(TenantId tenantId) => PlatformDbContext.ForTenant(options, tenant, tenantId);

    private const int MaxListLimit = 200;

    // A mutation re-runs when a delete worker changed a replica between the read and the write; the lock keeps every other writer out.
    private const int MutationAttempts = 3;

    public async Task<BackupArtifactDeleteResult> RequestDeleteAsync(TenantId tenantId, Guid artifactId, Guid? requestedByUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = await MutateArtifactAsync(tenantId, artifactId, artifact => artifact.RequestDelete(now, requestedByUserId), cancellationToken,
            (context, artifact, outcome) =>
            {
                if (outcome != BackupArtifactDeleteOutcome.Requested) return;
                var actorId = requestedByUserId.HasValue ? new UserId(requestedByUserId.Value) : UserId.System;
                var actorKind = requestedByUserId.HasValue ? AuditActorKind.UserAdmin : AuditActorKind.System;
                context.RecordAudit(AuditEvent.Record(
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
                var actorKind = requestedByUserId.HasValue ? AuditActorKind.UserAdmin : AuditActorKind.System;
                context.RecordAudit(AuditEvent.Record(
                    now, actorId, actorKind, AuditActions.BackupArtifactDeleteCancelled, tenantId,
                    "backup_artifact", artifactId.ToString("D"),
                    new Dictionary<string, object?> { ["result"] = "restored", ["replicasRestored"] = restored }));
            });
        if (result is null) return new BackupArtifactCancelResult(BackupArtifactCancelKind.NotFound, null);
        return new BackupArtifactCancelResult(result.Outcome > 0 ? BackupArtifactCancelKind.Restored : BackupArtifactCancelKind.NothingToCancel, result.Record);
    }

    public async Task<BackupArtifactRecord?> GetArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken)
    {
        await using var context = Open(tenantId);
        return await RetentionArtifacts.GetAsync(context, tenantId, artifactId, cancellationToken);
    }

    public async Task<IReadOnlyList<BackupArtifactRecord>> ListArtifactsAsync(TenantId tenantId, Guid? jobId, int limit, CancellationToken cancellationToken)
    {
        await using var context = Open(tenantId);
        var artifacts = await context.BackupArtifacts.AsNoTracking().Include(entity => entity.Replicas)
            .Where(entity => entity.TenantId == tenantId && (jobId == null || context.BackupRunAttempts.Any(attempt =>
                attempt.Id == entity.AttemptId && attempt.TenantId == entity.TenantId && context.BackupRuns.Any(run =>
                    run.Id == attempt.RunId && run.TenantId == attempt.TenantId && run.JobId == jobId))))
            .OrderByDescending(entity => entity.AcceptedAt ?? entity.CreatedAt).ThenBy(entity => entity.Id)
            .Take(Math.Clamp(limit, 1, MaxListLimit)).AsSplitQuery().ToListAsync(cancellationToken);
        var owners = await RetentionArtifacts.ResolveOwnersAsync(context, tenantId, artifacts.Select(artifact => artifact.AttemptId).Distinct().ToArray(), cancellationToken);
        return artifacts.Select(artifact => RetentionArtifacts.ToRecord(artifact, owners)).ToList();
    }

    // One row per artifact with everything the list sorts or searches by resolved in SQL (job and database names by joining the run and the job).
    // StateRank is the lifecycle state the API reports: 0 Available, 1 DeletePending, 2 Deleting, 3 DeleteFailed, 4 Deleted (the filter and the sort share it).
    private sealed class ArtifactRow
    {
        public required Guid Id { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required long SizeBytes { get; init; }
        public required string JobName { get; init; }
        public string? DatabaseName { get; init; }
        public string? FileName { get; init; }
        public required int StateRank { get; init; }
    }

    private static readonly string[] ArtifactStates = ["Available", "DeletePending", "Deleting", "DeleteFailed", "Deleted"];

    private static readonly SortMap<ArtifactRow> ArtifactSorts = SortMap<ArtifactRow>.Create()
        .Date("createdAt", row => row.CreatedAt)
        .Value("size", row => row.SizeBytes)
        .Value("status", row => row.StateRank)
        .Text("jobName", row => row.JobName)
        .Text("databaseName", row => row.DatabaseName);

    public async Task<PagedResult<BackupArtifactRecord>> QueryArtifactsAsync(TenantId tenantId, BackupArtifactQuery query, CancellationToken cancellationToken)
    {
        await using var context = Open(tenantId);
        var artifacts = context.BackupArtifacts.AsNoTracking().Where(artifact => artifact.TenantId == tenantId);
        if (query.From is { } from) artifacts = artifacts.Where(artifact => artifact.CreatedAt >= from.ToUniversalTime());
        if (query.To is { } to) artifacts = artifacts.Where(artifact => artifact.CreatedAt <= to.ToUniversalTime());
        var agentId = query.AgentId is { } agent ? new AgentId(agent) : (AgentId?)null;
        var rows = from artifact in artifacts
                   join attempt in context.BackupRunAttempts.AsNoTracking() on new { artifact.AttemptId, artifact.TenantId } equals new { AttemptId = attempt.Id, attempt.TenantId }
                   join run in context.BackupRuns.AsNoTracking() on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                   join job in context.BackupJobs.AsNoTracking() on new { run.JobId, run.TenantId } equals new { JobId = job.Id, job.TenantId }
                   where (query.JobId == null || run.JobId == query.JobId) && (agentId == null || run.AgentId == agentId)
                   select new ArtifactRow
                   {
                       Id = artifact.Id, CreatedAt = artifact.CreatedAt, SizeBytes = artifact.SizeBytes, JobName = job.Name, DatabaseName = run.Configuration.Database,
                       FileName = artifact.Replicas.OrderBy(replica => replica.CreatedAt).ThenBy(replica => replica.Id)
                           .Select(replica => Regex.Replace(replica.Key, "^.*/", "")).FirstOrDefault(),
                       StateRank = artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.Available) ? 0
                           : artifact.Replicas.Any() && artifact.Replicas.All(replica => replica.Status == BackupReplicaStatus.Deleted) ? 4
                           : artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.DeleteFailed && replica.DeleteFailureCode != null) ? 3
                           : artifact.Replicas.Any(replica => replica.Status == BackupReplicaStatus.Deleting) ? 2
                           : 1
                   };
        if (query.State is { } state)
        {
            var rank = Array.IndexOf(ArtifactStates, state);
            rows = rows.Where(row => row.StateRank == rank);
        }
        var page = await rows
            .ApplySearch(query.List, row => row.JobName, row => row.DatabaseName, row => row.FileName)
            .ApplySort(ArtifactSorts, query.List, "createdAt", row => row.Id)
            .ToPagedResultAsync(query.List, cancellationToken);
        var ids = page.Items.Select(row => row.Id).ToArray();
        var loaded = (await context.BackupArtifacts.AsNoTracking().Include(artifact => artifact.Replicas)
            .Where(artifact => artifact.TenantId == tenantId && ids.Contains(artifact.Id)).AsSplitQuery().ToListAsync(cancellationToken)).ToDictionary(artifact => artifact.Id);
        var owners = await RetentionArtifacts.ResolveOwnersAsync(context, tenantId, loaded.Values.Select(artifact => artifact.AttemptId).Distinct().ToArray(), cancellationToken);
        return new([.. ids.Select(id => RetentionArtifacts.ToRecord(loaded[id], owners))], page.TotalCount, page.Page, page.PageSize);
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
            await using var context = Open(tenantId);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (!await RetentionArtifacts.LockArtifactAsync(context, tenantId, artifactId, skipLocked: false, cancellationToken)) return null;
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
            var owners = await RetentionArtifacts.ResolveOwnersAsync(context, tenantId, [artifact.AttemptId], cancellationToken);
            return new Mutation<TOutcome>(outcome, RetentionArtifacts.ToRecord(artifact, owners));
        }
    }
}
