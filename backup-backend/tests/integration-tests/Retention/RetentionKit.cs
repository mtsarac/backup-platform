using Microsoft.EntityFrameworkCore;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using Completion = BackupPlatform.Application.Backups.BackupCompletion;

namespace BackupPlatform.IntegrationTests;

/// <summary>A backup the platform accepted: everything a retention test needs to find it again.</summary>
internal sealed record SeededBackup(
    TenantId TenantId, Guid JobId, Guid RunId, Guid AttemptId, Guid ArtifactId, Guid ReplicaId,
    string StorageProvider, string StorageTarget, string Key, DateTimeOffset AcceptedAt);

/// <summary>
/// Seeds accepted backups through the real store, exactly as an agent would (claim, start, complete with an artifact), so every row, snapshot
/// and expiration is produced by production code, not written by hand. Times are explicit: the store, not the wall clock, decides.
/// </summary>
internal static class RetentionKit
{
    public const string ManagedFileName = "Sales_20261005T053015Z_a1b2c3d4_FULL.bak";

    public static BackupRetentionPolicy Policy(bool enabled = true, TimeSpan? keepFor = null, TimeSpan? grace = null) =>
        new() { Enabled = enabled, KeepFor = keepFor ?? TimeSpan.FromDays(30), DeleteGracePeriod = grace ?? TimeSpan.FromHours(24) };

    /// <summary>A job of <paramref name="agent"/> with the given retention policy.</summary>
    public static async Task<BackupJob> AddJobAsync(BackupStoreHarness harness, StoreAgent agent, BackupRetentionPolicy? retention = null,
        BackupStorageExecutionLocation executionLocation = BackupStorageExecutionLocation.Backend, string storagePrefix = "")
    {
        var configuration = BackupStoreHarness.Configuration("local", executionLocation) with { StoragePrefix = storagePrefix };
        var job = new BackupJob(Guid.NewGuid(), agent.TenantId, "Database", agent.AgentId, configuration, BackupStoreHarness.T0,
            retryPolicy: null, BackupOverlapPolicy.Queue, schedule: null, retention);
        return (await harness.Store().CreateJobAsync(job, default))!;
    }

    /// <summary>
    /// Runs one backup of <paramref name="job"/> to success, accepted by the backend at <paramref name="acceptedAt"/>. The replica is stored under
    /// <paramref name="key"/> (default: a key of the managed layout for this very attempt, which is what the agent writes).
    /// </summary>
    public static async Task<SeededBackup> SeedAsync(
        BackupStoreHarness harness, StoreAgent agent, BackupJob job, DateTimeOffset acceptedAt,
        string storageProvider = "local", string storageTarget = "local", string? key = null)
    {
        var store = harness.Store();
        var run = await harness.StartRunAsync(store, job, Guid.NewGuid().ToString("N"), acceptedAt - TimeSpan.FromMinutes(5));
        await harness.SnapshotDestinationsAsync(run.Id, [storageProvider]);
        // Claim and start inside the harness' two-minute lease so the completion at acceptedAt is still accepted.
        var claim = await BackupStoreHarness.ClaimAsync(store, agent, acceptedAt - TimeSpan.FromSeconds(60))
            ?? throw new InvalidOperationException("The agent could not claim the seeded run.");
        await BackupStoreHarness.StartAttemptAsync(store, claim, acceptedAt - TimeSpan.FromSeconds(30));
        var attemptId = claim.Attempt.Id;
        var scope = ManagedBackupLayout.AttemptScope(agent.TenantId.Value, job.Id, run.Id, attemptId);
        var prefix = job.Configuration.StoragePrefix;
        var storedKey = key ?? $"{(prefix.Length == 0 ? "" : prefix + "/")}{scope}/{ManagedFileName}";
        // The agent reports its own idea of when it finished; retention must ignore it (it is deliberately far in the past here).
        var agentReportedAt = acceptedAt - TimeSpan.FromDays(400);
        var completion = new Completion("Succeeded", null, new BackupArtifactResult(100, BackupStoreHarness.SourceHash, BackupStoreHarness.ArtifactHash,
            "sha256", agentReportedAt, "{\"DatabaseName\":\"Database\"}", "{\"CopyOnly\":true}",
            [new BackupReplicaResult(storageProvider, storageTarget, storedKey, 100, BackupStoreHarness.ArtifactHash, "sha256", "Verified", "ReadBack", agentReportedAt, agentReportedAt)]));
        await BackupStoreHarness.CompleteAsync(store, claim, completion, acceptedAt);
        await using var context = harness.Database.CreatePlatformContext();
        var artifact = context.BackupArtifacts.Where(candidate => candidate.AttemptId == attemptId).Select(candidate => candidate.Id).Single();
        var replica = context.BackupReplicas.Where(candidate => candidate.ArtifactId == artifact).Select(candidate => candidate.Id).Single();
        return new SeededBackup(agent.TenantId, job.Id, run.Id, attemptId, artifact, replica, storageProvider, storageTarget, storedKey, acceptedAt);
    }

