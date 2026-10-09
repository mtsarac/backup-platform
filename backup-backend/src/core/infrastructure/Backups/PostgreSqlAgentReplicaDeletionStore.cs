using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Replica row locks serialize dispatch, polls, renewal and completion; operation and token fence every mutation.</summary>
public sealed class PostgreSqlAgentReplicaDeletionStore(DbContextOptions<PlatformDbContext> options, BackupRetentionOptions retention)
    : IAgentReplicaDeletionStore
{
    private const int MaxClaimCandidates = 32;

    public async Task<DeleteReplicaTaskSnapshot?> ClaimAsync(AgentIdentity identity, DateTimeOffset now, TimeSpan lease,
        CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // A pinned replica is never handed to an agent for physical removal. The database refuses to move a pinned replica to Deleting, so none should be found here; the
        // check keeps this claim honest on its own, and is repeated after the row locks (a new statement) exactly like the backend claim.
        var ids = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_replicas
            WHERE "TenantId" = {identity.TenantId.Value} AND "OwningAgentId" = {identity.AgentId.Value}
              AND "ExecutionLocation" = 'Agent' AND "Status" = 'Deleting'
              AND ("DeleteLeaseToken" IS NULL OR "DeleteLeaseExpiresAt" <= {now})
              AND NOT EXISTS (SELECT 1 FROM restore_replica_pins pin WHERE pin."ReplicaId" = backup_replicas."Id" AND pin."ReleasedAt" IS NULL)
              AND NOT EXISTS (SELECT 1 FROM download_sessions hold WHERE hold."ReplicaId" = backup_replicas."Id" AND hold."TenantId" = backup_replicas."TenantId"
                  AND hold."ReleasedAt" IS NULL AND hold."ExpiresAt" > {now} AND (hold."LeaseExpiresAt" IS NULL OR hold."LeaseExpiresAt" > {now}))
            ORDER BY "DeleteAfter", "Id"
            LIMIT {MaxClaimCandidates}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
        // Most polls find nothing to delete: only a claim that selected rows pays for the second statement.
        var pinned = ids.Count == 0 ? [] : await context.Database.SqlQuery<Guid>($"""
            SELECT "ReplicaId" AS "Value" FROM restore_replica_pins WHERE "ReleasedAt" IS NULL AND "ReplicaId" = ANY({ids.ToArray()})
            UNION
            SELECT "ReplicaId" AS "Value" FROM download_sessions WHERE "ReleasedAt" IS NULL AND "ExpiresAt" > {now}
                AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" > {now}) AND "ReplicaId" = ANY({ids.ToArray()})
            """).ToListAsync(cancellationToken);
        if (pinned.Count != 0) ids = [.. ids.Where(id => !pinned.Contains(id))];
        DeleteReplicaTaskSnapshot? task = null;
        foreach (var id in ids)
        {
            var replica = await context.BackupReplicas.SingleAsync(entity => entity.Id == id, cancellationToken);
            var authority = await ResolveAuthorityAsync(context, replica, cancellationToken);
            if (!replica.ClaimAgentDeletion(replica.DeleteOperationId ?? Guid.NewGuid(), Guid.NewGuid(), now, lease)) continue;
            var failure = ValidateAuthority(replica, identity, authority);
            if (failure is not null)
            {
                replica.CompleteAgentDeletion(replica.DeleteOperationId!.Value, replica.DeleteLeaseToken!.Value, now, false, failure, null);
                continue;
            }
            task = new DeleteReplicaTaskSnapshot
            {
                ReplicaId = replica.Id, TenantId = replica.TenantId.Value, AgentId = identity.AgentId.Value,
                OperationId = replica.DeleteOperationId!.Value, LeaseToken = replica.DeleteLeaseToken!.Value,
                LeaseExpiresAt = replica.DeleteLeaseExpiresAt!.Value, JobId = authority!.JobId,
                RunId = authority.RunId, AttemptId = authority.AttemptId, StorageProvider = replica.StorageProvider,
                StorageTarget = replica.StorageTarget, StorageKey = replica.Key, StoragePrefix = authority.Configuration.StoragePrefix
            };
            break;
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    public async Task<DeleteReplicaLeaseResponse?> RenewAsync(AgentIdentity identity, Guid replicaId, DeleteReplicaLeaseRequest request,
        DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var replica = await LockReplicaAsync(context, identity, replicaId, cancellationToken);
        if (replica is null || request.OperationId == Guid.Empty || request.LeaseToken == Guid.Empty ||
            !replica.RenewAgentDeletion(request.OperationId, request.LeaseToken, now, lease)) return null;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DeleteReplicaLeaseResponse { LeaseExpiresAt = replica.DeleteLeaseExpiresAt!.Value };
    }

    public async Task<BackupMutationResult> CompleteAsync(AgentIdentity identity, Guid replicaId, DeleteReplicaCompletionRequest request,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var replica = await LockReplicaAsync(context, identity, replicaId, cancellationToken);
        if (replica is null) return BackupMutationResult.NotFound;
        if (!ValidCompletion(request)) return BackupMutationResult.Conflict;
        if (replica.SameAgentCompletion(request.OperationId, request.LeaseToken, request.AbsenceVerified, request.FailureCode))
            return BackupMutationResult.Accepted;
        DateTimeOffset? next = request.FailureCode is { } code && IsRetryable(code)
            ? now + retention.DeleteRetryDelay(replica.DeleteAttempts) : null;
        if (!replica.CompleteAgentDeletion(request.OperationId, request.LeaseToken, now, request.AbsenceVerified, request.FailureCode, next))
            return BackupMutationResult.Conflict;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BackupMutationResult.Accepted;
    }

    private static bool ValidCompletion(DeleteReplicaCompletionRequest request) =>
        request.OperationId != Guid.Empty && request.LeaseToken != Guid.Empty &&
        (request.AbsenceVerified ? request.FailureCode is null : request.FailureCode is
            DeleteReplicaFailureCodes.StorageTargetUnknown or DeleteReplicaFailureCodes.InvalidLocation or
            DeleteReplicaFailureCodes.InvalidConfiguration or DeleteReplicaFailureCodes.AccessDenied or DeleteReplicaFailureCodes.IoError or
            DeleteReplicaFailureCodes.DeleteNotSupported or DeleteReplicaFailureCodes.DeleteNotVerified or DeleteReplicaFailureCodes.Unexpected);

    private static bool IsRetryable(string code) => code is DeleteReplicaFailureCodes.IoError or
        DeleteReplicaFailureCodes.DeleteNotVerified or DeleteReplicaFailureCodes.Unexpected;

    private static async Task<BackupReplica?> LockReplicaAsync(PlatformDbContext context, AgentIdentity identity, Guid replicaId,
        CancellationToken cancellationToken)
    {
        var ids = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_replicas
            WHERE "Id" = {replicaId} AND "TenantId" = {identity.TenantId.Value} AND "OwningAgentId" = {identity.AgentId.Value}
              AND "ExecutionLocation" = 'Agent'
            FOR UPDATE
            """).ToListAsync(cancellationToken);
        return ids.Count == 0 ? null : await context.BackupReplicas.SingleAsync(entity => entity.Id == replicaId, cancellationToken);
    }

    private sealed record Authority(Guid JobId, Guid RunId, Guid AttemptId, Guid AgentId, BackupConfiguration Configuration);

    private static async Task<Authority?> ResolveAuthorityAsync(PlatformDbContext context, BackupReplica replica, CancellationToken cancellationToken) =>
        await (from artifact in context.BackupArtifacts.AsNoTracking()
               join attempt in context.BackupRunAttempts.AsNoTracking() on new { artifact.AttemptId, artifact.TenantId }
                   equals new { AttemptId = attempt.Id, attempt.TenantId }
               join run in context.BackupRuns.AsNoTracking() on new { attempt.RunId, attempt.TenantId }
                   equals new { RunId = run.Id, run.TenantId }
               where artifact.Id == replica.ArtifactId && artifact.TenantId == replica.TenantId
               select new Authority(run.JobId, run.Id, attempt.Id, run.AgentId.Value, run.Configuration)).SingleOrDefaultAsync(cancellationToken);

    private static string? ValidateAuthority(BackupReplica replica, AgentIdentity identity, Authority? authority)
    {
        if (authority is null || authority.AgentId != identity.AgentId.Value ||
            authority.Configuration.StorageExecutionLocation != BackupStorageExecutionLocation.Agent ||
            authority.Configuration.StorageProvider != "local" || replica.StorageProvider != authority.Configuration.StorageProvider)
            return DeleteReplicaFailureCodes.InvalidConfiguration;
        if (replica.StorageTarget != authority.Configuration.StorageProvider) return DeleteReplicaFailureCodes.StorageTargetUnknown;
        if (!ManagedBackupLayout.TryParseStorageKey(replica.Key, out var key) || key is null || key.IsPartial ||
            key.TenantId != replica.TenantId.Value || key.JobId != authority.JobId || key.RunId != authority.RunId ||
            key.AttemptId != authority.AttemptId || key.Prefix != authority.Configuration.StoragePrefix)
            return DeleteReplicaFailureCodes.InvalidLocation;
        return null;
    }
}
