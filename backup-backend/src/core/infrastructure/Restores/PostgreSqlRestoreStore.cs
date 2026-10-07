using System.Linq.Expressions;
using System.Text.Json.Nodes;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Restores;

/// <summary>
/// Lifecycle writes take the tenant-qualified agent row lock first, like backups do, which fences one agent's polls, callbacks, cancellations and expiry sweeps
/// (and makes "no restore beside a backup of the same agent" a decision taken under one lock). Creating a restore additionally locks, in this order and never the
/// other way round: the artifact row (<c>FOR NO KEY UPDATE</c>, the order artifact-level mutators already use) and then the chosen replica row
/// (<c>FOR UPDATE</c>). The replica lock is what the retention pin relies on: a delete claim holds that row's lock while it checks for pins, so a pin can never
/// slip in between its check and its update, and the other way round the restore sees a replica a claim already took and refuses it.
/// </summary>
public sealed class PostgreSqlRestoreStore(DbContextOptions<PlatformDbContext> options) : IRestoreStore
{
    private const int MaxScanBatch = 500;
    // A concurrent eligibility change re-selects a replica this many times before the request is refused.
    private const int SelectionRounds = 4;
    // A losing idempotency race re-reads the winner; one extra round is enough, the others are for safety.
    private const int CreateRounds = 3;

    public async Task<RestoreCreateResult> CreateAsync(RestoreCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        for (var round = 0; round < CreateRounds; round++)
        {
            var result = await CreateOnceAsync(command, cancellationToken);
            if (result is not null) return result;
        }
        throw new InvalidOperationException("The restore could not be created because of repeated conflicting writes.");
    }

