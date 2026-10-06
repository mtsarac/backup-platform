using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ApplicationCompletion = BackupPlatform.Application.Backups.BackupCompletion;
using DomainCompletion = BackupPlatform.Domain.Backups.BackupCompletion;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>
/// All lifecycle writes acquire the tenant-qualified agent row lock first, fencing concurrent polls and callbacks of one agent.
/// Slot-consuming transitions (claim) additionally take one transaction-scoped advisory lock FIRST, so the global, tenant and
/// agent limits are decided against a consistent count of Leased/Running attempts by any number of backend instances. Releasing
/// transitions (complete, expire, cancel) never wait on that lock, so the two lock orders cannot cycle.
/// </summary>
public sealed class PostgreSqlBackupRunStore(DbContextOptions<PlatformDbContext> options, BackupExecutionOptions? limits = null, Func<double>? jitter = null) : IBackupRunStore
{
    // Arbitrary fixed application key for the claim critical section.
    private const long ClaimLockKey = 0x4250_434C_4149_4D01;
    private static readonly BackupStatus[] ActiveStatuses = [BackupStatus.Pending, BackupStatus.Leased, BackupStatus.Running, BackupStatus.RetryPending];
    private readonly BackupExecutionOptions _limits = limits ?? new BackupExecutionOptions();

    /// <summary>A sample in [0,1) for retry jitter.</summary>
    private double Jitter() => jitter is null ? Random.Shared.NextDouble() : Math.Clamp(jitter(), 0, 0.999999);

