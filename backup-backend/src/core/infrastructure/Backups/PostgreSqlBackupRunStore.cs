using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using ApplicationCompletion = BackupPlatform.Application.Backups.BackupCompletion;
using DomainCompletion = BackupPlatform.Domain.Backups.BackupCompletion;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>All lifecycle writes acquire the tenant-qualified agent row lock first, fencing concurrent polls and callbacks.</summary>
public sealed class PostgreSqlBackupRunStore(DbContextOptions<PlatformDbContext> options) : IBackupRunStore
{
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
    public async Task<BackupRun?> StartRunAsync(TenantId tenantId, Guid jobId, string idempotencyKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var owner = await context.BackupJobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.Id == jobId).Select(job => job.AgentId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return null;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, tenantId, owner, cancellationToken)) return null;
        var existing = await Runs(context).SingleOrDefaultAsync(run => run.TenantId == tenantId && run.JobId == jobId && run.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null) return existing;
        var job = await context.BackupJobs.SingleOrDefaultAsync(job => job.TenantId == tenantId && job.Id == jobId && job.Enabled, cancellationToken);
        if (job is null || !await context.Agents.AnyAsync(agent => agent.Id == owner && agent.TenantId == tenantId && agent.Status == AgentStatus.Active, cancellationToken)) return null;
        var run = new BackupRun(Guid.NewGuid(), tenantId, job.Id, owner, job.Configuration, idempotencyKey, now);
        run.CreateAttempt(Guid.NewGuid(), now);
        context.BackupRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return run;
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
        var run = await Runs(context).SingleAsync(run => run.TenantId == tenantId && run.Id == runId && run.AgentId == owner, cancellationToken);
        run.RequestCancellation(now);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return run;
    }
    public async Task<BackupClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, tenantId, agentId, cancellationToken)) return null;
        if (!await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == agentId && agent.Status == AgentStatus.Active, cancellationToken)) return null;
        var active = await Runs(context).Where(run => run.TenantId == tenantId && run.AgentId == agentId &&
            (run.Status == BackupStatus.Leased || run.Status == BackupStatus.Running)).ToListAsync(cancellationToken);
        foreach (var run in active) run.Expire(now);
        // Save terminal rows first so the partial concurrency-one index no longer reserves the agent.
        await context.SaveChangesAsync(cancellationToken);
        if (active.Any(run => !run.IsTerminal))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var next = await Runs(context).Where(run => run.TenantId == tenantId && run.AgentId == agentId && run.Status == BackupStatus.Pending)
            .OrderBy(run => run.CreatedAt).ThenBy(run => run.Id).FirstOrDefaultAsync(cancellationToken);
        if (next is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
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
        MutateAsync(command, run => run.Complete(command.LeaseToken, command.Now, MapCompletion(command, completion)), cancellationToken);
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
                run => changed = run.Expire(now), cancellationToken);
            if (result == BackupMutationResult.Accepted && changed) expired++;
        }
        return expired;
    }
    private async Task<BackupMutationResult> MutateAsync(BackupLeaseCommand command, Action<BackupRun> mutate, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockAgentAsync(context, command.TenantId, command.AgentId, cancellationToken)) return BackupMutationResult.NotFound;
        var run = await Runs(context).SingleOrDefaultAsync(run => run.TenantId == command.TenantId && run.AgentId == command.AgentId &&
            run.Attempts.Any(attempt => attempt.Id == command.AttemptId && attempt.TenantId == command.TenantId && attempt.AgentId == command.AgentId), cancellationToken);
        if (run is null) return BackupMutationResult.NotFound;
        if (run.CurrentAttempt.Id != command.AttemptId) return BackupMutationResult.Conflict;
        try { mutate(run); }
        catch (InvalidOperationException) { return BackupMutationResult.Conflict; }
        catch (ArgumentException) { return BackupMutationResult.Conflict; }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BackupMutationResult.Accepted;
    }
    private static async Task<bool> LockAgentAsync(PlatformDbContext context, TenantId tenantId, AgentId agentId, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT \"Id\" FROM agents WHERE \"Id\" = {agentId.Value} AND \"TenantId\" = {tenantId.Value} FOR UPDATE", cancellationToken);
        return await context.Agents.AnyAsync(agent => agent.TenantId == tenantId && agent.Id == agentId, cancellationToken);
    }
    private static IQueryable<BackupRun> Runs(PlatformDbContext context) => context.BackupRuns
        .Include(run => run.Attempts).ThenInclude(attempt => attempt.Artifacts).ThenInclude(artifact => artifact.Replicas)
        .Include(run => run.Facts).AsSplitQuery();
    private static DomainCompletion MapCompletion(BackupLeaseCommand command, ApplicationCompletion result)
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
                    replica.VerificationState, replica.VerificationMethod, replica.CreatedAt, replica.VerifiedAt));
        }
        return new DomainCompletion(status, result.ReasonCode, artifact);
    }
}
