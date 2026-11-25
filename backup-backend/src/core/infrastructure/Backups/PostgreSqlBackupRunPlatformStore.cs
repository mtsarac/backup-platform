using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Flags;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.Infrastructure.Notifications;
using BackupPlatform.Notifications;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Infrastructure.Storage;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ApplicationCompletion = BackupPlatform.Application.Backups.BackupCompletion;
using DomainCompletion = BackupPlatform.Domain.Backups.BackupCompletion;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>
/// Agent- and worker-driven run flows on the unfiltered platform-operations context. Tenant requests use <see cref="PostgreSqlBackupRunStore"/>.
/// All lifecycle writes acquire the tenant-qualified agent row lock first, fencing concurrent polls and callbacks of one agent.
/// Slot-consuming transitions (claim) additionally take one transaction-scoped advisory lock FIRST, so the global, tenant and
/// agent limits are decided against a consistent count of Leased/Running attempts by any number of backend instances. Releasing
/// transitions (complete, expire, cancel) never wait on that lock, so the two lock orders cannot cycle.
/// </summary>
public sealed class PostgreSqlBackupRunPlatformStore(DbContextOptions<PlatformDbContext> options, BackupExecutionOptions? limits = null, Func<double>? jitter = null, NotificationOptions? notifications = null, ILogger<PostgreSqlBackupRunPlatformStore>? log = null, FeatureFlags? flags = null) : IBackupRunPlatformStore
{
    // Arbitrary fixed application key for the claim critical section.
    private const long ClaimLockKey = 0x4250_434C_4149_4D01;
    private static readonly BackupStatus[] ActiveStatuses = [BackupStatus.Pending, BackupStatus.Leased, BackupStatus.Running, BackupStatus.RetryPending, BackupStatus.Paused];
    private readonly BackupExecutionOptions _limits = limits ?? new BackupExecutionOptions();
    private readonly ILogger logger = log ?? NullLogger<PostgreSqlBackupRunPlatformStore>.Instance;

    /// <summary>A sample in [0,1) for retry jitter.</summary>
    private double Jitter() => jitter is null ? Random.Shared.NextDouble() : Math.Clamp(jitter(), 0, 0.999999);

