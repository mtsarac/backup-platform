using System.Net;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.IntegrationTests.Scheduling;
using BackupPlatform.ManagedService.Quota;
using static BackupPlatform.IntegrationTests.Quota.AdmissionTestKit;
using static BackupPlatform.IntegrationTests.Scheduling.ScheduleKit;
using static BackupPlatform.IntegrationTests.Storage.Connections.StorageConnectionPlatformLockIntegrationTests;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

public sealed class StorageConnectionPlatformLockAdmissionIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly ConnectionWorld _world = new(fixture);
    private readonly ScheduleKit _schedule = new(fixture);

    [Theory]
    [InlineData("local", "Agent")]
    [InlineData("central", "Backend")]
    public async Task PlatformLockedTargetRejectsNewManualRunsWithoutCreatingRunsOrReservations(string providerType, string location)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var (local, central) = await _world.DefaultsAsync(scenario.Setup.Tenant.Id);
        var target = providerType == "local" ? local : central;
        var jobId = await CreateTargetedJobAsync(scenario, target.Id, providerType, location);
        var platform = await PlatformAsync(scenario.World);
        await LockAsync(platform, target.Id);

        using var denied = await scenario.StartRawAsync(jobId, "locked-new-manual");
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var error = AgentWorld.ReadJson(denied);
        Assert.Equal("admission_denied", error.GetProperty("error").GetString());
        Assert.Equal(BackupAdmissionDenialCodes.StorageConnectionDisabled, error.GetProperty("code").GetString());
        Assert.Equal(0, await _world.Quota.Harness.CountRunsAsync(jobId));
        Assert.Empty(await ReservationsAsync(fixture, scenario.Setup.Tenant.Id));
        var row = await _world.RowAsync(target.Id);
        Assert.Equal((true, true, false), (row.Enabled, row.AdministrativelyDisabled, row.EffectiveEnabled));

        using var unlocked = await platform.SendAsync(HttpMethod.Post, $"/api/platform/storage-connections/{target.Id}/enable");
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        AssertState(AgentWorld.ReadJson(unlocked), true, false, true);
        using var allowed = await scenario.StartRawAsync(jobId, "unlocked-new-manual");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(1, await _world.Quota.Harness.CountRunsAsync(jobId));
    }

    [Fact]
    public async Task PlatformLockedTargetRejectsScheduledRunsBeforeQuotaAdmissionAndRecordsDenial()
    {
        var agent = await _world.Quota.AgentAsync();
        var (local, _) = await _world.DefaultsAsync(agent.TenantId);
        var configuration = BackupStoreHarness.Configuration("local", BackupStorageExecutionLocation.Agent, local.Id);
        var schedule = BackupSchedule.Create(true, "0 * * * *", "UTC", TimeSpan.FromMinutes(30), ConnectionWorld.Now);
        var job = (await _world.Quota.RunStore().CreateJobAsync(new BackupJob(Guid.NewGuid(), agent.TenantId, "Locked scheduled target",
            agent.AgentId, configuration, ConnectionWorld.Now, null, BackupOverlapPolicy.Skip, schedule), default))!;
        await using var api = await AgentWorld.CreateAsync(fixture);
        await LockAsync(await PlatformAsync(api), local.Id);
        await _schedule.SeenAsync(agent, H(1, 19));
        var admission = new CountingAdmission(_world.Quota.Admission());

        var evaluation = await _world.Quota.ScheduleStore(admission).EvaluateAsync(agent.TenantId, job.Id, H(1, 20), default);

        Assert.Null(evaluation.CreatedRunId);
        Assert.Equal(BackupOccurrenceOutcome.AdmissionDenied, Assert.Single(evaluation.Recorded).Outcome);
        var occurrence = Assert.Single(await _schedule.OccurrencesAsync(job));
        Assert.Equal((H(1), BackupOccurrenceOutcome.AdmissionDenied, BackupAdmissionDenialCodes.StorageConnectionDisabled, H(1, 20)),
            (occurrence.ScheduledAt, occurrence.Outcome, occurrence.Reason, occurrence.DecidedAt));
        Assert.Null(occurrence.RunId);
        Assert.Equal(0, await _world.Quota.Harness.CountRunsAsync(job.Id));
        Assert.Empty(await ReservationsAsync(fixture, agent.TenantId));
        Assert.Equal(0, admission.Calls);
    }

    [Theory]
    [InlineData("None", 1)]
    [InlineData("Manual", 2)]
    [InlineData("Automatic", 2)]
    public async Task PreviouslyAdmittedRunsAndRetriesStillPublishToAPlatformLockedConnection(string retry, int attempts)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var (local, _) = await _world.DefaultsAsync(scenario.Setup.Tenant.Id);
        var jobId = await CreateTargetedJobAsync(scenario, local.Id, "local", "Agent");
        var runId = await scenario.StartRunAsync(jobId);
        await LockAsync(await PlatformAsync(scenario.World), local.Id);
        if (retry == "Manual")
        {
            await scenario.FailNextAsync("PreCheckFailed");
            using var requested = await scenario.RetryRawAsync(runId);
            Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
        }
        if (retry == "Automatic")
        {
            await scenario.FailNextAsync("DatabaseUnavailable");
            scenario.Clock.Advance(TimeSpan.FromSeconds(30));
            await scenario.ScanRetriesAsync();
        }
        var completed = await scenario.SucceedNextAsync();
        Assert.Equal(runId, completed.RunId);
        var run = await scenario.RunAsync(runId);
        Assert.Equal("Succeeded", BackupApiScenario.StatusOf(run));
        Assert.Equal(attempts, BackupApiScenario.AttemptsOf(run).Count);
        var artifact = RetentionApiSupport.ArtifactOf(run);
        var replica = Assert.Single(await _world.ReplicasAsync(artifact.GetProperty("id").GetGuid()));
        Assert.Equal((local.Id, BackupReplicaStatus.Available), (replica.StorageConnectionId, replica.Status));
        await _world.Quota.AssertUsedAsync(scenario.Setup.Tenant.Id, 1024);
        var row = await _world.RowAsync(local.Id);
        Assert.Equal((true, true, false), (row.Enabled, row.AdministrativelyDisabled, row.EffectiveEnabled));
    }

    [Theory]
    [InlineData(true, "AgentLocal")]
    [InlineData(false, "CentralStorage")]
    public async Task ExistingReplicaRestoreAccessRemainsUsableWhileItsConnectionIsPlatformLocked(bool agentLocal, string sourceAccess)
    {
        await using var scenario = await RestoreApiScenario.CreateAsync(fixture);
        var accepted = await scenario.AcceptBackupAsync(agentLocal: agentLocal);
        var (local, central) = await _world.DefaultsAsync(new TenantId(scenario.TenantId));
        var target = agentLocal ? local : central;
        var replica = Assert.Single(await _world.ReplicasAsync(accepted.ArtifactId));
        Assert.Equal(target.Id, replica.StorageConnectionId);
        await LockAsync(await PlatformAsync(scenario.World), target.Id);

        var created = await scenario.StartAsync(accepted.ArtifactId, scenario.Body(replicaId: replica.Id));
        Assert.Equal(sourceAccess, created.GetProperty("sourceAccess").GetString());
        Assert.Equal(replica.Id, created.GetProperty("replicaId").GetGuid());
        var task = await scenario.RunToCompletionAsync(result: RestoreApiScenario.SampleResult());
        Assert.Equal(created.GetProperty("id").GetGuid(), task.OperationId);
        Assert.Equal(sourceAccess, task.Snapshot.GetProperty("source").GetProperty("access").GetString());
        Assert.Equal(accepted.Key, task.Snapshot.GetProperty("source").GetProperty("storageKey").GetString());
        var restored = await scenario.RestoreAsync(task.OperationId);
        Assert.Equal("Succeeded", RestoreApiScenario.StatusOf(restored));
        Assert.Equal("Online", restored.GetProperty("targetState").GetString());
        Assert.Equal(BackupReplicaStatus.Available, Assert.Single(await _world.ReplicasAsync(accepted.ArtifactId)).Status);
        var row = await _world.RowAsync(target.Id);
        Assert.Equal((true, true, false), (row.Enabled, row.AdministrativelyDisabled, row.EffectiveEnabled));
    }

    private static async Task<Guid> CreateTargetedJobAsync(BackupApiScenario scenario, Guid connectionId, string providerType, string location)
    {
        using var response = await scenario.CreateJobRawAsync(new
        {
            name = "Explicit connection target", agentId = scenario.Agent.AgentId,
            configuration = new BackupExecutionConfiguration
            {
                Database = "master", Host = "sql-server", IntegratedSecurity = true,
                Destinations = [new BackupDestinationContract { StorageProvider = providerType, StorageExecutionLocation = location, StorageConnectionId = connectionId, IsPrimary = true }]
            },
            retryPolicy = new { maxAttempts = 3, initialDelaySeconds = 30.0, maxDelaySeconds = 600.0, backoff = "Fixed", jitter = 0.0 }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return AgentWorld.ReadJson(response).GetProperty("id").GetGuid();
    }

    private static async Task<BrowserSession> PlatformAsync(AgentWorld api) =>
        await api.SignInAsync(await api.SeedUserAsync(platformAdmin: true));

    private static async Task LockAsync(BrowserSession platform, Guid connectionId)
    {
        using var response = await platform.SendAsync(HttpMethod.Post, $"/api/platform/storage-connections/{connectionId}/disable");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertState(AgentWorld.ReadJson(response), true, true, false);
    }
}