    /// <summary>Null when the insert lost a race on a unique key (another request with the same Idempotency-Key committed first); the caller starts over and finds the winner.</summary>
    private async Task<RestoreCreateResult?> CreateOnceAsync(RestoreCommand command, CancellationToken cancellationToken)
    {
        var (tenantId, artifactId, agentId) = (command.TenantId, command.ArtifactId, command.AgentId);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await context.BackupArtifacts.AnyAsync(artifact => artifact.TenantId == tenantId && artifact.Id == artifactId, cancellationToken))
            return new RestoreCreateResult(RestoreCreateOutcome.ArtifactNotFound);
        if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, tenantId, agentId, cancellationToken))
            return new RestoreCreateResult(RestoreCreateOutcome.AgentNotFound);
        // The key is looked up first: replaying a request returns its original operation even when the agent or the replica has since become unusable.
        var existing = await Operations(context).AsNoTracking()
            .SingleOrDefaultAsync(operation => operation.TenantId == tenantId && operation.ArtifactId == artifactId && operation.IdempotencyKey == command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return string.Equals(existing.RequestFingerprint, command.RequestFingerprint, StringComparison.Ordinal)
                ? new RestoreCreateResult(RestoreCreateOutcome.Replayed, existing)
                : new RestoreCreateResult(RestoreCreateOutcome.IdempotencyConflict);
        }
        var status = await context.Agents.AsNoTracking().Where(agent => agent.TenantId == tenantId && agent.Id == agentId)
            .Select(agent => agent.Status).SingleAsync(cancellationToken);
        if (status != AgentStatus.Active) return new RestoreCreateResult(RestoreCreateOutcome.AgentNotActive);
        var capable = await context.AgentCapabilities.AsNoTracking().AnyAsync(capability => capability.TenantId == tenantId && capability.AgentId == agentId &&
            capability.Identifier == RestoreAgentCapability.Identifier && capability.Support == CapabilitySupport.Supported, cancellationToken);
        if (!capable) return new RestoreCreateResult(RestoreCreateOutcome.AgentCannotRestore);

        // Lock order: agent (held), artifact, replica. Artifact-level mutators (manual delete, cancel) take the artifact first and then write replicas; taking the replica
        // first here would let a restore and a delete wait for each other's row.
        if (!await LockArtifactAsync(context, tenantId, artifactId, cancellationToken)) return new RestoreCreateResult(RestoreCreateOutcome.ArtifactNotFound);
        var selection = await SelectReplicaAsync(context, command, cancellationToken);
        if (selection.Rejection is { } rejection) return new RestoreCreateResult(RestoreCreateOutcome.SourceRejected, Rejection: rejection);

        var operation = new RestoreOperation(Guid.NewGuid(), tenantId, artifactId, selection.Replica!.Id, selection.Access, agentId, command.Target, command.DatabaseName, command.Mode,
            command.OverwriteConfirmed, command.Options, command.IdempotencyKey, command.RequestFingerprint, command.RequestedByUserId, command.Now);
        operation.QueueFirstAttempt(Guid.NewGuid(), command.Now);
        context.RestoreOperations.Add(operation);
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (PostgreSqlBackupRunStore.IsUniqueViolation(exception)) { return null; }
        await transaction.CommitAsync(cancellationToken);
        return new RestoreCreateResult(RestoreCreateOutcome.Created, operation);
    }

    private sealed record Selection(BackupReplica? Replica, RestoreSourceAccess Access, RestoreSourceRejection? Rejection);

    /// <summary>
    /// Chooses the replica (the requested one, or the best eligible one) and keeps it: the row is locked, then re-read in a new statement and judged again, so
    /// what is pinned is exactly what was checked. A replica that stopped being eligible between the first read and the lock is dropped and, when the
    /// caller did not name one, the next best is tried.
    /// </summary>
    private static async Task<Selection> SelectReplicaAsync(PlatformDbContext context, RestoreCommand command, CancellationToken cancellationToken)
    {
        var artifact = await LoadArtifactAsync(context, command, cancellationToken);
        for (var round = 0; round < SelectionRounds; round++)
        {
            BackupReplica? candidate;
            if (command.ReplicaId is { } requested)
            {
                candidate = artifact.Replicas.SingleOrDefault(replica => replica.Id == requested);
                if (candidate is null) return Rejected(RestoreSourceRejection.ReplicaNotFound);
            }
            else
            {
                candidate = RestoreSourceEvaluator.SelectReplica(artifact, command.AgentId, command.Now);
                if (candidate is null) return Rejected(RestoreSourceEvaluator.Explain(artifact, command.AgentId, command.Now));
            }
            var early = RestoreSourceEvaluator.Evaluate(artifact, candidate, command.AgentId, command.Now);
            if (!early.Eligible) return Rejected(early.Rejection!.Value);
            if (!await LockReplicaAsync(context, command.TenantId, command.ArtifactId, candidate.Id, cancellationToken)) return Rejected(RestoreSourceRejection.ReplicaNotFound);
            // A new statement under the lock: nothing can change this row until the transaction ends.
            artifact = await LoadArtifactAsync(context, command, cancellationToken);
            var locked = artifact.Replicas.SingleOrDefault(replica => replica.Id == candidate.Id);
            var decision = locked is null ? RestoreSourceDecision.Reject(RestoreSourceRejection.ReplicaNotFound) : RestoreSourceEvaluator.Evaluate(artifact, locked, command.AgentId, command.Now);
            if (decision.Eligible) return new Selection(locked, decision.Access!.Value, null);
            if (command.ReplicaId is not null) return Rejected(decision.Rejection!.Value);
        }
        return Rejected(RestoreSourceEvaluator.Explain(artifact, command.AgentId, command.Now));
    }

    private static Selection Rejected(RestoreSourceRejection rejection) => new(null, default, rejection);

    private static Task<BackupArtifact> LoadArtifactAsync(PlatformDbContext context, RestoreCommand command, CancellationToken cancellationToken) =>
        context.BackupArtifacts.AsNoTracking().Include(artifact => artifact.Replicas).AsSplitQuery()
            .SingleAsync(artifact => artifact.TenantId == command.TenantId && artifact.Id == command.ArtifactId, cancellationToken);

    public async Task<RestoreOperation?> GetAsync(TenantId tenantId, Guid restoreId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await Operations(context).AsNoTracking().SingleOrDefaultAsync(operation => operation.TenantId == tenantId && operation.Id == restoreId, cancellationToken);
    }

    public async Task<RestorePage> ListAsync(TenantId tenantId, RestoreQuery query, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var operations = context.RestoreOperations.AsNoTracking().Where(operation => operation.TenantId == tenantId);
        if (query.Status is { } status) operations = operations.Where(operation => operation.Status == status);
        if (query.ArtifactId is { } artifactId) operations = operations.Where(operation => operation.ArtifactId == artifactId);
        if (query.AgentId is { } agentGuid)
        {
            var agentId = new AgentId(agentGuid);
            operations = operations.Where(operation => operation.AgentId == agentId);
        }
        var totalCount = await operations.LongCountAsync(cancellationToken);
        var offset = (long)(query.Page - 1) * query.PageSize;
        if (offset >= totalCount) return new RestorePage([], totalCount, query.Page, query.PageSize);
        var rows = await operations.Include(operation => operation.Attempts).Include(operation => operation.Pin)
            .OrderByDescending(operation => operation.CreatedAt).ThenBy(operation => operation.Id)
            .Skip(checked((int)offset)).Take(query.PageSize).AsSplitQuery().ToListAsync(cancellationToken);
        return new RestorePage(rows, totalCount, query.Page, query.PageSize);
    }

    public async Task<RestoreOperation?> RequestCancellationAsync(TenantId tenantId, Guid restoreId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await context.RestoreOperations.AsNoTracking().Where(operation => operation.TenantId == tenantId && operation.Id == restoreId)
            .Select(operation => operation.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return null;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, tenantId, owner, cancellationToken)) return null;
        // Loaded under the lock, so a claim, a failure or an expiry that committed first is already visible and the cancellation applies to what really happened.
        var operation = await Operations(context).SingleOrDefaultAsync(entity => entity.TenantId == tenantId && entity.Id == restoreId && entity.AgentId == owner, cancellationToken);
        if (operation is null) return null;
        operation.RequestCancellation(now);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return operation;
    }

    public async Task<RestoreClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, TimeSpan unclaimedExpiry, CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, tenantId, agentId, cancellationToken)) return null;
        if (!await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == agentId && agent.Status == AgentStatus.Active, cancellationToken)) return null;
        // An expired lease means the agent that held it is gone: end it now, so it neither blocks this claim nor keeps its replica pinned.
        var held = await Operations(context).Where(operation => operation.TenantId == tenantId && operation.AgentId == agentId &&
            (operation.Status == RestoreStatus.Leased || operation.Status == RestoreStatus.Running)).ToListAsync(cancellationToken);
        foreach (var operation in held) operation.Expire(now);
        var pending = await Operations(context).Where(operation => operation.TenantId == tenantId && operation.AgentId == agentId && operation.Status == RestoreStatus.Pending)
            .OrderBy(operation => operation.CreatedAt).ThenBy(operation => operation.Id).ToListAsync(cancellationToken);
        foreach (var operation in pending) operation.ExpireUnclaimed(now, unclaimedExpiry);
        await context.SaveChangesAsync(cancellationToken);
        // A restore never runs beside another job of its agent: one live restore attempt, or one live (unexpired) backup attempt, and nothing is handed out.
        var busy = held.Any(operation => !operation.IsTerminal) || await context.BackupRunAttempts.AsNoTracking().AnyAsync(attempt => attempt.TenantId == tenantId &&
            attempt.AgentId == agentId && (attempt.Status == BackupStatus.Leased || attempt.Status == BackupStatus.Running) && attempt.LeaseExpiresAt > now, cancellationToken);
        if (busy)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        RestoreClaim? claim = null;
        foreach (var next in pending.Where(operation => operation.Status == RestoreStatus.Pending))
        {
            var token = Guid.NewGuid();
            next.Claim(token, now, leaseDuration);
            var replica = await context.BackupReplicas.AsNoTracking().SingleOrDefaultAsync(entity => entity.TenantId == tenantId && entity.Id == next.ReplicaId, cancellationToken);
            // The pin keeps the replica out of every delete claim, so this holds unless the database was changed behind the platform's back. If it does not hold, the
            // restore must not be handed to an agent: it ends here, failed, with the target untouched and the pin released.
            if (next.Pin.IsActive && replica is { Status: BackupReplicaStatus.Available or BackupReplicaStatus.DeletePending })
            {
                claim = new RestoreClaim(next, next.CurrentAttempt, replica);
                break;
            }
            next.Complete(token, now, new RestoreCompletion(RestoreStatus.Failed, RestoreFailureCodes.ReplicaUnavailable, RestoreTargetState.NotModified, null));
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claim;
    }

    public async Task<RestoreLeaseOutcome> StartAsync(RestoreLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        DateTimeOffset? expiresAt = null;
        var cancellation = false;
        var result = await MutateAttemptAsync(command, operation =>
        {
            operation.Start(command.LeaseToken, command.Now, command.SoftwareVersion!, command.ProtocolVersion!, command.Platform!);
            operation.Renew(command.LeaseToken, command.Now, leaseDuration);
            expiresAt = operation.CurrentAttempt.LeaseExpiresAt;
            cancellation = operation.CancellationRequestedAt.HasValue;
        }, cancellationToken);
        return result == BackupMutationResult.Accepted ? new RestoreLeaseOutcome(result, expiresAt, cancellation) : new RestoreLeaseOutcome(result);
    }

    public async Task<RestoreLeaseOutcome> RenewLeaseAsync(RestoreLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        DateTimeOffset? expiresAt = null;
        var cancellation = false;
        var result = await MutateAttemptAsync(command, operation =>
        {
            operation.Renew(command.LeaseToken, command.Now, leaseDuration);
            expiresAt = operation.CurrentAttempt.LeaseExpiresAt;
            cancellation = operation.CancellationRequestedAt.HasValue;
        }, cancellationToken);
        return result == BackupMutationResult.Accepted ? new RestoreLeaseOutcome(result, expiresAt, cancellation) : new RestoreLeaseOutcome(result);
    }

    public Task<BackupMutationResult> ReportProgressAsync(RestoreLeaseCommand command, RestoreProgress progress, CancellationToken cancellationToken) =>
        MutateAttemptAsync(command, operation => operation.Progress(command.LeaseToken, command.Now, progress.Stage, progress.Percent), cancellationToken);

    public Task<BackupMutationResult> CompleteAsync(RestoreLeaseCommand command, RestoreCompletion completion, CancellationToken cancellationToken) =>
        MutateAttemptAsync(command, operation =>
        {
            // A repeated completion is accepted when it says the same thing. The report is stored as jsonb, which normalises text (key order, spacing), so the
            // domain's exact-text comparison would call a faithful repeat different: judge the JSON, and hand the domain the stored text when it means the same.
            if (operation.IsTerminal && SameJson(operation.ResultJson, completion.ResultJson)) completion = completion with { ResultJson = operation.ResultJson };
            operation.Complete(command.LeaseToken, command.Now, completion);
        }, cancellationToken);

    private static bool SameJson(string? stored, string? reported)
    {
        if (stored is null || reported is null) return stored is null && reported is null;
        try { return JsonNode.DeepEquals(JsonNode.Parse(stored), JsonNode.Parse(reported)); }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public async Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var scan = PlatformDbContext.CreateForPlatformOperations(options);
        var stale = await scan.RestoreAttempts.AsNoTracking().Where(attempt => (attempt.Status == RestoreStatus.Leased || attempt.Status == RestoreStatus.Running) && attempt.LeaseExpiresAt <= now)
            .OrderBy(attempt => attempt.LeaseExpiresAt).Take(MaxScanBatch).Select(attempt => new { attempt.TenantId, attempt.AgentId, attempt.Id }).ToListAsync(cancellationToken);
        var expired = 0;
        foreach (var attempt in stale)
        {
            var changed = false;
            var result = await MutateAttemptAsync(new RestoreLeaseCommand(attempt.TenantId, attempt.AgentId, attempt.Id, Guid.Empty, now), operation => changed = operation.Expire(now), cancellationToken);
            if (result == BackupMutationResult.Accepted && changed) expired++;
        }
        return expired;
    }

    public async Task<int> ExpireUnclaimedAsync(DateTimeOffset now, TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        var cutoff = now - maximumWait;
        await using var scan = PlatformDbContext.CreateForPlatformOperations(options);
        var waiting = await scan.RestoreOperations.AsNoTracking().Where(operation => operation.Status == RestoreStatus.Pending && operation.CreatedAt <= cutoff)
            .OrderBy(operation => operation.CreatedAt).ThenBy(operation => operation.Id).Take(MaxScanBatch)
            .Select(operation => new { operation.TenantId, operation.AgentId, operation.Id }).ToListAsync(cancellationToken);
        var withdrawn = 0;
        foreach (var candidate in waiting)
        {
            var changed = false;
            var result = await MutateAsync(candidate.TenantId, candidate.AgentId, operation => operation.Id == candidate.Id,
                operation => changed = operation.ExpireUnclaimed(now, maximumWait), cancellationToken);
            if (result == BackupMutationResult.Accepted && changed) withdrawn++;
        }
        return withdrawn;
    }

    /// <summary>
    /// The operation owning <paramref name="command"/>'s attempt, loaded and changed under its agent's lock. Unknown attempt (or one of another tenant or agent) is
    /// NotFound; a lifecycle or lease the domain refuses, or a row somebody changed meanwhile, is a Conflict.
    /// </summary>
    private Task<BackupMutationResult> MutateAttemptAsync(RestoreLeaseCommand command, Action<RestoreOperation> mutate, CancellationToken cancellationToken) =>
        MutateAsync(command.TenantId, command.AgentId, operation => operation.Attempts.Any(attempt => attempt.Id == command.AttemptId && attempt.TenantId == command.TenantId && attempt.AgentId == command.AgentId),
            operation =>
            {
                // Only the operation's current attempt can be addressed; with one attempt this is a guard for the day there are more.
                if (operation.CurrentAttempt.Id != command.AttemptId) throw new InvalidOperationException("The attempt is no longer current.");
                mutate(operation);
            }, cancellationToken);

    private async Task<BackupMutationResult> MutateAsync(TenantId tenantId, AgentId agentId, Expression<Func<RestoreOperation, bool>> selector, Action<RestoreOperation> mutate, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, tenantId, agentId, cancellationToken)) return BackupMutationResult.NotFound;
        var operation = await Operations(context).Where(entity => entity.TenantId == tenantId && entity.AgentId == agentId).Where(selector).SingleOrDefaultAsync(cancellationToken);
        if (operation is null) return BackupMutationResult.NotFound;
        try { mutate(operation); }
        catch (InvalidOperationException) { return BackupMutationResult.Conflict; }
        catch (ArgumentException) { return BackupMutationResult.Conflict; }
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return BackupMutationResult.Conflict; }
        await transaction.CommitAsync(cancellationToken);
        return BackupMutationResult.Accepted;
    }

    private static IQueryable<RestoreOperation> Operations(PlatformDbContext context) =>
        context.RestoreOperations.Include(operation => operation.Attempts).Include(operation => operation.Pin).AsSplitQuery();

    private static async Task<bool> LockArtifactAsync(PlatformDbContext context, TenantId tenantId, Guid artifactId, CancellationToken cancellationToken)
    {
        // NO KEY UPDATE: conflicts with the artifact-level writers' FOR UPDATE (so they and this serialize), but not with the foreign-key checks of inserts that reference the row.
        var locked = await context.Database.SqlQuery<Guid>(
            $"SELECT \"Id\" AS \"Value\" FROM backup_artifacts WHERE \"Id\" = {artifactId} AND \"TenantId\" = {tenantId.Value} FOR NO KEY UPDATE").ToListAsync(cancellationToken);
        return locked.Count > 0;
    }

    private static async Task<bool> LockReplicaAsync(PlatformDbContext context, TenantId tenantId, Guid artifactId, Guid replicaId, CancellationToken cancellationToken)
    {
        var locked = await context.Database.SqlQuery<Guid>(
            $"SELECT \"Id\" AS \"Value\" FROM backup_replicas WHERE \"Id\" = {replicaId} AND \"ArtifactId\" = {artifactId} AND \"TenantId\" = {tenantId.Value} FOR UPDATE").ToListAsync(cancellationToken);
        return locked.Count > 0;
    }
}
