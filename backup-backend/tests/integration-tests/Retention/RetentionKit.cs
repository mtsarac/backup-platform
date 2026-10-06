using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
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
        var configuration = BackupStoreHarness.Configuration() with { StorageExecutionLocation = executionLocation, StoragePrefix = storagePrefix };
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
}
