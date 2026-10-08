using System.Net;
using System.Text.Json;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Contracts.Backups;
using Xunit.Abstractions;

namespace BackupPlatform.IntegrationTests.Backup;

/// <summary>
/// Retention end to end on real parts only: PostgreSQL, the HTTPS API with its retention workers running on their real timers, the published
/// Agent.Host writing a real SQL Server backup into a real storage directory, and a backend storage configuration that reaches the same
/// directory. The retention periods are seconds, set through the job's own policy and the platform minimums.
/// </summary>
public sealed class MsSqlRetentionAgentSmokeTests(PostgreSqlFixture database, ITestOutputHelper output) : IClassFixture<PostgreSqlFixture>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ExpiredBackupWaitsOutItsGracePeriodThenItsFileIsDeletedAndTheHistoryRemains()
    {
        await using var stage = await Stage.StartAsync(database, output, retentionEnabled: true, keepForSeconds: 4, graceSeconds: 4);
        var artifact = await stage.RunBackupAsync();

        Assert.Equal("Available", artifact.GetProperty("state").GetString());
        Assert.Equal(artifact.GetProperty("acceptedAt").GetDateTimeOffset() + TimeSpan.FromSeconds(4), artifact.GetProperty("retentionExpiresAt").GetDateTimeOffset());
        var replica = Assert.Single(artifact.GetProperty("replicas").EnumerateArray());
        Assert.Equal("Available", replica.GetProperty("status").GetString());
        var file = stage.PathOf(replica);
        Assert.True(File.Exists(file), "The real backup file must exist before retention acts.");

        var pending = await stage.WaitForArtifactAsync(artifact.GetProperty("id").GetGuid(), value => value.GetProperty("state").GetString() != "Available", "the backup to expire");
        Assert.Equal("DeletePending", pending.GetProperty("state").GetString());
        var pendingReplica = Assert.Single(pending.GetProperty("replicas").EnumerateArray());
        Assert.Equal("DeletePending", pendingReplica.GetProperty("status").GetString());
        Assert.Equal("RetentionExpired", pendingReplica.GetProperty("deleteReason").GetString());
        Assert.True(pendingReplica.GetProperty("usable").GetBoolean(), "Inside its grace period the replica is intact and usable.");
        var deleteAfter = pendingReplica.GetProperty("deleteAfter").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromSeconds(4), deleteAfter - pendingReplica.GetProperty("deleteRequestedAt").GetDateTimeOffset());
        Assert.True(File.Exists(file), "Expiry alone must never delete the file: the grace period protects it.");
        Assert.True(DateTimeOffset.UtcNow < deleteAfter, "The grace period must still be running when DeletePending is first observed.");

        var deleted = await stage.WaitForArtifactAsync(artifact.GetProperty("id").GetGuid(), value => value.GetProperty("state").GetString() == "Deleted", "the grace period to end and the delete to complete");
        var deletedReplica = Assert.Single(deleted.GetProperty("replicas").EnumerateArray());
        Assert.Equal("Deleted", deletedReplica.GetProperty("status").GetString());
        Assert.False(deletedReplica.GetProperty("usable").GetBoolean());
        Assert.True(deletedReplica.GetProperty("deletedAt").GetDateTimeOffset() >= deleteAfter, "Physical deletion must not precede DeleteAfter.");
        Assert.False(File.Exists(file), "After the grace period the backup file must be physically gone.");
        Assert.Equal(replica.GetProperty("checksum").GetString(), deletedReplica.GetProperty("checksum").GetString());
        Assert.Equal(replica.GetProperty("key").GetString(), deletedReplica.GetProperty("key").GetString());

        var run = await stage.RunAsync();
        Assert.Equal("Succeeded", run.GetProperty("status").GetString());
        var historic = Assert.Single(Assert.Single(run.GetProperty("attempts").EnumerateArray()).GetProperty("artifacts").EnumerateArray());
        Assert.Equal("Deleted", historic.GetProperty("state").GetString());
        output.WriteLine("Real backup {0}: expired -> DeletePending (file kept, grace {1}s) -> Deleted at {2:O}; file removed, run/attempt/artifact history still queryable.",
            artifact.GetProperty("id").GetGuid(), 4, deletedReplica.GetProperty("deletedAt").GetDateTimeOffset());
    }
    [Fact]
    public async Task AgentLocalRetentionDeletesThroughTheActualHostWithoutBackendStorageAccess()
    {
        await using var stage = await Stage.StartAsync(database, output, true, 4, 4, agentLocal: true);
        var artifact = await stage.RunBackupAsync();
        var replica = Assert.Single(artifact.GetProperty("replicas").EnumerateArray());
        Assert.Equal("Agent", replica.GetProperty("deletionExecutor").GetString());
        var file = stage.PathOf(replica);
        Assert.True(File.Exists(file));
        var id = artifact.GetProperty("id").GetGuid();
        var pending = await stage.WaitForArtifactAsync(id, value => value.GetProperty("state").GetString() == "DeletePending", "agent-local expiry");
        var pendingReplica = Assert.Single(pending.GetProperty("replicas").EnumerateArray());
        Assert.True(File.Exists(file), "Soft deletion must leave the actual agent-local backup intact.");
        var deleteAfter = pendingReplica.GetProperty("deleteAfter").GetDateTimeOffset();
        var deleted = await stage.WaitForArtifactAsync(id, value => value.GetProperty("state").GetString() == "Deleted", "the Agent.Host deletion completion");
        var deletedReplica = Assert.Single(deleted.GetProperty("replicas").EnumerateArray());
        Assert.False(File.Exists(file));
        Assert.Equal("Deleted", deletedReplica.GetProperty("status").GetString());
        Assert.False(deletedReplica.GetProperty("waitingForAgent").GetBoolean());
        Assert.True(deletedReplica.GetProperty("deletedAt").GetDateTimeOffset() >= deleteAfter);
        var history = await stage.RunAsync();
        Assert.Equal("Succeeded", history.GetProperty("status").GetString());
        Assert.Equal(id, Assert.Single(Assert.Single(history.GetProperty("attempts").EnumerateArray()).GetProperty("artifacts").EnumerateArray()).GetProperty("id").GetGuid());
        output.WriteLine("Actual Agent.Host agent-local retention: Succeeded -> Available -> DeletePending -> DeleteReplica -> verified absent -> Deleted; no backend storage configured; run/artifact history preserved.");
    }

    [Fact]
    public async Task OfflineAgentKeepsTheActualLocalFileUntilTheHostReturnsAndClaimsDeletion()
    {
        await using var stage = await Stage.StartAsync(database, output, true, 8, 4, agentLocal: true);
        var artifact = await stage.RunBackupAsync();
        var id = artifact.GetProperty("id").GetGuid();
        var replica = Assert.Single(artifact.GetProperty("replicas").EnumerateArray());
        var file = stage.PathOf(replica);
        Assert.True(File.Exists(file));
        await stage.StopAgentAsync();
        var pending = await stage.WaitForArtifactAsync(id, value => value.GetProperty("state").GetString() == "DeletePending", "expiry while Agent.Host is stopped");
        Assert.True(File.Exists(file));
        var waiting = await stage.WaitForArtifactAsync(id, value => Assert.Single(value.GetProperty("replicas").EnumerateArray()).GetProperty("status").GetString() == "Deleting", "durable agent dispatch after grace");
        var waitingReplica = Assert.Single(waiting.GetProperty("replicas").EnumerateArray());
        Assert.Equal("Agent", waitingReplica.GetProperty("deletionExecutor").GetString());
        Assert.True(waitingReplica.GetProperty("waitingForAgent").GetBoolean());
        Assert.True(File.Exists(file), "The backend must never delete an agent-owned file, even when it exists on the test machine.");
        await stage.WaitForAgentOfflineAsync();
        Assert.True(File.Exists(file));
        Assert.NotEqual("Deleted", (await stage.ArtifactAsync(id)).GetProperty("state").GetString());
        await stage.RestartAgentAsync(output.WriteLine);
        var deleted = await stage.WaitForArtifactAsync(id, value => value.GetProperty("state").GetString() == "Deleted", "returning Agent.Host to claim deletion");
        Assert.False(File.Exists(file));
        Assert.False(Assert.Single(deleted.GetProperty("replicas").EnumerateArray()).GetProperty("waitingForAgent").GetBoolean());
        Assert.Equal("Succeeded", (await stage.RunAsync()).GetProperty("status").GetString());
        output.WriteLine("Actual offline Agent.Host: backup retained through expiry/grace and durable Deleting/waitingForAgent; host restarted -> claim -> local file absent -> Deleted; history remains.");
    }


    [Fact]
    public async Task CancellingTheDeletionDuringTheGracePeriodKeepsTheBackupFileForGood()
    {
        await using var stage = await Stage.StartAsync(database, output, retentionEnabled: true, keepForSeconds: 4, graceSeconds: 8);
        var artifact = await stage.RunBackupAsync();
        var id = artifact.GetProperty("id").GetGuid();
        var file = stage.PathOf(Assert.Single(artifact.GetProperty("replicas").EnumerateArray()));

        var pending = await stage.WaitForArtifactAsync(id, value => value.GetProperty("state").GetString() == "DeletePending", "the backup to expire");
        var deleteAfter = Assert.Single(pending.GetProperty("replicas").EnumerateArray()).GetProperty("deleteAfter").GetDateTimeOffset();
        using var cancel = await stage.Tenant.Admin.SendAsync(HttpMethod.Post, $"{stage.Root}/backup-artifacts/{id}/cancel-delete");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var restored = AgentWorld.ReadJson(cancel);
        Assert.Equal("Available", restored.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, restored.GetProperty("retentionPinnedAt").ValueKind);

        // Well past the end of the grace period, with the workers scanning every second.
        while (DateTimeOffset.UtcNow < deleteAfter + TimeSpan.FromSeconds(5))
        {
            await Task.Delay(500);
            Assert.True(File.Exists(file), "A cancelled deletion must leave the file alone.");
        }
        var after = await stage.ArtifactAsync(id);
        Assert.Equal("Available", after.GetProperty("state").GetString());
        Assert.Equal("Available", Assert.Single(after.GetProperty("replicas").EnumerateArray()).GetProperty("status").GetString());
        Assert.True(File.Exists(file));
        output.WriteLine("Real backup {0}: DeletePending -> cancel-delete -> Available (pinned); file still present {1}s after the grace period ended.", id, 5);
    }

    [Fact]
    public async Task OnlyOldManagedOrphansAndAbandonedUploadsAreSweptEverythingElseStays()
    {
        // Retention is off for this job: the real backup must survive, and the only thing under test is the orphan sweep.
        await using var stage = await Stage.StartAsync(database, output, retentionEnabled: false, keepForSeconds: 3600, graceSeconds: 3600);
        var artifact = await stage.RunBackupAsync();
        var real = stage.PathOf(Assert.Single(artifact.GetProperty("replicas").EnumerateArray()));
        var directory = Path.GetDirectoryName(real)!;
        var old = DateTime.UtcNow.AddDays(-2);

        var oldOrphan = Plant(Path.Combine(directory, "Orphan_20261005T010101Z_deadbeef_FULL.bak"), old);
        var abandonedUpload = Plant(Path.Combine(directory, $".Sales_20261005T010101Z_deadbeef_FULL.bak.{Guid.NewGuid():N}.partial"), old);
        var freshOrphan = Plant(Path.Combine(directory, "Fresh_20261006T010101Z_cafebabe_FULL.bak"), DateTime.UtcNow);
        var userFile = Plant(Path.Combine(directory, "operator-notes.txt"), old);
        var operatorBackup = Plant(Path.Combine(stage.StorageRoot, "manual", "operator_backup.bak"), old);
        // Looks exactly like a managed file but belongs to an attempt the platform has never heard of.
        var strangerDirectory = Path.Combine(stage.StorageRoot, "manual", ManagedBackupLayout.AttemptScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        var stranger = Plant(Path.Combine(strangerDirectory, "Orphan_20261005T010101Z_deadbeef_FULL.bak"), old);

        await stage.WaitUntilAsync(() => !File.Exists(oldOrphan) && !File.Exists(abandonedUpload), "the sweep to remove the old orphan and the abandoned upload");
        Assert.True(File.Exists(freshOrphan), "A file younger than OrphanMinimumAge may belong to a completion still on its way.");
        Assert.True(File.Exists(real), "A persisted replica is never an orphan.");
        Assert.True(File.Exists(userFile), "A file that is not the platform's is never touched.");
        Assert.True(File.Exists(operatorBackup), "An operator's own .bak is never touched.");
        Assert.True(File.Exists(stranger), "A managed-looking file of an unknown attempt is not provably ours and stays.");

        await stage.WaitUntilAsync(() => !File.Exists(freshOrphan), "the orphan to age past OrphanMinimumAge and be swept");
        Assert.True(File.Exists(real) && File.Exists(userFile) && File.Exists(operatorBackup) && File.Exists(stranger));
        Assert.Equal("Available", (await stage.ArtifactAsync(artifact.GetProperty("id").GetGuid())).GetProperty("state").GetString());
        output.WriteLine("Sweep removed the old orphan, the abandoned upload and (after OrphanMinimumAge) the fresh orphan; the real backup, user files and an unknown attempt's file stayed.");
    }

    private static string Plant(string path, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[64]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private sealed class Stage : IAsyncDisposable
    {
        private readonly OrchestrationSqlSource _sql;
        private readonly AgentWorld _world;
        private AgentHostSmokeProcess? _host;
        private readonly EnrolledAgent _agent;
        private readonly Guid _jobId;
        private Guid _runId;

        private Stage(OrchestrationSqlSource sql, string storageRoot, AgentWorld world, TenantSetup tenant, EnrolledAgent agent, AgentHostSmokeProcess host, Guid jobId)
        {
            _sql = sql;
            StorageRoot = storageRoot;
            _world = world;
            Tenant = tenant;
            _agent = agent;
            _host = host;
            _jobId = jobId;
        }

        public string StorageRoot { get; }
        public TenantSetup Tenant { get; }
        public string Root => $"/api/tenants/{Tenant.Tenant.Id.Value}";

        public static async Task<Stage> StartAsync(PostgreSqlFixture database, ITestOutputHelper output, bool retentionEnabled, double keepForSeconds, double graceSeconds, bool agentLocal = false)
        {
            var sql = new OrchestrationSqlSource(rows: 1500);
            await sql.InitializeAsync();
            var storageRoot = Path.Combine(Path.GetTempPath(), "bp_retention_smoke_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(storageRoot);
            try
            {
                var settings = new Dictionary<string, string?>
                {
                    ["Backups:Retention:Enabled"] = "true",
                    ["Backups:Retention:ScanInterval"] = "00:00:01",
                    ["Backups:Retention:MinimumKeepFor"] = "00:00:01",
                    ["Backups:Retention:MinimumDeleteGracePeriod"] = "00:00:01",
                    ["Backups:Retention:StorageSweepInterval"] = "00:00:01",
                    ["Backups:Retention:OrphanMinimumAge"] = "00:00:15",
                    ["Backups:Retention:PartialUploadMinimumAge"] = "00:00:15",
                    ["Backups:Retention:DeleteLeaseDuration"] = "00:00:05"
                };
                if (!agentLocal) settings["Backups:Storage:Local:RootDirectory"] = storageRoot;
                var world = await AgentWorld.CreateAsync(database, clock: new MutableClock(live: true), kestrel: true, configuration: settings,
                    connectivity: new AgentConnectivityOptions
                    {
                        HeartbeatInterval = TimeSpan.FromSeconds(5), OfflineTimeout = TimeSpan.FromSeconds(15),
                        PollInterval = TimeSpan.FromSeconds(5), SeenWriteCoalescing = TimeSpan.Zero
                    });
                var tenant = await world.CreateTenantAsync();
                await world.EntitleAsync(tenant.Tenant);
                var agent = await world.EnrollAsync(tenant);
                using var created = await tenant.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant.Tenant.Id.Value}/backup-jobs", new
                {
                    name = "Retention smoke",
                    agentId = agent.AgentId,
                    configuration = MsSqlManualAgentSmokeTests.Configuration(sql, sql.DatabaseName) with
                    {
                        StorageExecutionLocation = agentLocal ? "Agent" : "Backend"
                    },
                    retention = new { enabled = retentionEnabled, keepForSeconds, deleteGracePeriodSeconds = graceSeconds }
                });
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                var job = AgentWorld.ReadJson(created);
                Assert.Equal(keepForSeconds, job.GetProperty("retention").GetProperty("keepForSeconds").GetDouble());
                var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine, storageRoot);
                return new Stage(sql, storageRoot, world, tenant, agent, host, job.GetProperty("id").GetGuid());
            }
            catch
            {
                await sql.DisposeAsync();
                Directory.Delete(storageRoot, recursive: true);
                throw;
            }
        }

        /// <summary>Runs one real backup to success and returns its artifact as the API shows it.</summary>
        public async Task<JsonElement> RunBackupAsync()
        {
            var run = await MsSqlManualAgentSmokeTests.StartRunAsync(Tenant, _jobId, Guid.NewGuid().ToString("N"));
            _runId = run.GetProperty("id").GetGuid();
            var detail = await WaitUntilAsync(RunAsync, current => current.GetProperty("status").GetString() is "Succeeded" or "Failed" or "Cancelled" or "AgentLost", "the real backup to finish");
            Assert.Equal("Succeeded", detail.GetProperty("status").GetString());
            return Assert.Single(Assert.Single(detail.GetProperty("attempts").EnumerateArray()).GetProperty("artifacts").EnumerateArray());
        }

        public async Task<JsonElement> RunAsync()
        {
            using var response = await Tenant.Admin.GetAsync($"{Root}/backup-runs/{_runId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return AgentWorld.ReadJson(response);
        }

        public async Task<JsonElement> ArtifactAsync(Guid artifactId)
        {
            using var response = await Tenant.Admin.GetAsync($"{Root}/backup-artifacts/{artifactId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return AgentWorld.ReadJson(response);
        }

        public Task<JsonElement> WaitForArtifactAsync(Guid artifactId, Func<JsonElement, bool> done, string what) =>
            WaitUntilAsync(() => ArtifactAsync(artifactId), done, what);

        public async Task StopAgentAsync()
        {
            if (_host is null) return;
            await _host.DisposeAsync();
            _host = null;
        }

        public async Task RestartAgentAsync(Action<string> diagnostic)
        {
            await StopAgentAsync();
            _host = await AgentHostSmokeProcess.StartAsync(_world, _agent, diagnostic, StorageRoot);
        }
        public async Task WaitForAgentOfflineAsync()
        {
            var connectivity = await _world.ConnectivityOfAsync(_agent.AgentId);
            Assert.NotNull(connectivity);
            await WaitUntilAsync(() => DateTimeOffset.UtcNow >= connectivity.LastSeenAt + TimeSpan.FromSeconds(15), "backend-derived Offline");
        }


        public string PathOf(JsonElement replica) => Path.Combine(StorageRoot, replica.GetProperty("key").GetString()!.Replace('/', Path.DirectorySeparatorChar));

        public async Task<JsonElement> WaitUntilAsync(Func<Task<JsonElement>> read, Func<JsonElement, bool> done, string what)
        {
            using var timeout = new CancellationTokenSource(Patience);
            while (!timeout.IsCancellationRequested)
            {
                _host?.AssertAlive();
                var value = await read();
                if (done(value)) return value;
                await Task.Delay(100, timeout.Token).ContinueWith(_ => { }, CancellationToken.None);
            }
            throw new TimeoutException($"Timed out waiting for {what}.");
        }

        public async Task WaitUntilAsync(Func<bool> done, string what)
        {
            using var timeout = new CancellationTokenSource(Patience);
            while (!timeout.IsCancellationRequested)
            {
                if (done()) return;
                await Task.Delay(250, timeout.Token).ContinueWith(_ => { }, CancellationToken.None);
            }
            throw new TimeoutException($"Timed out waiting for {what}.");
        }

        public async ValueTask DisposeAsync()
        {
            await StopAgentAsync();
            await _world.DisposeAsync();
            await _sql.DisposeAsync();
            Directory.Delete(StorageRoot, recursive: true);
        }
    }
}
