using BackupPlatform.Domain.Storage;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
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
        // External replicas have no owning agent: any agent of the tenant that advertises the lifecycle capability may remove them. An older agent would never run the task.
        var external = await context.AgentCapabilities.AsNoTracking().AnyAsync(capability => capability.TenantId == identity.TenantId && capability.AgentId == identity.AgentId &&
            capability.Identifier == ReplicaLifecycleProtocol.Capability && capability.Support == CapabilitySupport.Supported, cancellationToken);
        // A pinned replica is never handed to an agent for physical removal. The database refuses to move a pinned replica to Deleting, so none should be found here; the
        // check keeps this claim honest on its own, and is repeated after the row locks (a new statement) exactly like the backend claim.
        var ids = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_replicas
            WHERE "TenantId" = {identity.TenantId.Value} AND "Status" = 'Deleting'
              AND (("OwningAgentId" = {identity.AgentId.Value} AND "ExecutionLocation" = 'Agent')
                   OR ({external} AND "ExecutionLocation" = 'Backend' AND "StorageProvider" IN ('sftp', 'google-drive')))
              AND ("DeleteLeaseToken" IS NULL OR "DeleteLeaseExpiresAt" <= {now})
              AND NOT EXISTS (SELECT 1 FROM restore_replica_pins pin WHERE pin."ReplicaId" = backup_replicas."Id" AND pin."ReleasedAt" IS NULL)
              AND NOT EXISTS (SELECT 1 FROM replica_repair_operations repair WHERE repair."SourceReplicaId" = backup_replicas."Id" AND repair."TenantId" = backup_replicas."TenantId" AND repair."Status" IN ('Pending', 'InProgress'))
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
                UNION
                SELECT "SourceReplicaId" AS "Value" FROM replica_repair_operations WHERE "Status" IN ('Pending', 'InProgress') AND "SourceReplicaId" = ANY({ids.ToArray()})
            """).ToListAsync(cancellationToken);
        if (pinned.Count != 0) ids = [.. ids.Where(id => !pinned.Contains(id))];
        DeleteReplicaTaskSnapshot? task = null;
        foreach (var id in ids)
        {
            var replica = await context.BackupReplicas.SingleAsync(entity => entity.Id == id, cancellationToken);
            var authority = await ResolveAuthorityAsync(context, replica, cancellationToken);
            if (!replica.ClaimAgentDeletion(replica.DeleteOperationId ?? Guid.NewGuid(), Guid.NewGuid(), now, lease, identity.AgentId.Value)) continue;
            var failure = ValidateAuthority(replica, identity, authority);
            if (failure is not null)
            {
                replica.CompleteAgentDeletion(replica.DeleteOperationId!.Value, replica.DeleteLeaseToken!.Value, now, false, failure, null);
                RecordDeletionOutcome(context, replica, identity, now, false, failure);
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
        // Credentials of an external connection can be repaired by its owner, so a refused access is retried there like any other transient failure.
        DateTimeOffset? next = request.FailureCode is { } code && (IsRetryable(code) || replica.ExecutionLocation == BackupStorageExecutionLocation.Backend && code == DeleteReplicaFailureCodes.AccessDenied)
            ? now + retention.DeleteRetryDelay(replica.DeleteAttempts) : null;
        if (!replica.CompleteAgentDeletion(request.OperationId, request.LeaseToken, now, request.AbsenceVerified, request.FailureCode, next))
            return BackupMutationResult.Conflict;
        RecordDeletionOutcome(context, replica, identity, now, request.AbsenceVerified, request.FailureCode);
        if (request.AbsenceVerified && replica.StorageProvider == StorageProviderTypes.GoogleDrive)
        {
            // The object is gone from Drive, so its catalog record goes in the same transaction as the Deleted status.
            await context.GoogleDriveFileMetadata.Where(metadata =>
                metadata.TenantId == replica.TenantId && metadata.ConnectionId == replica.StorageConnectionId && metadata.StorageKey == replica.Key)
                .ExecuteDeleteAsync(cancellationToken);
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BackupMutationResult.Accepted;
    }

    public async Task<DeletionStorageTarget?> AuthorizeStorageAsync(AgentIdentity identity, Guid replicaId, Guid leaseToken, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var replica = await context.BackupReplicas.AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.TenantId == identity.TenantId && entity.Id == replicaId, cancellationToken);
        return replica is { ExecutionLocation: BackupStorageExecutionLocation.Backend, Status: BackupReplicaStatus.Deleting } && replica.AgentExecuted
            && leaseToken != Guid.Empty && replica.DeleteLeaseToken == leaseToken && replica.DeleteLeaseExpiresAt > now
            && replica.DeleteAgentId == identity.AgentId.Value && replica.DeleteLeaseOwner == BackupReplica.AgentLeaseOwner(identity.AgentId.Value) && replica.StorageConnectionId != Guid.Empty
            ? new DeletionStorageTarget(replica.StorageProvider, replica.StorageConnectionId) : null;
    }

    private static bool ValidCompletion(DeleteReplicaCompletionRequest request) =>
        request.OperationId != Guid.Empty && request.LeaseToken != Guid.Empty &&
        (request.AbsenceVerified ? request.FailureCode is null : request.FailureCode is
            DeleteReplicaFailureCodes.StorageTargetUnknown or DeleteReplicaFailureCodes.InvalidLocation or
            DeleteReplicaFailureCodes.InvalidConfiguration or DeleteReplicaFailureCodes.AccessDenied or DeleteReplicaFailureCodes.IoError or
            DeleteReplicaFailureCodes.DeleteNotSupported or DeleteReplicaFailureCodes.DeleteNotVerified or DeleteReplicaFailureCodes.Unexpected);

    private static bool IsRetryable(string code) => code is DeleteReplicaFailureCodes.IoError or
        DeleteReplicaFailureCodes.DeleteNotVerified or DeleteReplicaFailureCodes.Unexpected;

    private static void RecordDeletionOutcome(PlatformDbContext context, BackupReplica replica, AgentIdentity identity,
        DateTimeOffset now, bool absenceVerified, string? failureCode) =>
        context.AuditEvents.Add(AuditEvent.Record(
            now, UserId.System, AuditActorKind.System,
            absenceVerified ? AuditActions.BackupReplicaPhysicalDeleteCompleted : AuditActions.BackupReplicaPhysicalDeleteFailed,
            replica.TenantId, "backup_replica", replica.Id.ToString("D"),
            new Dictionary<string, object?>
            {
                ["artifactId"] = replica.ArtifactId.ToString("D"),
                ["agentId"] = identity.AgentId.Value.ToString("D"),
                ["result"] = absenceVerified ? "deleted" : replica.DeleteNextAttemptAt.HasValue ? "retry_scheduled" : "failed",
                ["absenceVerified"] = absenceVerified,
                ["failureCode"] = failureCode,
                ["attempt"] = replica.DeleteAttempts,
                ["nextAttemptAt"] = replica.DeleteNextAttemptAt
            }));

    private static async Task<BackupReplica?> LockReplicaAsync(PlatformDbContext context, AgentIdentity identity, Guid replicaId,
        CancellationToken cancellationToken)
    {
        var ids = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_replicas
            WHERE "Id" = {replicaId} AND "TenantId" = {identity.TenantId.Value}
              AND (("OwningAgentId" = {identity.AgentId.Value} AND "ExecutionLocation" = 'Agent')
                   OR ("ExecutionLocation" = 'Backend' AND "StorageProvider" IN ('sftp', 'google-drive')
                       AND "DeleteAgentId" = {identity.AgentId.Value}))
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
        if (replica.ExecutionLocation == BackupStorageExecutionLocation.Backend)
        {
            // An external replica names its own connection; the run only proves which job, run and attempt its key belongs to.
            if (authority is null || !Guid.TryParse(replica.StorageTarget, out var connectionId) || connectionId == Guid.Empty || connectionId != replica.StorageConnectionId)
                return DeleteReplicaFailureCodes.StorageTargetUnknown;
        }
        else
        {
            var primary = authority?.Configuration.Destinations.FirstOrDefault(d => d.IsPrimary);
            if (authority is null || primary is null || authority.AgentId != identity.AgentId.Value ||
                primary.StorageExecutionLocation != BackupStorageExecutionLocation.Agent ||
                primary.StorageProvider != StorageProviderTypes.Local || replica.StorageProvider != primary.StorageProvider)
                return DeleteReplicaFailureCodes.InvalidConfiguration;
            if (replica.StorageTarget != primary.StorageProvider) return DeleteReplicaFailureCodes.StorageTargetUnknown;
        }
        if (!ManagedBackupLayout.TryParseStorageKey(replica.Key, out var key) || key is null || key.IsPartial && replica.ExecutionLocation == BackupStorageExecutionLocation.Agent ||
            key.TenantId != replica.TenantId.Value || key.JobId != authority!.JobId || key.RunId != authority.RunId ||
            key.AttemptId != authority.AttemptId || key.Prefix != authority.Configuration.StoragePrefix)
            return DeleteReplicaFailureCodes.InvalidLocation;
        return null;
    }
}