    public async Task<IReadOnlyList<TransferCheckpoint>> GetCheckpointsAsync(TenantId tenantId, Guid attemptId, CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
        return await context.Set<TransferCheckpoint>().AsNoTracking().Where(x => x.TenantId == tenantId && x.AttemptId == attemptId).ToListAsync(cancellationToken);
    }
    public async Task<BackupJob?> GetJobAsync(TenantId tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
        return await context.BackupJobs.AsNoTracking().SingleOrDefaultAsync(job => job.TenantId == tenantId && job.Id == jobId, cancellationToken);
    }
    public async Task<TenantId?> FindRunTenantAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
        return await context.BackupRuns.AsNoTracking().Where(x => x.Id == runId).Select(x => x.TenantId).SingleOrDefaultAsync(cancellationToken);
    }
    public async Task<BackupClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({ClaimLockKey})", cancellationToken);
        if (!await BackupRunQueries.LockAgentAsync(context, tenantId, agentId, cancellationToken)) return null;
        if (!await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == agentId && agent.Status == AgentStatus.Active, cancellationToken)) return null;
        var held = await BackupRunQueries.Runs(context).Where(run => run.TenantId == tenantId && run.AgentId == agentId &&
            (run.Status == BackupStatus.Leased || run.Status == BackupStatus.Running)).ToListAsync(cancellationToken);
        foreach (var run in held)
        {
            var wasTerminal = run.IsTerminal;
            var attemptWasTerminal = run.CurrentAttempt.IsTerminal;
            run.Expire(now, Jitter());
            BackupAudit.RecordCompletion(context, run, wasTerminal, attemptWasTerminal, now);
        }
        await context.SaveChangesAsync(cancellationToken);
        // A backup never runs beside a restore of the same agent: a live (unexpired Leased/Running) restore attempt holds the agent whatever the slot limits say. Decided under the
        // same agent lock the restore claim takes, so exactly one of the two can ever hold the agent. An expired restore lease does not count: the restore sweep will end it.
        if (await context.RestoreAttempts.AsNoTracking().AnyAsync(attempt => attempt.TenantId == tenantId && attempt.AgentId == agentId &&
            (attempt.Status == RestoreStatus.Leased || attempt.Status == RestoreStatus.Running) && attempt.LeaseExpiresAt > now, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        // A slot is a live (unexpired) Leased/Running attempt. Counting rows, not holding tokens, means a crashed
        // worker, a lost agent or a restarted backend cannot leak a slot: the lease simply runs out.
        var live = await context.BackupRunAttempts.AsNoTracking()
            .Where(attempt => (attempt.Status == BackupStatus.Leased || attempt.Status == BackupStatus.Running) && attempt.LeaseExpiresAt > now)
            .Select(attempt => new { attempt.TenantId, attempt.AgentId }).ToListAsync(cancellationToken);
        if (live.Count >= _limits.GlobalMaxConcurrentBackups
            || live.Count(slot => slot.TenantId == tenantId) >= _limits.TenantLimit(tenantId)
            || live.Count(slot => slot.TenantId == tenantId && slot.AgentId == agentId) >= _limits.AgentMaxConcurrentBackups)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var candidateIds = await (from attempt in context.BackupRunAttempts
                                  join run in context.BackupRuns on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                                  join job in context.BackupJobs on new { run.JobId, run.TenantId } equals new { JobId = job.Id, job.TenantId }
                                  where attempt.TenantId == tenantId && attempt.AgentId == agentId && attempt.Status == BackupStatus.Pending && run.Status == BackupStatus.Pending && job.ArchivedAt == null
                                     // A scheduled run's first attempt that missed its grace period never starts; the unclaimed-run sweep ends it.
                                     && (run.ClaimDeadline == null || run.ClaimDeadline >= now || attempt.Number > 1 || attempt.LeasedAt != null)
                                  orderby attempt.EligibleAt, run.CreatedAt, run.Id
                                  select run.Id).Take(16).ToListAsync(cancellationToken);
        BackupRun? next = null;
        bool? supportsMultiDestination = null;
        bool? supportsQuotaBound = null;
        bool? supportsUploadLimits = null;
        long? maxArtifactBytes = null;
        foreach (var candidateId in candidateIds)
        {
            var candidate = await BackupRunQueries.Runs(context).SingleAsync(run => run.TenantId == tenantId && run.Id == candidateId, cancellationToken);
            if (!await BackupRunQueries.LockUnarchivedJobAsync(context, tenantId, candidate.JobId, cancellationToken)) continue;
            if (candidate.Configuration.Engine is DatabaseEngines.PostgreSql or DatabaseEngines.FileSystem && !await context.AgentCapabilities.AsNoTracking().AnyAsync(
                capability => capability.TenantId == tenantId && capability.AgentId == agentId &&
                    capability.Identifier == candidate.Configuration.Engine + ".backup.full" && capability.Support == BackupPlatform.Domain.Capabilities.CapabilitySupport.Supported, cancellationToken)) continue;
            // A retry or a queued run reaches an agent without passing admission again, so the capability is fenced here too: such a run waits for an agent that can execute it.
            supportsMultiDestination ??= await StorageTargetGate.SupportsMultiDestinationAsync(context, tenantId, agentId, cancellationToken);
            if (supportsMultiDestination != true) continue;
            if (candidate.Configuration.Destinations.Any(destination => StorageProviderTypes.AgentExecuted(destination.StorageProvider)))
            {
                // Legacy snapshots stay immutable and wait for an explicit fresh run; they are never silently relayed or retargeted.
                if (candidate.Configuration.Destinations.Any(destination => StorageProviderTypes.AgentExecuted(destination.StorageProvider) && !destination.Direct)
                    || !await StorageTargetGate.SupportsDirectStorageAsync(context, tenantId, agentId, cancellationToken)) continue;
            }
            // The backend paces every upload it relays itself; only an AgentDirect destination depends on the agent. A limit that such an agent cannot follow would be
            // silently ignored, so that run waits for an agent that can pace its uploads.
            if (candidate.Configuration.Destinations.Any(destination => destination.StorageExecutionLocation == BackupStorageExecutionLocation.Agent)
                && await UploadBandwidthStoreCore.AnyAsync(context, tenantId, agentId, candidate.JobId, cancellationToken))
            {
                supportsUploadLimits ??= await UploadBandwidthStoreCore.SupportsAsync(context, tenantId, agentId, cancellationToken);
                if (supportsUploadLimits != true) continue;
            }
            var bound = await TenantUsageEngine.ReadRunMaxArtifactBytesAsync(context, candidate, now, cancellationToken);
            if (bound is not null)
            {
                supportsQuotaBound ??= await TenantUsageEngine.SupportsQuotaBoundedUploadAsync(context, tenantId, agentId, cancellationToken);
                if (supportsQuotaBound != true) continue;
            }
            maxArtifactBytes = bound;
            next = candidate;
            break;
        }
        if (next is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        next.Claim(Guid.NewGuid(), now, leaseDuration);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BackupClaim(next, next.CurrentAttempt)
        {
            MaxArtifactBytes = maxArtifactBytes
        };
    }
    public async Task<BackupClaim?> GetAttemptAsync(TenantId tenantId, AgentId agentId, Guid attemptId, CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
        var run = await BackupRunQueries.Runs(context).AsNoTracking().SingleOrDefaultAsync(run => run.TenantId == tenantId && run.AgentId == agentId &&
            run.Attempts.Any(attempt => attempt.Id == attemptId && attempt.TenantId == tenantId && attempt.AgentId == agentId), cancellationToken);
        return run is null ? null : new BackupClaim(run, run.Attempts.Single(attempt => attempt.Id == attemptId))
        {
            MaxArtifactBytes = await TenantUsageEngine.ReadRunMaxArtifactBytesAsync(context, run, run.CurrentAttempt.LeasedAt ?? run.CreatedAt, cancellationToken)
        };
    }
    public async Task<BackupQuotaReservationResult> AdjustQuotaReservationAsync(BackupLeaseCommand command, long sizeBytes, CancellationToken cancellationToken)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var conflict = new BackupQuotaReservationResult(BackupMutationResult.Conflict, false);
        if (sizeBytes < 0) return conflict;
        await using var context = PlatformOperationsDbContext.Create(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await BackupRunQueries.LockAgentAsync(context, command.TenantId, command.AgentId, cancellationToken))
            return new(BackupMutationResult.NotFound, false);
        var run = await BackupRunQueries.Runs(context).SingleOrDefaultAsync(run => run.TenantId == command.TenantId && run.AgentId == command.AgentId &&
            run.Attempts.Any(attempt => attempt.Id == command.AttemptId && attempt.TenantId == command.TenantId && attempt.AgentId == command.AgentId), cancellationToken);
        if (run is null) return new(BackupMutationResult.NotFound, false);
        if (run.CurrentAttempt.Id != command.AttemptId) return conflict;
        if (!await TenantUsageEngine.LockTenantAsync(context, command.TenantId, cancellationToken)) return conflict;
        if (!run.CurrentAttempt.HasLease(command.LeaseToken, command.Now + elapsed.Elapsed)) return conflict;

        var (granted, adjusted) = await PostgreSqlBackupAdmissionPolicy.AdjustQuotaReservationAsync(context, command.TenantId, run, command.AttemptId, sizeBytes, command.Now, cancellationToken);
        if (!adjusted) return new(BackupMutationResult.Accepted, granted);
        if (!run.CurrentAttempt.HasLease(command.LeaseToken, command.Now + elapsed.Elapsed)) return conflict;
        await transaction.CommitAsync(cancellationToken);
        return new(BackupMutationResult.Accepted, true);
    }

    public Task<BackupMutationResult> StartAttemptAsync(BackupLeaseCommand command, CancellationToken cancellationToken) =>
        MutateAsync(command, run => run.Start(command.LeaseToken, command.Now, command.SoftwareVersion!, command.ProtocolVersion!, command.Platform!), cancellationToken);
    public Task<BackupMutationResult> ReportProgressAsync(BackupLeaseCommand command, BackupProgress progress, CancellationToken cancellationToken) =>
        MutateAsync(command, run => run.Progress(command.LeaseToken, command.Now, progress.Stage, progress.Percent), cancellationToken,
            progress.Checkpoint is not { } checkpoint ? null : async (context, run) =>
            {
                if (!run.CurrentAttempt.HasLease(command.LeaseToken, command.Now)) throw new InvalidOperationException("Stale checkpoint.");
                if (!run.Configuration.Destinations.Any(d => d.StorageProvider == checkpoint.StorageProvider && d.StorageConnectionId == checkpoint.DestinationId))
                    throw new ArgumentException("Checkpoint destination is outside the snapshot.");
                var rows = context.Set<TransferCheckpoint>();
                var previous = await rows.SingleOrDefaultAsync(x => x.TenantId == command.TenantId && x.AttemptId == command.AttemptId &&
                    x.StorageProvider == checkpoint.StorageProvider && x.DestinationId == checkpoint.DestinationId, cancellationToken);
                if (previous is not null) rows.Remove(previous);
                if (previous is not null) await context.SaveChangesAsync(cancellationToken);
                rows.Add(new TransferCheckpoint(command.TenantId, command.AttemptId, checkpoint.StorageProvider, checkpoint.DestinationId,
                    checkpoint.VerifiedByteOffset, checkpoint.ProviderState, command.Now));
            });
    public Task<BackupMutationResult> RecordTransferAsync(BackupLeaseCommand command, BackupTransferIntent transfer, CancellationToken cancellationToken) =>
        MutateAsync(command, run =>
        {
            var destination = run.Configuration.Destinations.SingleOrDefault(d => d.StorageConnectionId == transfer.StorageConnectionId);
            if (destination is not { StorageProvider: StorageProviderTypes.Sftp or StorageProviderTypes.GoogleDrive or StorageProviderTypes.S3 or StorageProviderTypes.Smb, StorageExecutionLocation: BackupStorageExecutionLocation.Backend } ||
                !ManagedBackupLayout.TryParseStorageKey(transfer.Key, out var layout) || layout is null || layout.IsPartial ||
                layout.TenantId != command.TenantId.Value || layout.JobId != run.JobId || layout.RunId != run.Id || layout.AttemptId != command.AttemptId)
                throw new ArgumentException("The upload intent does not belong to this attempt.");
            var artifact = new BackupArtifact(Guid.NewGuid(), command.TenantId, command.AttemptId, transfer.SizeBytes, transfer.Checksum,
                transfer.Checksum, "sha256", command.Now, "{\"kind\":\"remote-upload-intent\"}", "{}");
            artifact.AddReplica(new BackupReplica(Guid.NewGuid(), command.TenantId, artifact.Id, destination.StorageProvider, transfer.StorageConnectionId.ToString("D"),
                transfer.Key, transfer.SizeBytes, transfer.Checksum, "sha256", "Unverified", "None", command.Now, null,
                BackupStorageExecutionLocation.Backend, storageConnectionId: transfer.StorageConnectionId));
            run.CurrentAttempt.RecordTransfer(command.LeaseToken, command.Now, artifact);
        }, cancellationToken);
    public Task<BackupMutationResult> CompleteAttemptAsync(BackupLeaseCommand command, ApplicationCompletion completion, CancellationToken cancellationToken)
    {
        if (completion.Outcome == "Paused")
        {
            if (!Enum.TryParse<TransferPauseReason>(completion.ReasonCode, out var reason) || !Enum.IsDefined(reason))
                return Task.FromResult(BackupMutationResult.Conflict);
            return MutateAsync(command, run => run.Pause(command.LeaseToken, command.Now, reason), cancellationToken);
        }
        // Replicas the agent already transferred are carried across the completion under the same agent lock; each destination is resolved from the run's snapshot when the domain completion is mapped.
        var connections = new Dictionary<string, Guid>(StringComparer.Ordinal);
        IReadOnlyList<BackupArtifactResult> retained = [];
        return MutateAsync(command, run => run.Complete(command.LeaseToken, command.Now, MapCompletion(command, completion, run, connections, retained), Jitter()), cancellationToken,
            async (context, run) =>
            {
                var pending = run.CurrentAttempt.Artifacts.ToArray();
                retained = pending.Select(artifact => new BackupArtifactResult(artifact.SizeBytes, artifact.SourceChecksum, artifact.ArtifactChecksum,
                    artifact.ChecksumAlgorithm, artifact.CreatedAt, artifact.SourceMetadataJson, artifact.AppliedOptionsJson,
                    artifact.Replicas.Select(replica => new BackupReplicaResult(replica.StorageProvider, replica.StorageTarget, replica.Key, replica.SizeBytes,
                        replica.Checksum, replica.ChecksumAlgorithm, replica.VerificationState, replica.VerificationMethod, replica.CreatedAt, replica.VerifiedAt,
                        replica.StorageConnectionId)).ToArray(), artifact.Format, artifact.EncryptionKeyId, artifact.EncryptionKeyVersion, artifact.EncryptionWrappedDekBase64, artifact.EncryptionRecipientsJson)).ToArray();
                if (!run.CurrentAttempt.IsTerminal && pending.Length > 0 && run.CurrentAttempt.HasLease(command.LeaseToken, command.Now))
                {
                    context.BackupReplicas.RemoveRange(pending.SelectMany(artifact => artifact.Replicas));
                    context.BackupArtifacts.RemoveRange(pending);
                    await context.SaveChangesAsync(cancellationToken);
                }
                // A snapshot that predates storage connections names none: its copies belong to the tenant's default for the provider. Publishing what already ran is never refused.
                foreach (var destination in run.Configuration.Destinations.Where(d => d.StorageConnectionId is null))
                {
                    connections[destination.StorageProvider] = await StorageTargetGate.ForPublishAsync(context, command.TenantId, destination.StorageProvider, command.Now, cancellationToken);
                }
            });
    }
    public async Task<BackupLeaseResult?> RenewLeaseAsync(BackupLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        BackupLeaseResult? result = null;
        var mutation = await MutateAsync(command, run =>
        {
            run.Renew(command.LeaseToken, command.Now, leaseDuration);
            result = new BackupLeaseResult(run.CurrentAttempt.LeaseExpiresAt!.Value, run.CancellationRequestedAt.HasValue, run.CurrentAttempt.PauseRequested);
        }, cancellationToken);
        return mutation == BackupMutationResult.Accepted ? result : null;
    }
    public async Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scan = PlatformOperationsDbContext.Create(options);
        var stale = await scan.BackupRunAttempts.AsNoTracking().Where(attempt =>
            (attempt.Status == BackupStatus.Leased || attempt.Status == BackupStatus.Running) && attempt.LeaseExpiresAt <= now)
            .Select(attempt => new { attempt.TenantId, attempt.AgentId, attempt.Id }).ToListAsync(cancellationToken);
        var expired = 0;
        foreach (var attempt in stale)
        {
            var changed = false;
            var retained = await scan.Set<TransferCheckpoint>().AnyAsync(x => x.TenantId == attempt.TenantId && x.AttemptId == attempt.Id, cancellationToken);
            var result = await MutateAsync(new BackupLeaseCommand(attempt.TenantId, attempt.AgentId, attempt.Id, Guid.Empty, now),
                run => changed = run.Expire(now, Jitter(), retained), cancellationToken,
                // A cancelled run never resumes, so its retained transfer state is released with the lost attempt.
                async (context, run) =>
                {
                    if (!run.CancellationRequestedAt.HasValue || run.CurrentAttempt.Status is not (BackupStatus.Leased or BackupStatus.Running) || run.CurrentAttempt.LeaseExpiresAt > now) return;
                    context.Set<TransferCheckpoint>().RemoveRange(await context.Set<TransferCheckpoint>()
                        .Where(x => x.TenantId == attempt.TenantId && x.AttemptId == attempt.Id).ToListAsync(cancellationToken));
                });
            if (result == BackupMutationResult.Accepted && changed) expired++;
        }
        return expired;
    }

    public async Task<IReadOnlyList<BackupRetryStarted>> StartDueRetriesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var scan = PlatformOperationsDbContext.Create(options);
        var due = await scan.BackupRuns.AsNoTracking().Where(run => run.Status == BackupStatus.RetryPending && run.NextAttemptAt <= now
            && scan.BackupJobs.Any(job => job.TenantId == run.TenantId && job.Id == run.JobId && job.ArchivedAt == null))
            .OrderBy(run => run.NextAttemptAt).ThenBy(run => run.Id).Take(Math.Clamp(limit, 1, 500))
            .Select(run => new { run.TenantId, run.AgentId, run.Id }).ToListAsync(cancellationToken);
        var started = new List<BackupRetryStarted>();
        foreach (var candidate in due)
        {
            await using var context = PlatformOperationsDbContext.Create(options);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (!await BackupRunQueries.LockAgentAsync(context, candidate.TenantId, candidate.AgentId, cancellationToken)) continue;
            // Re-read under the lock: a concurrent worker, a manual retry or a cancellation may already have moved the run on.
            var run = await BackupRunQueries.Runs(context).SingleOrDefaultAsync(run => run.TenantId == candidate.TenantId && run.Id == candidate.Id && run.AgentId == candidate.AgentId, cancellationToken);
            if (run is null || !await BackupRunQueries.LockUnarchivedJobAsync(context, run.TenantId, run.JobId, cancellationToken) || !run.StartScheduledRetry(Guid.NewGuid(), now)) continue;
            context.RecordAudit(AuditEvent.Record(
                now, UserId.System, AuditActorKind.System, AuditActions.BackupRunRetryRequested, run.TenantId,
                "backup_run", run.Id.ToString("D"),
                new Dictionary<string, object?> { ["jobId"] = run.JobId.ToString("D"), ["runId"] = run.Id.ToString("D"), ["attemptId"] = run.CurrentAttempt.Id.ToString("D"), ["retry"] = "Automatic", ["result"] = "started" }));
            try { await context.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException) { continue; } // lost a race the lock did not cover (another instance); the winner's attempt stands
            await transaction.CommitAsync(cancellationToken);
            started.Add(new BackupRetryStarted(run.Id, run.AgentId));
        }
        return started;
    }
    public async Task<IReadOnlyList<BackupRetryStarted>> ResumeDueNetworkPausesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var scan = PlatformOperationsDbContext.Create(options);
        var paused = await (from attempt in scan.BackupRunAttempts.AsNoTracking()
                            join run in scan.BackupRuns.AsNoTracking() on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                            where run.Status == BackupStatus.Paused && run.CancellationRequestedAt == null && run.CurrentAttemptNumber == attempt.Number
                               && attempt.Status == BackupStatus.Paused && attempt.PauseReason == TransferPauseReason.PausedByNetworkError
                            orderby attempt.ProgressAt, run.Id
                            select new { run.TenantId, run.AgentId, run.Id }).Take(Math.Clamp(limit, 1, 500)).ToListAsync(cancellationToken);
        var resumed = new List<BackupRetryStarted>();
        foreach (var candidate in paused)
        {
            await using var context = PlatformOperationsDbContext.Create(options);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (!await BackupRunQueries.LockAgentAsync(context, candidate.TenantId, candidate.AgentId, cancellationToken)) continue;
            // Re-read under the lock: an operator's resume or cancel, or another instance, may already have moved the run on.
            var run = await BackupRunQueries.Runs(context).SingleOrDefaultAsync(run => run.TenantId == candidate.TenantId && run.Id == candidate.Id && run.AgentId == candidate.AgentId, cancellationToken);
            if (run is null || !run.ResumeAfterNetworkPause(now)) continue;
            context.RecordAudit(AuditEvent.Record(
                now, UserId.System, AuditActorKind.System, AuditActions.BackupRunResumed, run.TenantId,
                "backup_run", run.Id.ToString("D"),
                new Dictionary<string, object?> { ["jobId"] = run.JobId.ToString("D"), ["runId"] = run.Id.ToString("D"), ["attemptId"] = run.CurrentAttempt.Id.ToString("D"), ["result"] = "resumed", ["resume"] = "Automatic" }));
            try { await context.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException) { continue; } // lost a race the lock did not cover (another instance); the winner's resume stands
            await transaction.CommitAsync(cancellationToken);
            resumed.Add(new BackupRetryStarted(run.Id, run.AgentId));
        }
        return resumed;
    }
    private async Task<BackupMutationResult> MutateAsync(BackupLeaseCommand command, Action<BackupRun> mutate, CancellationToken cancellationToken, Func<PlatformDbContext, BackupRun, Task>? prepare = null)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await BackupRunQueries.LockAgentAsync(context, command.TenantId, command.AgentId, cancellationToken)) return BackupMutationResult.NotFound;
        var run = await BackupRunQueries.Runs(context).SingleOrDefaultAsync(run => run.TenantId == command.TenantId && run.AgentId == command.AgentId &&
            run.Attempts.Any(attempt => attempt.Id == command.AttemptId && attempt.TenantId == command.TenantId && attempt.AgentId == command.AgentId), cancellationToken);
        if (run is null) return BackupMutationResult.NotFound;
        // Only the run's current attempt can be addressed: once a retry exists, every callback of an older attempt is stale.
        if (run.CurrentAttempt.Id != command.AttemptId) return BackupMutationResult.Conflict;
        var wasTerminal = run.IsTerminal;
        var attemptWasTerminal = run.CurrentAttempt.IsTerminal;
        try
        {
            if (prepare is not null) await prepare(context, run);
            mutate(run);
        }
        catch (InvalidOperationException) { return BackupMutationResult.Conflict; }
        catch (ArgumentException) { return BackupMutationResult.Conflict; }
        // The attempt ended (failed, lost) after its job was archived: the run it would have retried or resumed is cancelled instead.
        if (!run.IsTerminal && run.Status is BackupStatus.RetryPending or BackupStatus.Paused
            && await context.BackupJobs.AsNoTracking().AnyAsync(job => job.TenantId == run.TenantId && job.Id == run.JobId && job.ArchivedAt != null, cancellationToken))
            BackupAudit.CancelRunOfArchivedJob(context, run, null, command.Now);
        BackupAudit.RecordCompletion(context, run, wasTerminal, attemptWasTerminal, command.Now);
        // No flags (a store built by hand) means no gate; the API always registers them.
        if (!wasTerminal && run.Status == BackupStatus.Succeeded && (flags?.IsEnabled(Features.SizeAnomaly) ?? true)) await EvaluateSizeAnomalyAsync(context, transaction, run, command.Now, cancellationToken);
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return BackupMutationResult.Conflict; }
        await transaction.CommitAsync(cancellationToken);
        return BackupMutationResult.Accepted;
    }
    /// <summary>
    /// ADR-0001: an anomaly fault is advisory and never fails the completion. The block runs under a savepoint (a failed Postgres statement
    /// otherwise poisons the whole transaction) and is undone on failure, leaving the anomaly status unknown. Persisting the evaluation
    /// is not guarded: a save failure still rolls back the completion and its notification outbox together.
    /// </summary>
    private async Task EvaluateSizeAnomalyAsync(PlatformDbContext context, IDbContextTransaction transaction, BackupRun run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        const string savepoint = "size_anomaly";
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        var tracked = context.ChangeTracker.Entries().Select(entry => entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        try
        {
            var baseline = await context.BackupRuns.AsNoTracking()
                .Where(previous => previous.TenantId == run.TenantId && previous.JobId == run.JobId && previous.Status == BackupStatus.Succeeded && previous.Id != run.Id)
                .OrderByDescending(previous => previous.CompletedAt ?? previous.CreatedAt).ThenByDescending(previous => previous.Id).Take(10)
                .Select(previous => previous.Attempts.Where(attempt => attempt.Status == BackupStatus.Succeeded)
                    .SelectMany(attempt => attempt.Artifacts).Sum(artifact => artifact.SizeBytes)).ToListAsync(cancellationToken);
            run.RecordSizeAnomaly(BackupSizeAnomalyDetector.Evaluate(run.CurrentAttempt.Artifacts.Sum(artifact => artifact.SizeBytes), baseline));
            if (run.IsSizeAnomaly)
            {
                var jobName = await context.BackupJobs.Where(job => job.TenantId == run.TenantId && job.Id == run.JobId).Select(job => job.Name).SingleAsync(cancellationToken);
                await PostgreSqlNotificationStore.RecordOccurrenceAsync(context, run.TenantId,
                    PostgreSqlNotificationObservationSource.SizeAnomaly(run, jobName), notifications ?? new NotificationOptions(), now, cancellationToken);
                context.RecordAudit(AuditEvent.Record(now, UserId.System, AuditActorKind.System, AuditActions.BackupRunSizeAnomaly, run.TenantId,
                    "backup_run", run.Id.ToString("D"), new Dictionary<string, object?>
                    {
                        ["jobId"] = run.JobId, ["status"] = run.SizeAnomalyStatus,
                        ["deviationRatio"] = run.SizeAnomalyDeviationRatio, ["baselineBytes"] = run.SizeAnomalyBaselineBytes
                    }));
            }
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            foreach (var entry in context.ChangeTracker.Entries().Where(entry => !tracked.Contains(entry.Entity)).ToList()) entry.State = EntityState.Detached;
            var runEntry = context.Entry(run);
            runEntry.Property(nameof(BackupRun.SizeAnomalyStatus)).CurrentValue = null;
            runEntry.Property(nameof(BackupRun.SizeAnomalyDeviationRatio)).CurrentValue = null;
            runEntry.Property(nameof(BackupRun.SizeAnomalyBaselineBytes)).CurrentValue = null;
            logger.LogError(exception, "Size anomaly evaluation failed for backup run {RunId}; the run completes without an anomaly verdict.", run.Id);
        }
    }
    private static DomainCompletion MapCompletion(BackupLeaseCommand command, ApplicationCompletion result, BackupRun run,
        IReadOnlyDictionary<string, Guid> connections, IReadOnlyList<BackupArtifactResult> retained)
    {
        var status = result.Outcome switch
        {
            "Succeeded" => BackupStatus.Succeeded,
            "Failed" => BackupStatus.Failed,
            "Cancelled" => BackupStatus.Cancelled,
            _ => throw new ArgumentException("Invalid terminal outcome.")
        };
        // Every destination is looked up in the RUN'S SNAPSHOT: what the agent names that the snapshot does not contain is refused, never created.
        var snapshot = run.Configuration.Destinations;
        (BackupDestination Destination, Guid Connection) Resolve(string provider, Guid? named)
        {
            var candidates = snapshot.Where(d => d.StorageProvider == provider && (named is null || d.StorageConnectionId == named)).ToArray();
            if (candidates.Length != 1) throw new ArgumentException("The destination is not part of the run snapshot.");
            var connection = candidates[0].StorageConnectionId ?? connections.GetValueOrDefault(provider);
            return connection == Guid.Empty ? throw new ArgumentException("The destination has no storage connection.") : (candidates[0], connection);
        }
        // A replica carries what the agent reported about THAT copy; the domain compares it with the artifact, so a copy that disagrees is refused rather than silently corrected.
        BackupReplica NewReplica(BackupDestination destination, Guid connection, Guid artifactId, string target, string key, long size, string checksum, string algorithm, string state, string method, DateTimeOffset createdAt, DateTimeOffset? verifiedAt) =>
            new(Guid.NewGuid(), command.TenantId, artifactId, destination.StorageProvider, target, key, size, checksum, algorithm, state, method,
                createdAt, verifiedAt, destination.StorageExecutionLocation, destination.StorageExecutionLocation == BackupStorageExecutionLocation.Agent ? run.AgentId.Value : null, connection);
        BackupArtifact? artifact = null;
        var replicaIds = new Dictionary<(string Provider, Guid Connection), Guid>();
        if (result.Artifact is { } input)
        {
            artifact = new BackupArtifact(Guid.NewGuid(), command.TenantId, command.AttemptId, input.SizeBytes,
                input.SourceChecksum, input.ArtifactChecksum, input.ChecksumAlgorithm, input.CreatedAt, input.SourceMetadataJson, input.AppliedOptionsJson,
                input.Format, input.EncryptionKeyId, input.EncryptionKeyVersion, input.EncryptionWrappedDekBase64, input.EncryptionRecipientsJson);
            foreach (var replica in input.Replicas)
            {
                var (destination, connection) = Resolve(replica.StorageProvider, replica.StorageConnectionId);
                var copy = NewReplica(destination, connection, artifact.Id, replica.StorageTarget, replica.Key, replica.SizeBytes, replica.Checksum, replica.ChecksumAlgorithm,
                    replica.VerificationState, replica.VerificationMethod, replica.CreatedAt, replica.VerifiedAt);
                artifact.AddReplica(copy);
                replicaIds[(destination.StorageProvider, connection)] = copy.Id;
            }
        }
        foreach (var pending in retained)
        {
            artifact ??= new BackupArtifact(Guid.NewGuid(), command.TenantId, command.AttemptId, pending.SizeBytes, pending.SourceChecksum,
                pending.ArtifactChecksum, pending.ChecksumAlgorithm, pending.CreatedAt, pending.SourceMetadataJson, pending.AppliedOptionsJson,
                pending.Format, pending.EncryptionKeyId, pending.EncryptionKeyVersion, pending.EncryptionWrappedDekBase64, pending.EncryptionRecipientsJson);
            foreach (var replica in pending.Replicas)
            {
                if (artifact.Replicas.Any(r => r.StorageConnectionId == replica.StorageConnectionId) ||
                    result.Destinations?.Any(d => d.StorageConnectionId == replica.StorageConnectionId && d.ObjectMayRemain) == true) continue;
                var (destination, connection) = Resolve(replica.StorageProvider, replica.StorageConnectionId);
                artifact.AddReplica(NewReplica(destination, connection, artifact.Id, replica.StorageTarget, replica.Key, replica.SizeBytes,
                    replica.Checksum, replica.ChecksumAlgorithm, "Failed", "None", replica.CreatedAt, null));
            }
        }
        var outcomes = new List<BackupDestinationOutcome>();
        foreach (var report in result.Destinations ?? [])
        {
            var (destination, connection) = Resolve(report.StorageProvider, report.StorageConnectionId);
            Guid? replicaId = report.State == BackupDestinationStates.Verified
                ? replicaIds.TryGetValue((destination.StorageProvider, connection), out var id) ? id : throw new ArgumentException("A verified destination has no replica.")
                : null;
            if (report.ObjectMayRemain)
            {
                if (artifact is null || !ManagedBackupLayout.TryParseStorageKey(report.Key, out var layout) || layout is null ||
                    layout.TenantId != command.TenantId.Value || layout.JobId != run.JobId || layout.RunId != run.Id || layout.AttemptId != command.AttemptId)
                    throw new ArgumentException("An orphan requires artifact metadata and an object key owned by this attempt.");
                // The object exists physically but never verified: it is recorded as a Failed replica so its bytes stay accounted for and retention can remove it.
                var target = StorageProviderTypes.External(destination.StorageProvider) ? connection.ToString("D") : destination.StorageProvider;
                var orphan = NewReplica(destination, connection, artifact.Id, target, report.Key!, artifact.SizeBytes, artifact.ArtifactChecksum, artifact.ChecksumAlgorithm,
                    "Failed", "None", report.CompletedAt, null);
                artifact.AddReplica(orphan);
            }
            outcomes.Add(new BackupDestinationOutcome(Guid.NewGuid(), command.TenantId, run.Id, command.AttemptId, destination.StorageProvider, connection, destination.IsPrimary,
                destination.StorageExecutionLocation, report.State, report.Upload, report.Verification, report.FailureCode, report.Key, report.ObjectMayRemain, report.StartedAt, report.CompletedAt, replicaId));
        }
        return new DomainCompletion(status, result.ReasonCode, artifact, outcomes);
    }
}
