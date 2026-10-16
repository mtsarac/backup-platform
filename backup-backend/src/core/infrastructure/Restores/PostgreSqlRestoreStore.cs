using System.Linq.Expressions;
using System.Text.Json.Nodes;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Support;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Support;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Restores;

/// <summary>
/// Lifecycle writes take the tenant-qualified agent row lock first, like backups do, which fences one agent's polls, callbacks, cancellations and expiry sweeps
/// (and makes "no restore beside a backup of the same agent" a decision taken under one lock). Creating a restore additionally locks, in this order and never the
/// other way round: the artifact row (<c>FOR NO KEY UPDATE</c>, the order artifact-level mutators already use) and then the chosen replica row
/// (<c>FOR UPDATE</c>). The replica lock is what the retention pin relies on: a delete claim holds that row's lock while it checks for pins, so a pin can never
/// slip in between its check and its update, and the other way round the restore sees a replica a claim already took and refuses it.
/// </summary>
public sealed class PostgreSqlRestoreStore(DbContextOptions<PlatformDbContext> options, IBackupStorageResolver? storage = null) : IRestoreStore
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
        if (command.SupportSessionId is { } supportId)
        {
            // First lock of all (shared): a restore through a support session gets through before the session can end, or is refused after. A close takes the same order.
            var (session, denial) = await SupportSessionGate.LockAndEvaluateAsync(context, supportId, tenantId, new UserId(command.RequestedByUserId!.Value),
                SupportPermissions.RestoreBackup, command.Now, cancellationToken);
            if (session is null) return new RestoreCreateResult(RestoreCreateOutcome.ArtifactNotFound);
            if (denial is { } refused) return new RestoreCreateResult(RestoreCreateOutcome.SupportAccessDenied, SupportDenial: refused);
        }
        if (!await context.BackupArtifacts.AnyAsync(artifact => artifact.TenantId == tenantId && artifact.Id == artifactId, cancellationToken))
            return new RestoreCreateResult(RestoreCreateOutcome.ArtifactNotFound);
        if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, tenantId, agentId, cancellationToken))
            return new RestoreCreateResult(RestoreCreateOutcome.AgentNotFound);
        // The key is looked up first: replaying a request returns its original operation even when the agent or the replica has since become unusable.
        var existing = await Operations(context).AsNoTracking()
            .SingleOrDefaultAsync(operation => operation.TenantId == tenantId && operation.ArtifactId == artifactId && operation.Purpose == command.Purpose &&
                operation.IdempotencyKey == command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return string.Equals(existing.RequestFingerprint, command.RequestFingerprint, StringComparison.Ordinal) && existing.SupportSessionId == command.SupportSessionId
                ? new RestoreCreateResult(RestoreCreateOutcome.Replayed, existing)
                : new RestoreCreateResult(RestoreCreateOutcome.IdempotencyConflict);
        }
        var status = await context.Agents.AsNoTracking().Where(agent => agent.TenantId == tenantId && agent.Id == agentId)
            .Select(agent => agent.Status).SingleAsync(cancellationToken);
        if (status != AgentStatus.Active) return new RestoreCreateResult(RestoreCreateOutcome.AgentNotActive);
        var source = await context.BackupArtifacts.AsNoTracking().SingleAsync(artifact => artifact.TenantId == tenantId && artifact.Id == artifactId, cancellationToken);
        var engine = BackupArtifactEngine.Read(source.SourceMetadataJson);
        if (engine is null) return new RestoreCreateResult(RestoreCreateOutcome.SourceRejected, Rejection: RestoreSourceRejection.BackupTypeUnsupported);
        if (engine == "postgres" && command.Target.IntegratedSecurity) throw new InvalidRestoreRequestException("target.databaseCredentialReference");
        if (command.Purpose != RestorePurpose.Discovery)
        {
            try { RestoreOperation.ValidateDatabaseName(command.DatabaseName, engine, verification: command.Purpose == RestorePurpose.Verification); }
            catch (ArgumentException) { throw new InvalidRestoreRequestException("databaseName"); }
        }
        var capabilityIdentifier = RestoreAgentCapability.ForPurpose(command.Purpose, engine);
        var capable = await context.AgentCapabilities.AsNoTracking().AnyAsync(capability => capability.TenantId == tenantId && capability.AgentId == agentId &&
            capability.Identifier == capabilityIdentifier && capability.Support == CapabilitySupport.Supported, cancellationToken);
        if (!capable) return new RestoreCreateResult(RestoreCreateOutcome.AgentCannotRestore);
        if (command.PrecheckId is { } precheckId)
        {
            var precheck = await context.RestoreOperations.AsNoTracking()
                .SingleOrDefaultAsync(operation => operation.TenantId == tenantId && operation.Id == precheckId, cancellationToken);
            if (!MatchesPrecheck(precheck, command, engine) || precheck!.SupportSessionId != command.SupportSessionId) return new RestoreCreateResult(RestoreCreateOutcome.PrecheckRejected);
        }

        // Lock order: agent (held), artifact, replica. Artifact-level mutators (manual delete, cancel) take the artifact first and then write replicas; taking the replica
        // first here would let a restore and a delete wait for each other's row.
        if (!await LockArtifactAsync(context, tenantId, artifactId, cancellationToken)) return new RestoreCreateResult(RestoreCreateOutcome.ArtifactNotFound);
        var selection = await SelectReplicaAsync(context, command, cancellationToken);
        if (selection.Rejection is { } rejection) return new RestoreCreateResult(RestoreCreateOutcome.SourceRejected, Rejection: rejection);

        var operation = new RestoreOperation(Guid.NewGuid(), tenantId, artifactId, selection.Replica!.Id, selection.Access, agentId, command.Target, command.DatabaseName, command.Mode,
            command.OverwriteConfirmed, command.Options, command.IdempotencyKey, command.RequestFingerprint, command.RequestedByUserId, command.Now, command.Purpose, command.PrecheckId, command.SupportSessionId, engine);
        operation.QueueFirstAttempt(Guid.NewGuid(), command.Now);
        context.RestoreOperations.Add(operation);
        AddAudit(context, operation, command.Now, command.RequestedByUserId, command.Purpose switch
        {
            RestorePurpose.Precheck => "restore.precheck.requested",
            RestorePurpose.Discovery => "restore.discovery.requested",
            RestorePurpose.Verification => "restore.verification.requested",
            _ => "restore.requested"
        }, operation.SupportSessionId);
        if (command.OverwriteConfirmed) AddAudit(context, operation, command.Now, command.RequestedByUserId, "restore.overwrite_confirmed");
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
            BackupReplica? candidate = null;
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
        var operations = context.RestoreOperations.AsNoTracking().Where(operation => operation.TenantId == tenantId && operation.Purpose == query.Purpose);
        if (query.SupportSessionId is { } supportSessionId) operations = operations.Where(operation => operation.SupportSessionId == supportSessionId);
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

    public async Task<RestoreOperation?> RequestCancellationAsync(TenantId tenantId, Guid restoreId, DateTimeOffset now, CancellationToken cancellationToken, Guid? requestedByUserId = null,
        Guid? supportSessionId = null)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await context.RestoreOperations.AsNoTracking().Where(operation => operation.TenantId == tenantId && operation.Id == restoreId &&
            (supportSessionId == null || operation.SupportSessionId == supportSessionId)).Select(operation => operation.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return null;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, tenantId, owner, cancellationToken)) return null;
        // Loaded under the lock, so a claim, a failure or an expiry that committed first is already visible and the cancellation applies to what really happened.
        var operation = await Operations(context).SingleOrDefaultAsync(entity => entity.TenantId == tenantId && entity.Id == restoreId && entity.AgentId == owner, cancellationToken);
        if (operation is null) return null;
        var changed = !operation.IsTerminal && operation.CancellationRequestedAt is null;
        operation.RequestCancellation(now);
        if (changed) AddAudit(context, operation, now, requestedByUserId, "restore.cancel_requested", supportSessionId);
        AddTerminalAudits(context);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return operation;
    }

    public async Task<RestoreClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, TimeSpan unclaimedExpiry, CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        var unreadableReplicas = new HashSet<Guid>();
        while (true)
        {
            var probeCandidate = await PrepareNextClaimAsync(tenantId, agentId, now, leaseDuration, unclaimedExpiry, unreadableReplicas, cancellationToken);
            if (probeCandidate.Outcome != ClaimPreparationOutcome.ProbeNeeded)
                return probeCandidate.Claim;

            if (!await CanReadAsync(probeCandidate.CandidateReplica!, cancellationToken))
            {
                unreadableReplicas.Add(probeCandidate.CandidateReplica!.Id);
                continue;
            }

            var finalized = await FinalizeClaimAsync(tenantId, agentId, probeCandidate.OperationId, probeCandidate.CandidateReplica!.Id,
                now, leaseDuration, cancellationToken);
            if (finalized.Outcome == ClaimFinalizeOutcome.Claimed)
                return finalized.Claim;
        }
    }

    private enum ClaimPreparationOutcome { Empty, Busy, Claimed, ProbeNeeded }
    private sealed record ClaimPreparationResult(ClaimPreparationOutcome Outcome, RestoreClaim? Claim = null, Guid OperationId = default, BackupReplica? CandidateReplica = null);

    private enum ClaimFinalizeOutcome { Claimed, Retry }
    private sealed record ClaimFinalizeResult(ClaimFinalizeOutcome Outcome, RestoreClaim? Claim = null);

    private async Task<ClaimPreparationResult> PrepareNextClaimAsync(
        TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, TimeSpan unclaimedExpiry,
        HashSet<Guid> unreadableReplicas, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, tenantId, agentId, cancellationToken))
            return new(ClaimPreparationOutcome.Empty);
        if (!await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == agentId && agent.Status == AgentStatus.Active, cancellationToken))
            return new(ClaimPreparationOutcome.Empty);

        var held = await Operations(context).Where(operation => operation.TenantId == tenantId && operation.AgentId == agentId &&
            (operation.Status == RestoreStatus.Leased || operation.Status == RestoreStatus.Running)).ToListAsync(cancellationToken);
        foreach (var operation in held) operation.Expire(now);
        var pending = await Operations(context).Where(operation => operation.TenantId == tenantId && operation.AgentId == agentId && operation.Status == RestoreStatus.Pending)
            .OrderBy(operation => operation.CreatedAt).ThenBy(operation => operation.Id).ToListAsync(cancellationToken);
        foreach (var operation in pending) operation.ExpireUnclaimed(now, unclaimedExpiry);
        AddTerminalAudits(context);
        await context.SaveChangesAsync(cancellationToken);

        var busy = held.Any(operation => !operation.IsTerminal) || await context.BackupRunAttempts.AsNoTracking().AnyAsync(attempt => attempt.TenantId == tenantId &&
            attempt.AgentId == agentId && (attempt.Status == BackupStatus.Leased || attempt.Status == BackupStatus.Running) && attempt.LeaseExpiresAt > now, cancellationToken);
        if (busy)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(ClaimPreparationOutcome.Busy);
        }

        var supported = await context.AgentCapabilities.AsNoTracking()
            .Where(capability => capability.TenantId == tenantId && capability.AgentId == agentId && capability.Support == CapabilitySupport.Supported)
            .Select(capability => capability.Identifier).ToListAsync(cancellationToken);

        foreach (var next in pending.Where(operation => operation.Status == RestoreStatus.Pending))
        {
            if (!next.Pin.IsActive || !await LockArtifactAsync(context, tenantId, next.ArtifactId, cancellationToken)) continue;

            var artifact = await context.BackupArtifacts.AsNoTracking().Include(entity => entity.Replicas)
                .SingleAsync(entity => entity.TenantId == tenantId && entity.Id == next.ArtifactId, cancellationToken);
            var engine = BackupArtifactEngine.Read(artifact.SourceMetadataJson);
            if (engine is null || !supported.Contains(RestoreAgentCapability.ForPurpose(next.Purpose, engine), StringComparer.Ordinal)) continue;
            var candidates = artifact.Replicas
                .Where(candidate => !unreadableReplicas.Contains(candidate.Id) &&
                    (candidate.Id == next.ReplicaId && candidate.Status is BackupReplicaStatus.Available or BackupReplicaStatus.DeletePending ||
                     RestoreSourceEvaluator.Evaluate(artifact, candidate, agentId, now).Eligible))
                .OrderBy(candidate => candidate.Id == next.ReplicaId ? 0 : 1)
                .ThenBy(candidate => candidate.ExecutionLocation == BackupStorageExecutionLocation.Agent ? 0 : 1)
                .ThenBy(candidate => ReplicaRepairSourceEvaluator.GetProviderPriority(candidate.StorageProvider))
                .ThenByDescending(candidate => candidate.CreatedAt);

            BackupReplica? chosenCandidate = null;
            foreach (var candidate in candidates)
            {
                if (!await LockReplicaAsync(context, tenantId, next.ArtifactId, candidate.Id, cancellationToken)) continue;
                var locked = await context.BackupReplicas.AsNoTracking()
                    .SingleAsync(entity => entity.TenantId == tenantId && entity.ArtifactId == next.ArtifactId && entity.Id == candidate.Id, cancellationToken);
                var decision = RestoreSourceEvaluator.Evaluate(artifact, locked, agentId, now);
                var alreadyPinned = locked.Id == next.ReplicaId &&
                    locked.Status is BackupReplicaStatus.Available or BackupReplicaStatus.DeletePending &&
                    locked.VerificationState == "Verified" && locked.VerifiedAt is not null;
                if (!alreadyPinned && !decision.Eligible) continue;

                chosenCandidate = locked;
                break;
            }

            if (chosenCandidate is not null)
            {
                if (chosenCandidate.ExecutionLocation == BackupStorageExecutionLocation.Agent || storage is null)
                {
                    var token = Guid.NewGuid();
                    if (chosenCandidate.Id != next.ReplicaId)
                    {
                        var decision = RestoreSourceEvaluator.Evaluate(artifact, chosenCandidate, agentId, now);
                        if (next.SwitchReplica(chosenCandidate, decision.Access!.Value))
                            AddAudit(context, next, now, null, "restore.replica_fallback", next.SupportSessionId);
                    }
                    next.Claim(token, now, leaseDuration);
                    var claim = new RestoreClaim(next, next.CurrentAttempt, chosenCandidate, engine);
                    AddTerminalAudits(context);
                    await context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new(ClaimPreparationOutcome.Claimed, claim);
                }

                await transaction.CommitAsync(cancellationToken);
                return new(ClaimPreparationOutcome.ProbeNeeded, OperationId: next.Id, CandidateReplica: chosenCandidate);
            }

            // No readable/eligible replicas remain for this pending operation. Mark it Failed and release pin.
            var failToken = Guid.NewGuid();
            next.Claim(failToken, now, leaseDuration);
            next.Complete(failToken, now, new RestoreCompletion(RestoreStatus.Failed, RestoreFailureCodes.ReplicaUnavailable, RestoreTargetState.NotModified, null, TemporaryDatabaseRemoved: true));
            AddTerminalAudits(context);
            await context.SaveChangesAsync(cancellationToken);
            continue;
        }

        await transaction.CommitAsync(cancellationToken);
        return new(ClaimPreparationOutcome.Empty);
    }

    private async Task<ClaimFinalizeResult> FinalizeClaimAsync(
        TenantId tenantId, AgentId agentId, Guid operationId, Guid candidateReplicaId,
        DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, tenantId, agentId, cancellationToken))
            return new(ClaimFinalizeOutcome.Retry);
        if (!await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == agentId && agent.Status == AgentStatus.Active, cancellationToken))
            return new(ClaimFinalizeOutcome.Retry);

        var held = await Operations(context).Where(operation => operation.TenantId == tenantId && operation.AgentId == agentId &&
            (operation.Status == RestoreStatus.Leased || operation.Status == RestoreStatus.Running)).ToListAsync(cancellationToken);
        foreach (var operation in held) operation.Expire(now);
        var busy = held.Any(operation => !operation.IsTerminal) || await context.BackupRunAttempts.AsNoTracking().AnyAsync(attempt => attempt.TenantId == tenantId &&
            attempt.AgentId == agentId && (attempt.Status == BackupStatus.Leased || attempt.Status == BackupStatus.Running) && attempt.LeaseExpiresAt > now, cancellationToken);
        if (busy)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(ClaimFinalizeOutcome.Retry);
        }

        var op = await Operations(context).SingleOrDefaultAsync(o => o.TenantId == tenantId && o.Id == operationId && o.AgentId == agentId, cancellationToken);
        if (op is null || op.Status != RestoreStatus.Pending || !op.Pin.IsActive || op.CancellationRequestedAt is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(ClaimFinalizeOutcome.Retry);
        }

        var supported = await context.AgentCapabilities.AsNoTracking()
            .Where(capability => capability.TenantId == tenantId && capability.AgentId == agentId && capability.Support == CapabilitySupport.Supported)
            .Select(capability => capability.Identifier).ToListAsync(cancellationToken);

        if (!await LockArtifactAsync(context, tenantId, op.ArtifactId, cancellationToken) ||
            !await LockReplicaAsync(context, tenantId, op.ArtifactId, candidateReplicaId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new(ClaimFinalizeOutcome.Retry);
        }

        var artifact = await context.BackupArtifacts.AsNoTracking().Include(entity => entity.Replicas)
            .SingleAsync(entity => entity.TenantId == tenantId && entity.Id == op.ArtifactId, cancellationToken);
        var engine = BackupArtifactEngine.Read(artifact.SourceMetadataJson);
        if (engine is null || !supported.Contains(RestoreAgentCapability.ForPurpose(op.Purpose, engine), StringComparer.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return new(ClaimFinalizeOutcome.Retry);
        }
        var lockedReplica = await context.BackupReplicas.AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.TenantId == tenantId && entity.ArtifactId == op.ArtifactId && entity.Id == candidateReplicaId, cancellationToken);
        if (lockedReplica is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(ClaimFinalizeOutcome.Retry);
        }

        var decision = RestoreSourceEvaluator.Evaluate(artifact, lockedReplica, agentId, now);
        var alreadyPinned = lockedReplica.Id == op.ReplicaId &&
            lockedReplica.Status is BackupReplicaStatus.Available or BackupReplicaStatus.DeletePending &&
            lockedReplica.VerificationState == "Verified" && lockedReplica.VerifiedAt is not null;
        if (!alreadyPinned && !decision.Eligible)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(ClaimFinalizeOutcome.Retry);
        }

        if (!alreadyPinned)
        {
            if (!op.SwitchReplica(lockedReplica, decision.Access!.Value))
            {
                await transaction.CommitAsync(cancellationToken);
                return new(ClaimFinalizeOutcome.Retry);
            }
            AddAudit(context, op, now, null, "restore.replica_fallback", op.SupportSessionId);
        }

        var token = Guid.NewGuid();
        op.Claim(token, now, leaseDuration);
        if (op.Pin.IsActive && lockedReplica.Status is BackupReplicaStatus.Available or BackupReplicaStatus.DeletePending)
        {
            var claim = new RestoreClaim(op, op.CurrentAttempt, lockedReplica, engine);
            AddTerminalAudits(context);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(ClaimFinalizeOutcome.Claimed, claim);
        }

        op.Complete(token, now, new RestoreCompletion(RestoreStatus.Failed, RestoreFailureCodes.ReplicaUnavailable, RestoreTargetState.NotModified, null, TemporaryDatabaseRemoved: true));
        AddTerminalAudits(context);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ClaimFinalizeOutcome.Retry);
    }
    private async Task<bool> CanReadAsync(BackupReplica replica, CancellationToken cancellationToken)
    {
        // Agent-local files are checked by their owning agent; backend replicas are probed before any restore is handed out.
        if (replica.ExecutionLocation == BackupStorageExecutionLocation.Agent || storage is null) return true;
        try
        {
            var provider = storage.Resolve(replica.TenantId, replica.StorageProvider, replica.StorageTarget);
            // Central storage can be mounted only on the agent; absence of a backend mount is not evidence of source failure.
            if (provider is null) return replica.StorageProvider == "central";
            if (!provider.Capabilities.Supports(StorageCapabilityIds.Read)) return false;
            await using var source = await provider.OpenReadAsync(new StorageObjectKey(replica.Key), cancellationToken);
            return source.CanRead;
        }
        catch (Exception exception) when (exception is StorageException or IOException or UnauthorizedAccessException) { return false; }
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

    public async Task<BackupMutationResult> CompleteAsync(RestoreLeaseCommand command, RestoreCompletion completion, CancellationToken cancellationToken)
    {
        string? engine = "mssql";
        if (completion.Status == RestoreStatus.Succeeded && completion.TargetState == RestoreTargetState.NotModified)
        {
            await using var context = PlatformDbContext.CreateForPlatformOperations(options);
            var metadata = await (from operation in context.RestoreOperations.AsNoTracking()
                                  join artifact in context.BackupArtifacts on new { operation.TenantId, Id = operation.ArtifactId } equals new { artifact.TenantId, artifact.Id }
                                  where operation.TenantId == command.TenantId && operation.AgentId == command.AgentId && operation.Purpose == RestorePurpose.Precheck &&
                                      operation.Attempts.Any(attempt => attempt.Id == command.AttemptId)
                                  select artifact.SourceMetadataJson).SingleOrDefaultAsync(cancellationToken);
            if (metadata is not null) engine = BackupArtifactEngine.Read(metadata);
        }
        return await MutateAttemptAsync(command, operation =>
        {
            if (operation.Purpose != RestorePurpose.Execution && completion.Status == RestoreStatus.Succeeded)
            {
                var result = RestoreInputValidation.ReadResult(completion.ResultJson);
                if (operation.Purpose == RestorePurpose.Precheck && !SuccessfulPrecheck(result, operation.Options, engine) ||
                    operation.Purpose == RestorePurpose.Discovery && result?.Discovery is null ||
                    operation.Purpose == RestorePurpose.Verification && !SuccessfulVerification(result))
                    throw new ArgumentException("A successful read-only operation requires its actual report.");
            }
            // A repeated completion is accepted when it says the same thing. The report is stored as jsonb, which normalises text (key order, spacing), so the
            // domain's exact-text comparison would call a faithful repeat different: judge the JSON, and hand the domain the stored text when it means the same.
            if (operation.IsTerminal && SameJson(operation.ResultJson, completion.ResultJson)) completion = completion with { ResultJson = operation.ResultJson };
            operation.Complete(command.LeaseToken, command.Now, completion);
        }, cancellationToken);
    }

    /// <summary>A verification only succeeds with the engine's own checks in the report and every one of them passed.</summary>
    private static bool SuccessfulVerification(BackupPlatform.Contracts.Restores.RestoreTaskResult? result) =>
        result?.Verification is { Checks.Count: > 0 } verification && verification.Checks.All(check => check.Status == "Passed");

    private static bool SameJson(string? stored, string? reported)
    {
        if (stored is null || reported is null) return stored is null && reported is null;
        try { return JsonNode.DeepEquals(JsonNode.Parse(stored), JsonNode.Parse(reported)); }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public async Task<RestoreCleanupClaim?> ClaimCleanupAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, tenantId, agentId, cancellationToken)) return null;
        if (!await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == agentId && agent.Status == AgentStatus.Active, cancellationToken)) return null;
        // A removal is housekeeping: it never runs beside a live restore or backup of the same agent, and the pipeline's own finally block already drops its own database.
        var busy = await context.RestoreAttempts.AsNoTracking().AnyAsync(attempt => attempt.TenantId == tenantId && attempt.AgentId == agentId &&
                (attempt.Status == RestoreStatus.Leased || attempt.Status == RestoreStatus.Running) && attempt.LeaseExpiresAt > now, cancellationToken) ||
            await context.BackupRunAttempts.AsNoTracking().AnyAsync(attempt => attempt.TenantId == tenantId && attempt.AgentId == agentId &&
                (attempt.Status == BackupStatus.Leased || attempt.Status == BackupStatus.Running) && attempt.LeaseExpiresAt > now, cancellationToken);
        if (busy) return null;
        var owed = await Operations(context).Where(operation => operation.TenantId == tenantId && operation.AgentId == agentId && operation.Cleanup.State == RestoreCleanupState.Pending)
            .OrderBy(operation => operation.CompletedAt).ThenBy(operation => operation.Id).ToListAsync(cancellationToken);
        var next = owed.FirstOrDefault(operation => operation.IsTerminal && operation.Cleanup.CanLease(now));
        if (next is null) return null;
        var metadata = await context.BackupArtifacts.AsNoTracking().Where(artifact => artifact.TenantId == tenantId && artifact.Id == next.ArtifactId)
            .Select(artifact => artifact.SourceMetadataJson).SingleOrDefaultAsync(cancellationToken);
        var engine = metadata is null ? null : BackupArtifactEngine.Read(metadata);
        if (engine is null) return null;
        var token = Guid.NewGuid();
        if (!next.TryLeaseCleanup(token, now, leaseDuration)) return null;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RestoreCleanupClaim(next, token, engine);
    }

    public async Task<BackupMutationResult> CompleteCleanupAsync(TenantId tenantId, AgentId agentId, Guid operationId, Guid leaseToken, bool removed, string? failureCode, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, tenantId, agentId, cancellationToken)) return BackupMutationResult.NotFound;
        var operation = await Operations(context).SingleOrDefaultAsync(entity => entity.TenantId == tenantId && entity.AgentId == agentId && entity.Id == operationId, cancellationToken);
        if (operation is null || operation.Cleanup.Kind == RestoreCleanupKind.None) return BackupMutationResult.NotFound;
        var before = operation.Cleanup.State;
        try { operation.ReportCleanup(leaseToken, removed, failureCode, now); }
        catch (InvalidOperationException) { return BackupMutationResult.Conflict; }
        if (before != RestoreCleanupState.Completed) AddCleanupAudit(context, operation, now, null, removed ? "restore.cleanup.completed" : "restore.cleanup.failed");
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return BackupMutationResult.Conflict; }
        await transaction.CommitAsync(cancellationToken);
        return BackupMutationResult.Accepted;
    }

    public async Task<RestoreCleanupRequestResult> RequestCleanupAsync(TenantId tenantId, Guid restoreId, DateTimeOffset now, TimeSpan rollbackRetention, Guid? requestedByUserId,
        CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await context.RestoreOperations.AsNoTracking().Where(operation => operation.TenantId == tenantId && operation.Id == restoreId)
            .Select(operation => operation.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return new RestoreCleanupRequestResult(RestoreCleanupRequestOutcome.NotFound);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await PostgreSqlBackupRunStore.LockAgentAsync(context, tenantId, owner, cancellationToken)) return new RestoreCleanupRequestResult(RestoreCleanupRequestOutcome.NotFound);
        var operation = await Operations(context).SingleOrDefaultAsync(entity => entity.TenantId == tenantId && entity.Id == restoreId && entity.AgentId == owner, cancellationToken);
        if (operation is null) return new RestoreCleanupRequestResult(RestoreCleanupRequestOutcome.NotFound);
        // A rollback copy is the only data left of a replaced database: it stays untouched until its guarded retention has passed, whatever the caller's role.
        DateTimeOffset? retainedUntil = operation.Cleanup is { Kind: RestoreCleanupKind.RollbackCopy, State: RestoreCleanupState.Retained } && operation.CompletedAt is { } completedAt
            ? completedAt + rollbackRetention : null;
        if (retainedUntil > now) return new RestoreCleanupRequestResult(RestoreCleanupRequestOutcome.RetentionNotElapsed, operation, retainedUntil);
        if (!operation.RequestCleanup(now, requestedByUserId)) return new RestoreCleanupRequestResult(RestoreCleanupRequestOutcome.NotApplicable, operation);
        AddCleanupAudit(context, operation, now, requestedByUserId, "restore.cleanup.requested");
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return new RestoreCleanupRequestResult(RestoreCleanupRequestOutcome.NotApplicable, operation); }
        await transaction.CommitAsync(cancellationToken);
        return new RestoreCleanupRequestResult(RestoreCleanupRequestOutcome.Accepted, operation, retainedUntil);
    }

    private static void AddCleanupAudit(PlatformDbContext context, RestoreOperation operation, DateTimeOffset now, Guid? actorId, string action) =>
        context.AuditEvents.Add(AuditEvent.Record(now, actorId is { } user ? new UserId(user) : UserId.System, actorId is null ? AuditActorKind.System : AuditActorKind.User, action,
            operation.TenantId, "restore", operation.Id.ToString("D"), new Dictionary<string, object?>
            {
                ["artifactId"] = operation.ArtifactId, ["agentId"] = operation.AgentId.Value, ["kind"] = operation.Cleanup.Kind.ToString(), ["database"] = operation.Cleanup.DatabaseName,
                ["state"] = operation.Cleanup.State.ToString(), ["attempts"] = operation.Cleanup.Attempts, ["failure"] = operation.Cleanup.LastFailureCode
            }));

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
        AddTerminalAudits(context);
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return BackupMutationResult.Conflict; }
        await transaction.CommitAsync(cancellationToken);
        return BackupMutationResult.Accepted;
    }

    private static bool SuccessfulPrecheck(BackupPlatform.Contracts.Restores.RestoreTaskResult? result, RestoreOptions options, string? engine) =>
        engine is ("mssql" or "postgres") && result is { PreCheck.Count: > 0, SourceBackup: not null, FileMappings: not null } && (engine == "postgres" || result.FileMappings.Count > 0) &&
        result.PreCheck.All(item => item.Status is "Passed" or "Warning" or "Skipped") &&
        (!options.VerifyChecksum || result.ChecksumVerified) && (!options.VerifyBackup || result.BackupVerified);

    private static bool MatchesPrecheck(RestoreOperation? precheck, RestoreCommand command, string engine) =>
        command.Purpose == RestorePurpose.Execution &&
        precheck is { Purpose: RestorePurpose.Precheck, Status: RestoreStatus.Succeeded, TargetState: RestoreTargetState.NotModified } &&
        precheck.ArtifactId == command.ArtifactId && precheck.AgentId == command.AgentId && precheck.Target == command.Target &&
        precheck.DatabaseName == command.DatabaseName && precheck.Mode == command.Mode && precheck.Options == command.Options &&
        (command.ReplicaId is null || command.ReplicaId == precheck.ReplicaId) &&
        SuccessfulPrecheck(RestoreInputValidation.ReadResult(precheck.ResultJson), command.Options, engine);

    private static void AddAudit(PlatformDbContext context, RestoreOperation operation, DateTimeOffset now, Guid? actorId, string action, Guid? supportSessionId = null)
    {
        if (actorId is null && supportSessionId is not null) return;
        var details = new Dictionary<string, object?>
        {
            ["artifactId"] = operation.ArtifactId, ["replicaId"] = operation.ReplicaId, ["agentId"] = operation.AgentId.Value,
            ["purpose"] = operation.Purpose.ToString(), ["mode"] = operation.Mode.ToString(), ["precheckId"] = operation.PrecheckId,
            ["result"] = action == "restore.replica_fallback" ? "changed" : "accepted"
        };
        if (supportSessionId is not { } support)
        {
            context.AuditEvents.Add(AuditEvent.Record(now, actorId is { } user ? new UserId(user) : UserId.System,
                actorId is null ? AuditActorKind.System : AuditActorKind.User, action, operation.TenantId,
                "restore", operation.Id.ToString("D"), details));
            return;
        }
        // Support access: the real PlatformAdmin as actor, the support session, and the restore as the resource. No target host or credential is recorded.
        details["supportSessionId"] = support;
        details["result"] = "accepted";
        var supportAction = action switch
        {
            "restore.precheck.requested" => SupportAuditActions.RestorePrecheckRequested,
            "restore.cancel_requested" => SupportAuditActions.RestoreCancelRequested,
            _ => SupportAuditActions.RestoreRequested
        };
        context.AuditEvents.Add(AuditEvent.Record(now, new UserId(actorId!.Value), AuditActorKind.PlatformAdmin, supportAction, operation.TenantId,
            SupportAuditActions.RestoreTargetType, operation.Id.ToString("D"), details, support));
    }

    /// <summary>Terminal transitions are audited in the same save; support completion remains independent of session expiry.</summary>
    internal static void AddTerminalAudits(PlatformDbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<RestoreOperation>().ToList())
        {
            var operation = entry.Entity;
            if (entry.State != EntityState.Modified || !operation.IsTerminal) continue;
            if (entry.OriginalValues.GetValue<RestoreStatus>(nameof(RestoreOperation.Status)) is RestoreStatus.Succeeded or RestoreStatus.Failed or RestoreStatus.Cancelled or RestoreStatus.AgentLost) continue;
            if (operation.SupportSessionId is not { } support)
            {
                context.AuditEvents.Add(AuditEvent.Record(operation.CompletedAt ?? DateTimeOffset.UtcNow, UserId.System, AuditActorKind.System,
                    operation.Purpose switch
                    {
                        RestorePurpose.Precheck => "restore.precheck.completed",
                        RestorePurpose.Discovery => "restore.discovery.completed",
                        RestorePurpose.Verification => "restore.verification.completed",
                        _ => "restore.completed"
                    }, operation.TenantId, "restore", operation.Id.ToString("D"), new Dictionary<string, object?>
                    {
                        ["artifactId"] = operation.ArtifactId, ["attemptId"] = operation.CurrentAttempt.Id,
                        ["result"] = operation.Status.ToString(), ["targetState"] = operation.TargetState?.ToString(), ["failure"] = operation.FailureCode,
                        ["outcome"] = RestoreService.VerificationOutcome(operation), ["cleanupState"] = operation.Cleanup.Kind == RestoreCleanupKind.None ? null : operation.Cleanup.State.ToString(),
                        ["durationMs"] = operation.StartedAt is { } started && operation.CompletedAt is { } completed ? (long)(completed - started).TotalMilliseconds : null
                    }));
                continue;
            }
            if (operation.Purpose != RestorePurpose.Execution) continue;
            if (operation.RequestedByUserId is not { } actor) continue;
            context.AuditEvents.Add(AuditEvent.Record(operation.CompletedAt ?? DateTimeOffset.UtcNow, new UserId(actor), AuditActorKind.PlatformAdmin,
                operation.Status == RestoreStatus.Succeeded ? SupportAuditActions.RestoreCompleted : SupportAuditActions.RestoreFailed, operation.TenantId,
                SupportAuditActions.RestoreTargetType, operation.Id.ToString("D"), new Dictionary<string, object?>
                {
                    ["supportSessionId"] = support, ["artifactId"] = operation.ArtifactId, ["result"] = operation.Status.ToString(),
                    ["targetState"] = operation.TargetState?.ToString(), ["failure"] = operation.FailureCode
                }, support));
        }
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