    public static async Task<IReadOnlyList<SeededBackup>> SeedBatchAsync(
        BackupStoreHarness harness, StoreAgent agent, BackupJob job, IReadOnlyList<DateTimeOffset> acceptedAts,
        string storageProvider = "local", string storageTarget = "local")
    {
        var result = new List<SeededBackup>(acceptedAts.Count);
        var runs = new List<BackupRun>(acceptedAts.Count);
        await using var context = harness.Database.CreatePlatformContext();
        var connection = await context.StorageConnections
            .Where(c => c.TenantId == agent.TenantId && c.ProviderType == storageProvider)
            .FirstOrDefaultAsync();
        if (connection is null)
        {
            connection = new StorageConnection(Guid.NewGuid(), agent.TenantId, "Default " + storageProvider, storageProvider,
                StorageOwnership.TenantManaged, "tenant", new Dictionary<string, string>(), true, acceptedAts[0]);
            context.StorageConnections.Add(connection);
            await context.SaveChangesAsync();
        }
        var connectionId = connection.Id;
        for (var i = 0; i < acceptedAts.Count; i++)
        {
            var acceptedAt = acceptedAts[i];
            var runId = Guid.NewGuid();
            var attemptId = Guid.NewGuid();
            var token = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var replicaId = Guid.NewGuid();
            var scope = ManagedBackupLayout.AttemptScope(agent.TenantId.Value, job.Id, runId, attemptId);
            var prefix = job.Configuration.StoragePrefix;
            var storedKey = $"{(prefix.Length == 0 ? "" : prefix + "/")}{scope}/{ManagedFileName}";
            var agentReportedAt = acceptedAt - TimeSpan.FromDays(400);

            var run = new BackupRun(runId, agent.TenantId, job.Id, agent.AgentId, job.Configuration, Guid.NewGuid().ToString("N"), acceptedAt - TimeSpan.FromMinutes(5), job.RetryPolicy, job.OverlapPolicy);
            run.QueueFirstAttempt(attemptId, acceptedAt - TimeSpan.FromMinutes(5));
            run.Claim(token, acceptedAt - TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10));
            run.Start(token, acceptedAt - TimeSpan.FromSeconds(30), "1.0", "1.0", "linux");

            var artifact = new BackupArtifact(artifactId, agent.TenantId, attemptId, 100, BackupStoreHarness.SourceHash, BackupStoreHarness.ArtifactHash, "sha256", agentReportedAt, "{\"DatabaseName\":\"Database\"}", "{\"CopyOnly\":true}");
            var replica = new BackupReplica(replicaId, agent.TenantId, artifactId, storageProvider, storageTarget, storedKey, 100, BackupStoreHarness.ArtifactHash, "sha256", "Verified", "ReadBack", agentReportedAt, agentReportedAt, BackupStorageExecutionLocation.Backend, null, connectionId);
            artifact.AddReplica(replica);

            var domainCompletion = new BackupPlatform.Domain.Backups.BackupCompletion(BackupStatus.Succeeded, null, artifact, []);
            run.Complete(token, acceptedAt, domainCompletion);
            runs.Add(run);
            result.Add(new SeededBackup(agent.TenantId, job.Id, runId, attemptId, artifactId, replicaId, storageProvider, storageTarget, storedKey, acceptedAt));
        }

        // Re-use opened context
        context.BackupRuns.AddRange(runs);
        await context.SaveChangesAsync();
        return result;
    }
}
