using System.Net;
using static BackupPlatform.IntegrationTests.RetentionApiSupport;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// The whole lifecycle through the API with a real storage directory: expiry soft-deletes, the grace period keeps the file, and only afterwards
/// the backend's own worker removes it. The worker is driven by hand at explicit clock values; nothing sleeps.
/// </summary>
public sealed class RetentionApiStorageFlowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>, IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 8, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan One = TimeSpan.FromSeconds(1);

    private readonly string _root = Directory.CreateTempSubdirectory("retention-api-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string Stage(string key)
    {
        var path = Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "backup bytes");
        return path;
    }

    private async Task<(BackupApiScenario Scenario, AcceptedBackup Backup, string Path)> ScenarioWithStagedBackupAsync(object? retention, bool storageConfigured = true)
    {
        var scenario = await BackupApiScenario.CreateAsync(fixture, WorkerConfiguration(storageConfigured ? _root : null), new MutableClock(Start));
        var jobId = await CreateJobAsync(scenario, retention);
        string path = string.Empty;
        var backup = await AcceptBackupAsync(scenario, jobId, claim => ManagedKey(scenario, jobId, claim), key => path = Stage(key));
        return (scenario, backup, path);
    }

    [Fact]
    public async Task AnExpiredBackupIsSoftDeletedFirstAndTheFileIsRemovedOnlyAfterTheGracePeriodWhileHistoryStays()
    {
        var (scenario, backup, path) = await ScenarioWithStagedBackupAsync(Policy(true, 2, 2));
        await using var _ = scenario;
        var accepted = backup.AcceptedAt;

        scenario.Clock.Advance(One); // one second before expiry
        await RunRetentionAsync(scenario);
        Assert.Equal("Available", (await ArtifactAsync(scenario, backup.ArtifactId)).GetProperty("state").GetString());

        scenario.Clock.Advance(One); // exactly at expiry: the boundary is inclusive
        var expiry = await RunRetentionAsync(scenario);
        var pending = await ArtifactAsync(scenario, backup.ArtifactId);
        var pendingReplica = ReplicaOf(pending);
        Assert.Equal(0, expiry.Deleted);
        Assert.Equal("DeletePending", pending.GetProperty("state").GetString());
        Assert.Equal("RetentionExpired", pending.GetProperty("deleteReason").GetString());
        Assert.Equal("DeletePending", pendingReplica.GetProperty("status").GetString());
        Assert.True(pendingReplica.GetProperty("usable").GetBoolean());
        Assert.Equal(accepted + 2 * One + 2 * One, pendingReplica.GetProperty("deleteAfter").GetDateTimeOffset());
        Assert.True(File.Exists(path)); // soft delete only

        scenario.Clock.Advance(One); // one second of grace left
        await RunRetentionAsync(scenario);
        Assert.True(File.Exists(path));
        Assert.Equal("DeletePending", ReplicaOf(await ArtifactAsync(scenario, backup.ArtifactId)).GetProperty("status").GetString());

        scenario.Clock.Advance(One); // the grace period is over
        var cycle = await RunRetentionAsync(scenario);

        Assert.Equal(0, cycle.Failed);
        Assert.False(File.Exists(path));
        var deleted = await ArtifactAsync(scenario, backup.ArtifactId);
        var replica = ReplicaOf(deleted);
        Assert.Equal("Deleted", deleted.GetProperty("state").GetString());
        Assert.Equal("Deleted", replica.GetProperty("status").GetString());
        Assert.False(replica.GetProperty("usable").GetBoolean());
        Assert.Equal(scenario.Clock.GetUtcNow(), replica.GetProperty("deletedAt").GetDateTimeOffset());
        Assert.Equal(backup.Key, replica.GetProperty("key").GetString()); // location and checksum stay as history
        Assert.Equal(new string('b', 64), replica.GetProperty("checksum").GetString());

        var run = await scenario.RunAsync(backup.RunId);
        Assert.Equal("Succeeded", BackupApiScenario.StatusOf(run));
        Assert.Equal(backup.ArtifactId, ArtifactOf(run).GetProperty("id").GetGuid());
        Assert.Equal("Deleted", ArtifactOf(run).GetProperty("state").GetString());
        using var list = await scenario.Admin.GetAsync(scenario.Root + "/backup-artifacts");
        Assert.Equal(backup.ArtifactId, Assert.Single(AgentWorld.ReadJson(list).EnumerateArray()).GetProperty("id").GetGuid());

        using var deleteAgain = await DeleteArtifactAsync(scenario, backup.ArtifactId);
        using var cancel = await CancelDeleteAsync(scenario, backup.ArtifactId);
        Assert.Equal(HttpStatusCode.Conflict, deleteAgain.StatusCode);
        Assert.Equal("already_deleted", AgentWorld.ReadJson(deleteAgain).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Equal("not_cancellable", AgentWorld.ReadJson(cancel).GetProperty("error").GetString());
    }

    [Fact]
    public async Task CancellingDuringTheGracePeriodKeepsTheFileAndPinsTheExpiredBackupSoItIsNotDeletedAgain()
    {
        var (scenario, backup, path) = await ScenarioWithStagedBackupAsync(Policy(true, 2, 2));
        await using var _ = scenario;
        scenario.Clock.Advance(2 * One);
        await RunRetentionAsync(scenario);
        Assert.Equal("DeletePending", (await ArtifactAsync(scenario, backup.ArtifactId)).GetProperty("state").GetString());

        using var cancel = await CancelDeleteAsync(scenario, backup.ArtifactId);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var restored = AgentWorld.ReadJson(cancel);
        Assert.Equal("Available", restored.GetProperty("state").GetString());
        Assert.Equal(scenario.Clock.GetUtcNow(), restored.GetProperty("retentionPinnedAt").GetDateTimeOffset());
        foreach (var advance in new[] { 10 * One, TimeSpan.FromDays(1) })
        {
            scenario.Clock.Advance(advance);
            await RunRetentionAsync(scenario);
            Assert.True(File.Exists(path));
            var artifact = await ArtifactAsync(scenario, backup.ArtifactId);
            Assert.Equal("Available", artifact.GetProperty("state").GetString());
            Assert.Equal("Available", ReplicaOf(artifact).GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task AManualDeleteNeverRemovesTheFileItselfOnlyTheWorkerDoesAfterTheGracePeriod()
    {
        var (scenario, backup, path) = await ScenarioWithStagedBackupAsync(retention: null);
        await using var _ = scenario;

        using var request = await DeleteArtifactAsync(scenario, backup.ArtifactId);
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        Assert.True(File.Exists(path));
        await RunRetentionAsync(scenario);
        Assert.True(File.Exists(path));

        scenario.Clock.Advance(TimeSpan.FromHours(24) - One);
        await RunRetentionAsync(scenario);
        Assert.True(File.Exists(path));
        Assert.Equal("DeletePending", (await ArtifactAsync(scenario, backup.ArtifactId)).GetProperty("state").GetString());

        scenario.Clock.Advance(One);
        await RunRetentionAsync(scenario);
        Assert.False(File.Exists(path));
        Assert.Equal("Deleted", (await ArtifactAsync(scenario, backup.ArtifactId)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task AFileThatIsAlreadyGoneCountsAsDeleted()
    {
        var (scenario, backup, path) = await ScenarioWithStagedBackupAsync(Policy(true, 2, 2));
        await using var _ = scenario;
        scenario.Clock.Advance(2 * One);
        await RunRetentionAsync(scenario);
        File.Delete(path); // removed by a previous attempt whose database commit was lost, or by hand

        scenario.Clock.Advance(2 * One);
        await RunRetentionAsync(scenario);

        var replica = ReplicaOf(await ArtifactAsync(scenario, backup.ArtifactId));
        Assert.Equal("Deleted", replica.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AReplicaOnAStorageTheBackendCannotReachFailsWithAStableCodeAndRetriesWithBackoffInsteadOfSpinning()
    {
        var (scenario, backup, path) = await ScenarioWithStagedBackupAsync(Policy(true, 2, 2), storageConfigured: false);
        await using var _ = scenario;
        scenario.Clock.Advance(2 * One);
        await RunRetentionAsync(scenario);
        scenario.Clock.Advance(2 * One);

        await RunRetentionAsync(scenario);

        var failedAt = scenario.Clock.GetUtcNow();
        var failed = await ArtifactAsync(scenario, backup.ArtifactId);
        var replica = ReplicaOf(failed);
        Assert.Equal("DeletePending", failed.GetProperty("state").GetString()); // never reported as deleted
        Assert.Equal("DeleteFailed", replica.GetProperty("status").GetString());
        Assert.Equal("storage_not_configured", replica.GetProperty("deleteFailureCode").GetString());
        Assert.Equal(1, replica.GetProperty("deleteAttempts").GetInt32());
        var next = replica.GetProperty("deleteNextAttemptAt").GetDateTimeOffset();
        Assert.InRange(next - failedAt, One, TimeSpan.FromHours(1));
        Assert.True(File.Exists(path));

        var again = await RunRetentionAsync(scenario); // same clock: nothing is due, nothing is retried
        Assert.Equal(0, again.DeletionsClaimed);
        var unchanged = ReplicaOf(await ArtifactAsync(scenario, backup.ArtifactId));
        Assert.Equal(1, unchanged.GetProperty("deleteAttempts").GetInt32());
        Assert.Equal(next, unchanged.GetProperty("deleteNextAttemptAt").GetDateTimeOffset());

        scenario.Clock.Advance(next - failedAt);
        await RunRetentionAsync(scenario);

        var retried = ReplicaOf(await ArtifactAsync(scenario, backup.ArtifactId));
        Assert.Equal(2, retried.GetProperty("deleteAttempts").GetInt32());
        Assert.Equal("DeleteFailed", retried.GetProperty("status").GetString());
        Assert.True(retried.GetProperty("deleteNextAttemptAt").GetDateTimeOffset() - scenario.Clock.GetUtcNow() >= next - failedAt); // the wait does not shrink
    }
}