    public async Task<BackupJob?> CreateJobAsync(BackupJob job, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, job.TenantId, job.AgentId, cancellationToken)) return null;
        if (!await context.Agents.AnyAsync(agent => agent.Id == job.AgentId && agent.TenantId == job.TenantId && agent.Status == AgentStatus.Active, cancellationToken)) return null;
        context.BackupJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job;
    }
    public async Task<BackupJob?> GetJobAsync(TenantId tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.BackupJobs.AsNoTracking().SingleOrDefaultAsync(job => job.TenantId == tenantId && job.Id == jobId, cancellationToken);
    }
    public async Task<BackupStartResult> StartRunAsync(TenantId tenantId, Guid jobId, string idempotencyKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.Id == jobId).Select(job => job.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return new BackupStartResult(BackupStartOutcome.NotFound, null);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, tenantId, owner, cancellationToken)) return new BackupStartResult(BackupStartOutcome.NotFound, null);
        // The key is checked first: replaying a request must return its original run even when the job has since become busy.
        var existing = await Runs(context).SingleOrDefaultAsync(run => run.TenantId == tenantId && run.JobId == jobId && run.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null) return new BackupStartResult(BackupStartOutcome.Replayed, existing);
        var job = await context.BackupJobs.SingleOrDefaultAsync(job => job.TenantId == tenantId && job.Id == jobId && job.Enabled, cancellationToken);
        if (job is null || !await context.Agents.AnyAsync(agent => agent.Id == owner && agent.TenantId == tenantId && agent.Status == AgentStatus.Active, cancellationToken))
            return new BackupStartResult(BackupStartOutcome.NotFound, null);
        if (job.OverlapPolicy == BackupOverlapPolicy.Skip && await ActiveRunIdAsync(context, tenantId, jobId, null, cancellationToken) is { } active)
            return new BackupStartResult(BackupStartOutcome.SkippedOverlap, null, active);
        var run = new BackupRun(Guid.NewGuid(), tenantId, job.Id, owner, job.Configuration, idempotencyKey, now, job.RetryPolicy, job.OverlapPolicy, retentionPolicy: job.RetentionPolicy);
        run.QueueFirstAttempt(Guid.NewGuid(), now);
        context.BackupRuns.Add(run);
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // The unique index on active Skip runs is the backstop behind the agent lock.
            return new BackupStartResult(BackupStartOutcome.SkippedOverlap, null, await ActiveRunIdAsync(context, tenantId, jobId, null, cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return new BackupStartResult(BackupStartOutcome.Started, run);
    }
    public async Task<IReadOnlyList<BackupRun>> ListRunsAsync(TenantId tenantId, Guid? jobId, int limit, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await Runs(context).AsNoTracking().Where(run => run.TenantId == tenantId && (jobId == null || run.JobId == jobId))
            .OrderByDescending(run => run.CreatedAt).ThenBy(run => run.Id).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
    }
    public async Task<BackupRun?> GetRunAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await Runs(context).AsNoTracking().SingleOrDefaultAsync(run => run.TenantId == tenantId && run.Id == runId, cancellationToken);
    }
    public async Task<BackupRun?> RequestCancellationAsync(TenantId tenantId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await context.BackupRuns.AsNoTracking().Where(run => run.TenantId == tenantId && run.Id == runId).Select(run => run.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return null;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, tenantId, owner, cancellationToken)) return null;
        // Loaded under the lock, so a failure or retry that committed first is already visible and cancellation wins from here on.
        var run = await Runs(context).SingleAsync(run => run.TenantId == tenantId && run.Id == runId && run.AgentId == owner, cancellationToken);
        run.RequestCancellation(now);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return run;
    }
    public async Task<BackupRetryResult> RetryRunAsync(TenantId tenantId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await context.BackupRuns.AsNoTracking().Where(run => run.TenantId == tenantId && run.Id == runId).Select(run => run.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return new BackupRetryResult(BackupRetryOutcome.NotFound, null);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, tenantId, owner, cancellationToken)) return new BackupRetryResult(BackupRetryOutcome.NotFound, null);
        var run = await Runs(context).SingleOrDefaultAsync(run => run.TenantId == tenantId && run.Id == runId && run.AgentId == owner, cancellationToken);
        if (run is null) return new BackupRetryResult(BackupRetryOutcome.NotFound, null);
        var reopens = run.IsTerminal && run.OverlapPolicy == BackupOverlapPolicy.Skip
            && await ActiveRunIdAsync(context, tenantId, run.JobId, run.Id, cancellationToken) is not null;
        var decision = run.RequestManualRetry(Guid.NewGuid(), now);
        var outcome = decision switch
        {
            BackupManualRetryResult.Succeeded => BackupRetryOutcome.Succeeded,
            BackupManualRetryResult.Cancelled => BackupRetryOutcome.Cancelled,
            BackupManualRetryResult.InProgress => BackupRetryOutcome.InProgress,
            BackupManualRetryResult.AttemptsExhausted => BackupRetryOutcome.AttemptsExhausted,
            _ => reopens ? BackupRetryOutcome.OverlapActive : BackupRetryOutcome.Started
        };
        if (outcome != BackupRetryOutcome.Started) return new BackupRetryResult(outcome, run); // nothing is saved: the transaction rolls back
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return new BackupRetryResult(BackupRetryOutcome.OverlapActive, run);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new BackupRetryResult(BackupRetryOutcome.InProgress, run);
        }
        await transaction.CommitAsync(cancellationToken);
        return new BackupRetryResult(BackupRetryOutcome.Started, run);
    }
    public async Task<BackupClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({ClaimLockKey})", cancellationToken);
        if (!await LockAgentAsync(context, tenantId, agentId, cancellationToken)) return null;
        if (!await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == agentId && agent.Status == AgentStatus.Active, cancellationToken)) return null;
        var held = await Runs(context).Where(run => run.TenantId == tenantId && run.AgentId == agentId &&
            (run.Status == BackupStatus.Leased || run.Status == BackupStatus.Running)).ToListAsync(cancellationToken);
        foreach (var run in held) run.Expire(now, Jitter());
        await context.SaveChangesAsync(cancellationToken);
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
        var nextId = await (from attempt in context.BackupRunAttempts
                            join run in context.BackupRuns on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                            where attempt.TenantId == tenantId && attempt.AgentId == agentId && attempt.Status == BackupStatus.Pending && run.Status == BackupStatus.Pending
                               // A scheduled run's first attempt that missed its grace period never starts; the unclaimed-run sweep ends it.
                               && (run.ClaimDeadline == null || run.ClaimDeadline >= now || attempt.Number > 1)
                            orderby attempt.EligibleAt, run.CreatedAt, run.Id
                            select (Guid?)run.Id).FirstOrDefaultAsync(cancellationToken);
        if (nextId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var next = await Runs(context).SingleAsync(run => run.TenantId == tenantId && run.Id == nextId, cancellationToken);
        next.Claim(Guid.NewGuid(), now, leaseDuration);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BackupClaim(next, next.CurrentAttempt);
    }
    public async Task<BackupClaim?> GetAttemptAsync(TenantId tenantId, AgentId agentId, Guid attemptId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var run = await Runs(context).AsNoTracking().SingleOrDefaultAsync(run => run.TenantId == tenantId && run.AgentId == agentId &&
            run.Attempts.Any(attempt => attempt.Id == attemptId && attempt.TenantId == tenantId && attempt.AgentId == agentId), cancellationToken);
        return run is null ? null : new BackupClaim(run, run.Attempts.Single(attempt => attempt.Id == attemptId));
    }
    public Task<BackupMutationResult> StartAttemptAsync(BackupLeaseCommand command, CancellationToken cancellationToken) =>
        MutateAsync(command, run => run.Start(command.LeaseToken, command.Now, command.SoftwareVersion!, command.ProtocolVersion!, command.Platform!), cancellationToken);
    public Task<BackupMutationResult> ReportProgressAsync(BackupLeaseCommand command, BackupProgress progress, CancellationToken cancellationToken) =>
        MutateAsync(command, run => run.Progress(command.LeaseToken, command.Now, progress.Stage, progress.Percent), cancellationToken);
    public Task<BackupMutationResult> CompleteAttemptAsync(BackupLeaseCommand command, ApplicationCompletion completion, CancellationToken cancellationToken) =>
        MutateAsync(command, run => run.Complete(command.LeaseToken, command.Now, MapCompletion(command, completion, run), Jitter()), cancellationToken);
    public async Task<BackupLeaseResult?> RenewLeaseAsync(BackupLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        BackupLeaseResult? result = null;
        var mutation = await MutateAsync(command, run =>
        {
            run.Renew(command.LeaseToken, command.Now, leaseDuration);
            result = new BackupLeaseResult(run.CurrentAttempt.LeaseExpiresAt!.Value, run.CancellationRequestedAt.HasValue);
        }, cancellationToken);
        return mutation == BackupMutationResult.Accepted ? result : null;
    }
    public async Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scan = PlatformDbContext.CreateForPlatformOperations(options);
        var stale = await scan.BackupRunAttempts.AsNoTracking().Where(attempt =>
            (attempt.Status == BackupStatus.Leased || attempt.Status == BackupStatus.Running) && attempt.LeaseExpiresAt <= now)
            .Select(attempt => new { attempt.TenantId, attempt.AgentId, attempt.Id }).ToListAsync(cancellationToken);
        var expired = 0;
        foreach (var attempt in stale)
        {
            var changed = false;
            var result = await MutateAsync(new BackupLeaseCommand(attempt.TenantId, attempt.AgentId, attempt.Id, Guid.Empty, now),
                run => changed = run.Expire(now, Jitter()), cancellationToken);
            if (result == BackupMutationResult.Accepted && changed) expired++;
        }
        return expired;
    }
    public async Task<IReadOnlyList<BackupRetryStarted>> StartDueRetriesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var scan = PlatformDbContext.CreateForPlatformOperations(options);
        var due = await scan.BackupRuns.AsNoTracking().Where(run => run.Status == BackupStatus.RetryPending && run.NextAttemptAt <= now)
            .OrderBy(run => run.NextAttemptAt).ThenBy(run => run.Id).Take(Math.Clamp(limit, 1, 500))
            .Select(run => new { run.TenantId, run.AgentId, run.Id }).ToListAsync(cancellationToken);
        var started = new List<BackupRetryStarted>();
        foreach (var candidate in due)
        {
            await using var context = PlatformDbContext.CreateForPlatformOperations(options);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (!await LockAgentAsync(context, candidate.TenantId, candidate.AgentId, cancellationToken)) continue;
            // Re-read under the lock: a concurrent worker, a manual retry or a cancellation may already have moved the run on.
            var run = await Runs(context).SingleOrDefaultAsync(run => run.TenantId == candidate.TenantId && run.Id == candidate.Id && run.AgentId == candidate.AgentId, cancellationToken);
            if (run is null || !run.StartScheduledRetry(Guid.NewGuid(), now)) continue;
            try { await context.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException) { continue; } // lost a race the lock did not cover (another instance); the winner's attempt stands
            await transaction.CommitAsync(cancellationToken);
            started.Add(new BackupRetryStarted(run.Id, run.AgentId));
        }
        return started;
    }
    private async Task<BackupMutationResult> MutateAsync(BackupLeaseCommand command, Action<BackupRun> mutate, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, command.TenantId, command.AgentId, cancellationToken)) return BackupMutationResult.NotFound;
        var run = await Runs(context).SingleOrDefaultAsync(run => run.TenantId == command.TenantId && run.AgentId == command.AgentId &&
            run.Attempts.Any(attempt => attempt.Id == command.AttemptId && attempt.TenantId == command.TenantId && attempt.AgentId == command.AgentId), cancellationToken);
        if (run is null) return BackupMutationResult.NotFound;
        // Only the run's current attempt can be addressed: once a retry exists, every callback of an older attempt is stale.
        if (run.CurrentAttempt.Id != command.AttemptId) return BackupMutationResult.Conflict;
        try { mutate(run); }
        catch (InvalidOperationException) { return BackupMutationResult.Conflict; }
        catch (ArgumentException) { return BackupMutationResult.Conflict; }
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return BackupMutationResult.Conflict; }
        await transaction.CommitAsync(cancellationToken);
        return BackupMutationResult.Accepted;
    }
    internal static async Task<Guid?> ActiveRunIdAsync(PlatformDbContext context, TenantId tenantId, Guid jobId, Guid? exceptRunId, CancellationToken cancellationToken)
    {
        var id = await context.BackupRuns.AsNoTracking().Where(run => run.TenantId == tenantId && run.JobId == jobId && ActiveStatuses.Contains(run.Status) && run.Id != exceptRunId)
            .OrderBy(run => run.CreatedAt).Select(run => (Guid?)run.Id).FirstOrDefaultAsync(cancellationToken);
        return id;
    }
    internal static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    internal static async Task<bool> LockAgentAsync(PlatformDbContext context, TenantId tenantId, AgentId agentId, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT \"Id\" FROM agents WHERE \"Id\" = {agentId.Value} AND \"TenantId\" = {tenantId.Value} FOR UPDATE", cancellationToken);
        return await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == agentId, cancellationToken);
    }
    internal static IQueryable<BackupRun> Runs(PlatformDbContext context) => context.BackupRuns
        .Include(run => run.Attempts).ThenInclude(attempt => attempt.Artifacts).ThenInclude(artifact => artifact.Replicas)
        .Include(run => run.Facts).AsSplitQuery();
    private static DomainCompletion MapCompletion(BackupLeaseCommand command, ApplicationCompletion result, BackupRun run)
    {
        var status = result.Outcome switch
        {
            "Succeeded" => BackupStatus.Succeeded,
            "Failed" => BackupStatus.Failed,
            "Cancelled" => BackupStatus.Cancelled,
            _ => throw new ArgumentException("Invalid terminal outcome.")
        };
        BackupArtifact? artifact = null;
        if (result.Artifact is { } input)
        {
            artifact = new BackupArtifact(Guid.NewGuid(), command.TenantId, command.AttemptId, input.SizeBytes,
                input.SourceChecksum, input.ArtifactChecksum, input.ChecksumAlgorithm, input.CreatedAt, input.SourceMetadataJson, input.AppliedOptionsJson);
            foreach (var replica in input.Replicas)
                artifact.AddReplica(new BackupReplica(Guid.NewGuid(), command.TenantId, artifact.Id, replica.StorageProvider,
                    replica.StorageTarget, replica.Key, replica.SizeBytes, replica.Checksum, replica.ChecksumAlgorithm,
                    replica.VerificationState, replica.VerificationMethod, replica.CreatedAt, replica.VerifiedAt,
                    run.Configuration.StorageExecutionLocation,
                    run.Configuration.StorageExecutionLocation == BackupStorageExecutionLocation.Agent ? run.AgentId.Value : null));
        }
        return new DomainCompletion(status, result.ReasonCode, artifact);
    }
}
