using System.Net;
using System.Text.Json;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Local;
using static BackupPlatform.IntegrationTests.RetentionApiSupport;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentReplicaDeletionApiTests(PostgreSqlFixture database) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OwningAgentAloneCanClaimAndCompleteWithExactDuplicateCompletionAccepted()
    {
        await using var scenario = await CreateAsync();
        var backup = await AcceptAgentBackupAsync(scenario);
        await MakeDueAsync(scenario);
        var other = await scenario.World.EnrollAsync(scenario.Setup, Guid.NewGuid());
        var outsider = await scenario.World.EnrollAsync(await scenario.World.CreateTenantAsync(), Guid.NewGuid());
        foreach (var stranger in new[] { other, outsider })
        {
            Assert.Empty(await TasksAsync(scenario, stranger));
        }
        var task = Assert.Single(await TasksAsync(scenario));
        var deletion = task.GetProperty("deleteReplica").Deserialize<DeleteReplicaTaskSnapshot>(JsonSerializerOptions.Web)!;
        Assert.Equal("delete-replica", task.GetProperty("type").GetString());
        Assert.Equal(scenario.Agent.AgentId, deletion.AgentId);
        var success = new DeleteReplicaCompletionRequest { OperationId = deletion.OperationId, LeaseToken = deletion.LeaseToken, AbsenceVerified = true };
        foreach (var stranger in new[] { other, outsider })
        {
            using var rejected = await CompleteAsync(scenario, deletion, success, stranger);
            Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        }
        using (var wrongToken = await CompleteAsync(scenario, deletion, success with { LeaseToken = Guid.NewGuid() }))
            Assert.Equal(HttpStatusCode.Conflict, wrongToken.StatusCode);
        using (var wrongOperation = await CompleteAsync(scenario, deletion, success with { OperationId = Guid.NewGuid() }))
            Assert.Equal(HttpStatusCode.Conflict, wrongOperation.StatusCode);
        using (var missingProof = await CompleteAsync(scenario, deletion, success with { AbsenceVerified = false }))
            Assert.Equal(HttpStatusCode.BadRequest, missingProof.StatusCode);
        using (var completed = await CompleteAsync(scenario, deletion, success))
            Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        using (var duplicate = await CompleteAsync(scenario, deletion, success))
            Assert.Equal(HttpStatusCode.NoContent, duplicate.StatusCode);
        using (var changedOutcome = await CompleteAsync(scenario, deletion, success with { AbsenceVerified = false, FailureCode = DeleteReplicaFailureCodes.IoError }))
            Assert.Equal(HttpStatusCode.Conflict, changedOutcome.StatusCode);
        var replica = ReplicaOf(await ArtifactAsync(scenario, backup.ArtifactId));
        Assert.Equal("Deleted", replica.GetProperty("status").GetString());
        Assert.False(replica.TryGetProperty("operationId", out _));
        Assert.False(replica.TryGetProperty("leaseToken", out _));
    }

    [Fact]
    public async Task LostCompletionRedeliversTheAbsentObjectAndFencesTheExpiredClaim()
    {
        await using var scenario = await CreateAsync();
        var root = Directory.CreateTempSubdirectory("agent-delete-api-").FullName;
        try
        {
            string file = string.Empty;
            var backup = await AcceptAgentBackupAsync(scenario, key =>
            {
                file = Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "physical backup bytes");
            });
            await MakeDueAsync(scenario);
            Assert.True(File.Exists(file), "Backend dispatch must not delete the agent-local object.");
            var first = DeletionOf(Assert.Single(await TasksAsync(scenario)));
            var provider = new LocalStorageProvider(new LocalStorageOptions(root));
            var key = new StorageObjectKey(first.StorageKey);
            Assert.True(await provider.DeleteAsync(key, CancellationToken.None));
            Assert.False(await provider.ExistsAsync(key, CancellationToken.None));
            scenario.Clock.Advance(TimeSpan.FromSeconds(6));
            await RunRetentionAsync(scenario);
            var second = DeletionOf(Assert.Single(await TasksAsync(scenario)));
            Assert.NotEqual(first.LeaseToken, second.LeaseToken);
            Assert.Equal(first.OperationId, second.OperationId);
            using (var stale = await CompleteAsync(scenario, first, Success(first)))
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.False(await provider.DeleteAsync(new StorageObjectKey(second.StorageKey), CancellationToken.None));
            Assert.False(await provider.ExistsAsync(new StorageObjectKey(second.StorageKey), CancellationToken.None));
            using (var completed = await CompleteAsync(scenario, second, Success(second)))
                Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
            Assert.Equal("Deleted", ReplicaOf(await ArtifactAsync(scenario, backup.ArtifactId)).GetProperty("status").GetString());
            Assert.Equal("Succeeded", BackupApiScenario.StatusOf(await scenario.RunAsync(backup.RunId)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TwoPollsClaimOnceAndDeletionDoesNotConsumeTheBackupSlot()
    {
        await using var scenario = await CreateAsync();
        await AcceptAgentBackupAsync(scenario);
        await MakeDueAsync(scenario);
        var job = await scenario.CreateJobAsync();
        var run = await scenario.StartRunAsync(job);
        var polls = await Task.WhenAll(TasksAsync(scenario), TasksAsync(scenario));
        var tasks = polls.SelectMany(value => value).ToArray();
        Assert.Single(tasks, task => task.GetProperty("type").GetString() == "delete-replica");
        var backup = Assert.Single(tasks, task => task.GetProperty("type").GetString() == "database-backup");
        Assert.Equal(run, backup.GetProperty("backup").GetProperty("runId").GetGuid());
    }

    [Fact]
    public async Task CancelAndDispatchRaceCannotDeliverWorkForARestoredReplica()
    {
        await using var scenario = await CreateAsync();
        var backup = await AcceptAgentBackupAsync(scenario);
        scenario.Clock.Advance(TimeSpan.FromSeconds(2));
        await RunRetentionAsync(scenario);
        scenario.Clock.Advance(TimeSpan.FromSeconds(2));
        var dispatch = RunRetentionAsync(scenario);
        var cancel = CancelDeleteAsync(scenario, backup.ArtifactId);
        await dispatch;
        using var cancelled = await cancel;
        var tasks = await TasksAsync(scenario);
        var replica = ReplicaOf(await ArtifactAsync(scenario, backup.ArtifactId));
        if (cancelled.StatusCode == HttpStatusCode.OK)
        {
            Assert.Empty(tasks);
            Assert.Equal("Available", replica.GetProperty("status").GetString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, cancelled.StatusCode);
            Assert.Single(tasks);
            Assert.Equal("Deleting", replica.GetProperty("status").GetString());
        }
    }

    private Task<BackupApiScenario> CreateAsync()
    {
        var settings = WorkerConfiguration();
        settings["Backups:Retention:DeleteLeaseDuration"] = "00:00:05";
        settings["Backups:Retention:DeleteRetryInitialDelay"] = "00:00:01";
        return BackupApiScenario.CreateAsync(database, settings, new MutableClock(Start));
    }

    private static async Task<AcceptedBackup> AcceptAgentBackupAsync(BackupApiScenario scenario, Action<string>? stage = null)
    {
        using var created = await scenario.CreateJobRawAsync(new
        {
            name = "Agent-local retention", agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration { Database = "master", Host = "sql-server", IntegratedSecurity = true, Destinations = BackupTestDestinations.LocalAgent },
            retention = Policy(true, 2, 2)
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        var runId = await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);
        var key = ManagedKey(scenario, jobId, claim);
        stage?.Invoke(key);
        var completion = scenario.SuccessRequest(claim);
        var artifact = completion.Artifact!;
        completion = completion with { Artifact = artifact with { Replicas = [artifact.Replicas[0] with { StorageTarget = "local", Key = key }] } };
        using var accepted = await scenario.CompleteRawAsync(claim, completion);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        var detail = ArtifactOf(await scenario.RunAsync(runId));
        return new AcceptedBackup(jobId, runId, claim.AttemptId, detail.GetProperty("id").GetGuid(), key, scenario.Clock.GetUtcNow());
    }

    private static async Task MakeDueAsync(BackupApiScenario scenario)
    {
        scenario.Clock.Advance(TimeSpan.FromSeconds(2));
        await RunRetentionAsync(scenario);
        scenario.Clock.Advance(TimeSpan.FromSeconds(2));
        await RunRetentionAsync(scenario);
    }

    private static async Task<JsonElement[]> TasksAsync(BackupApiScenario scenario, EnrolledAgent? agent = null)
    {
        using var response = await scenario.World.PollAsync(agent ?? scenario.Agent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response).GetProperty("tasks").EnumerateArray().Select(value => value.Clone()).ToArray();
    }

    private static DeleteReplicaTaskSnapshot DeletionOf(JsonElement task) => task.GetProperty("deleteReplica").Deserialize<DeleteReplicaTaskSnapshot>(JsonSerializerOptions.Web)!;
    private static DeleteReplicaCompletionRequest Success(DeleteReplicaTaskSnapshot task) => new() { OperationId = task.OperationId, LeaseToken = task.LeaseToken, AbsenceVerified = true };
    private static Task<HttpResponseMessage> CompleteAsync(BackupApiScenario scenario, DeleteReplicaTaskSnapshot task, DeleteReplicaCompletionRequest body, EnrolledAgent? caller = null)
    {
        var agent = caller ?? scenario.Agent;
        return scenario.World.SendAgentAsync(HttpMethod.Post, $"/api/agent/replica-deletions/{task.ReplicaId}/complete", agent.CredentialId, agent.Secret, body: body);
    }
}
