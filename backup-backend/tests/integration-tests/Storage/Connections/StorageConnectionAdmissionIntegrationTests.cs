using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.IntegrationTests.Scheduling;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.Storage;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.Quota.AdmissionTestKit;
using static BackupPlatform.IntegrationTests.Scheduling.ScheduleKit;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

/// <summary>
/// Proves admission, gating and replica publication against real PostgreSQL: disabling a connection blocks new runs only; runs admitted earlier
/// still finish and publish; stored replicas remain available, downloadable and subject to retention; jobs naming mismatched connections are rejected.
/// </summary>
public sealed class StorageConnectionAdmissionIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const long Gb = QuotaWorld.Gb;
    private readonly ConnectionWorld _world = new(fixture);
    private readonly ScheduleKit _schedule = new(fixture);

    // ---- disabled connection denies new runs -----------------------------------------------------------------------------------

    [Fact]
    public async Task ADisabledConnectionDeniesAManualRunAtStartWithoutCreatingARunOrAReservation()
    {
        var agent = await _world.Quota.AgentAsync(limitBytes: null);
        var job = await _world.Quota.JobAsync(agent);
        var (_, central) = await _world.DefaultsAsync(agent.TenantId);
        await _world.SetEnabledAsync(central.Id, agent.TenantId, false);

        var started = await _world.Quota.RunStore().StartRunAsync(agent.TenantId, job.Id, "run-denied", ConnectionWorld.Now, default);

        Assert.Equal(BackupStartOutcome.AdmissionDenied, started.Outcome);
        Assert.Equal(BackupAdmissionDenialCodes.StorageConnectionDisabled, started.DenialCode);
        Assert.Null(started.Run);
        Assert.Equal(0, await _world.Quota.Harness.CountRunsAsync(job.Id));
        Assert.Empty(await ReservationsAsync(fixture, agent.TenantId));

        // Re-enabling lets runs start again normally.
        await _world.SetEnabledAsync(central.Id, agent.TenantId, true);
        var recovered = await _world.Quota.RunStore().StartRunAsync(agent.TenantId, job.Id, "run-allowed", ConnectionWorld.Now, default);
        Assert.Equal(BackupStartOutcome.Started, recovered.Outcome);
        Assert.NotNull(recovered.Run);
    }

    [Fact]
    public async Task ADisabledConnectionDeniesAnAgentLocalJobAsWell()
    {
        var agent = await _world.Quota.AgentAsync();
        var job = await _world.Quota.JobAsync(agent, location: BackupStorageExecutionLocation.Agent);
        var (local, _) = await _world.DefaultsAsync(agent.TenantId);
        await _world.SetEnabledAsync(local.Id, agent.TenantId, false);

        var started = await _world.Quota.RunStore().StartRunAsync(agent.TenantId, job.Id, "run-local-denied", ConnectionWorld.Now, default);

        Assert.Equal(BackupStartOutcome.AdmissionDenied, started.Outcome);
        Assert.Equal(BackupAdmissionDenialCodes.StorageConnectionDisabled, started.DenialCode);
    }

    [Fact]
    public async Task AScheduledEvaluationOfAJobTargetingADisabledConnectionRecordsAnAdmissionDeniedOccurrence()
    {
        var agent = await _world.Quota.AgentAsync();
        var job = await _schedule.AddJobAsync(agent);
        var (local, _) = await _world.DefaultsAsync(agent.TenantId); // schedule kit uses local/agent
        await _world.SetEnabledAsync(local.Id, agent.TenantId, false);
        await _schedule.SeenAsync(agent, H(1, 19));
        var admission = new CountingAdmission(_world.Quota.Admission());
        var scheduleStore = _world.Quota.ScheduleStore(admission);

        var evaluation = await scheduleStore.EvaluateAsync(agent.TenantId, job.Id, H(1, 20), default);

        Assert.Null(evaluation.CreatedRunId);
        Assert.Equal(BackupOccurrenceOutcome.AdmissionDenied, Assert.Single(evaluation.Recorded).Outcome);
        Assert.Equal(0, await _world.Quota.Harness.CountRunsAsync(job.Id));
        var occurrence = Assert.Single(await _schedule.OccurrencesAsync(job));
        Assert.Equal((H(1), BackupOccurrenceOutcome.AdmissionDenied, BackupAdmissionDenialCodes.StorageConnectionDisabled, H(1, 20)),
            (occurrence.ScheduledAt, occurrence.Outcome, occurrence.Reason, occurrence.DecidedAt));
        Assert.Null(occurrence.RunId);
        // The storage gate refused the run before quota admission was ever asked.
        Assert.Equal(0, admission.Calls);
    }

    // ---- a run admitted BEFORE the disable keeps running and publishes replicas ------------------------------------------------

    [Fact]
    public async Task ARunAdmittedBeforeTheDisableStillCompletesPublishesReplicasAndCountsInUsage()
    {
        var agent = await _world.Quota.AgentAsync(limitBytes: null);
        var job = await _world.Quota.JobAsync(agent);
        var started = await _world.Quota.RunStore().StartRunAsync(agent.TenantId, job.Id, "run-pre-disable", ConnectionWorld.Now, default);
        var (_, central) = await _world.DefaultsAsync(agent.TenantId);

        // Connection is disabled while the run executes.
        await _world.SetEnabledAsync(central.Id, agent.TenantId, false);

        var claim = await BackupStoreHarness.ClaimAsync(_world.Quota.RunStore(), agent, ConnectionWorld.Now);
        Assert.NotNull(claim);
        await BackupStoreHarness.StartAttemptAsync(_world.Quota.RunStore(), claim!, ConnectionWorld.Now);
        await BackupStoreHarness.CompleteAsync(_world.Quota.RunStore(), claim!, QuotaWorld.Success(2 * Gb, [ReplicaSpec.Central], ConnectionWorld.Now), ConnectionWorld.Now);

        await _world.Quota.AssertUsedAsync(agent.TenantId, 2 * Gb);
        var run = await _world.Quota.RunStore().GetRunAsync(agent.TenantId, started.Run!.Id, default);
        Assert.Equal(BackupStatus.Succeeded, run!.Status);
        var replica = Assert.Single(await _world.ReplicasAsync(await StoredArtifactIdAsync(agent.TenantId)));
        Assert.Equal((central.Id, "central", BackupReplicaStatus.Available), (replica.StorageConnectionId, replica.StorageProvider, replica.Status));
    }

    // ---- replicas on a disabled connection stay available, counted and subject to retention and download -----------------------

    [Fact]
    public async Task ReplicasOnADisabledConnectionStayAvailableAndKeepCountingInUsage()
    {
        var agent = await _world.Quota.AgentAsync(100 * Gb);
        var job = await _world.Quota.JobAsync(agent);
        var (_, artifactId) = await _world.Quota.PublishCopiesAsync(agent, job, 2 * Gb, ReplicaSpec.Central);
        var (_, central) = await _world.DefaultsAsync(agent.TenantId);

        await _world.SetEnabledAsync(central.Id, agent.TenantId, false);

        var replica = Assert.Single(await _world.ReplicasAsync(artifactId));
        Assert.Equal(BackupReplicaStatus.Available, replica.Status);
        await _world.Quota.AssertUsedAsync(agent.TenantId, 2 * Gb);
        await _world.Quota.AssertOracleAsync(agent.TenantId, 2 * Gb, 2 * Gb);
    }

    [Fact]
    public async Task DownloadAvailabilityAndContentStillWorkForAnArtifactWhoseConnectionIsPlatformLocked()
    {
        await using var download = await DownloadApiScenario.CreateAsync(fixture);
        var scenario = download.Backup;
        var (_, central) = await _world.DefaultsAsync(new TenantId(scenario.TenantId));

        // Disable through platform admin.
        var platform = await scenario.Backup.World.SignInAsync(await scenario.Backup.World.SeedUserAsync(platformAdmin: true));
        using var disabled = await platform.SendAsync(HttpMethod.Post, $"/api/platform/storage-connections/{central.Id}/disable");
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        StorageConnectionPlatformLockIntegrationTests.AssertState(AgentWorld.ReadJson(disabled), true, true, false);
        var replica = Assert.Single(await _world.ReplicasAsync(download.Accepted.ArtifactId));
        Assert.Equal((download.ReplicaId, central.Id, BackupReplicaStatus.Available), (replica.Id, replica.StorageConnectionId, replica.Status));

        // Download availability still says available.
        using var availability = await download.Admin.GetAsync(download.ArtifactPath + "/download-availability");
        Assert.Equal(HttpStatusCode.OK, availability.StatusCode);
        var available = AgentWorld.ReadJson(availability);
        Assert.True(available.GetProperty("available").GetBoolean());
        Assert.True(available.GetProperty("replicas").EnumerateArray().Single().GetProperty("available").GetBoolean());

        // And the bytes can still be downloaded.
        var session = await download.CreateSessionAsync();
        using var content = await download.Admin.Client.GetAsync(session.GetProperty("downloadUrl").GetString()!);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(download.Bytes, await content.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AManualDeleteAndRetentionCycleWorkForAnArtifactOnAPlatformLockedConnection()
    {
        var root = Directory.CreateTempSubdirectory("admission-retention-").FullName;
        try
        {
            var scenario = await BackupApiScenario.CreateAsync(fixture, RetentionApiSupport.WorkerConfiguration(root));
            await using var _ = scenario;
            var jobId = await RetentionApiSupport.CreateJobAsync(scenario, null);
            string path = string.Empty;
            var backup = await RetentionApiSupport.AcceptBackupAsync(scenario, jobId,
                claim => RetentionApiSupport.ManagedKey(scenario, jobId, claim),
                key =>
                {
                    path = Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "backup bytes");
                });
            var (local, _) = await _world.DefaultsAsync(scenario.Setup.Tenant.Id);
            var platform = await scenario.World.SignInAsync(await scenario.World.SeedUserAsync(platformAdmin: true));
            using var locked = await platform.SendAsync(HttpMethod.Post, $"/api/platform/storage-connections/{local.Id}/disable");
            Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
            StorageConnectionPlatformLockIntegrationTests.AssertState(AgentWorld.ReadJson(locked), true, true, false);
            Assert.Equal(BackupReplicaStatus.Available, Assert.Single(await _world.ReplicasAsync(backup.ArtifactId)).Status);

            using var requested = await RetentionApiSupport.DeleteArtifactAsync(scenario, backup.ArtifactId);
            Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);

            scenario.Clock.Advance(TimeSpan.FromHours(24));
            var cycle = await RetentionApiSupport.RunRetentionAsync(scenario);

            Assert.Equal(0, cycle.Failed);
            Assert.False(File.Exists(path));
            var artifact = await RetentionApiSupport.ArtifactAsync(scenario, backup.ArtifactId);
            Assert.Equal("Deleted", artifact.GetProperty("state").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ---- explicit connection validation on job and run --------------------------------------------------------------------------

    [Fact]
    public async Task CreatingAJobTargetingAnotherTenantsConnectionIsRejected()
    {
        var agent = await _world.Quota.AgentAsync();
        var stranger = await _world.TenantAsync();
        var (_, foreignCentral) = await _world.DefaultsAsync(stranger);
        var configuration = BackupStoreHarness.Configuration("central", BackupStorageExecutionLocation.Backend, foreignCentral.Id);
        var job = new BackupJob(Guid.NewGuid(), agent.TenantId, "Alien", agent.AgentId, configuration, ConnectionWorld.Now, null, BackupOverlapPolicy.Queue);

        var rejection = await Assert.ThrowsAsync<InvalidBackupJobException>(() => _world.Quota.RunStore().CreateJobAsync(job, default));

        Assert.Equal("configuration.destinations[0].storageConnectionId", rejection.Field);
    }

    [Fact]
    public async Task CreatingAJobTargetingAConnectionOfAnotherProviderTypeIsRejected()
    {
        var agent = await _world.Quota.AgentAsync();
        var (local, _) = await _world.DefaultsAsync(agent.TenantId);
        // The job's provider is central, but the connection is the local one.
        var configuration = BackupStoreHarness.Configuration("central", BackupStorageExecutionLocation.Backend, local.Id);
        var job = new BackupJob(Guid.NewGuid(), agent.TenantId, "Mismatch", agent.AgentId, configuration, ConnectionWorld.Now, null, BackupOverlapPolicy.Queue);

        var rejection = await Assert.ThrowsAsync<InvalidBackupJobException>(() => _world.Quota.RunStore().CreateJobAsync(job, default));

        Assert.Equal("configuration.destinations[0].storageConnectionId", rejection.Field);
    }

    [Fact]
    public async Task UpdatingAJobToTargetAnInvalidConnectionIsRejectedAndLeavesTheJobUntouched()
    {
        var agent = await _world.Quota.AgentAsync();
        var job = await _world.Quota.JobAsync(agent);
        var stranger = await _world.TenantAsync();
        var (_, foreignCentral) = await _world.DefaultsAsync(stranger);

        var rejection = await Assert.ThrowsAsync<InvalidBackupJobException>(() => _world.Quota.RunStore().UpdateJobAsync(agent.TenantId, job.Id,
            new BackupJobUpdate(job.Name, job.AgentId, BackupStoreHarness.Configuration("central", BackupStorageExecutionLocation.Backend, foreignCentral.Id), null, null), default));

        Assert.Equal("configuration.destinations[0].storageConnectionId", rejection.Field);
        Assert.Null((await _world.Quota.RunStore().GetJobAsync(agent.TenantId, job.Id, default))!.Configuration.Destinations.Single(d => d.IsPrimary).StorageConnectionId);
    }

    [Fact]
    public async Task AnUnknownConnectionIdAtRunStartReportsStorageConnectionUnavailable()
    {
        var agent = await _world.Quota.AgentAsync();
        var job = await _world.Quota.JobAsync(agent);
        var target = await _world.AddAsync(agent.TenantId, "Vanishing", "central", StorageOwnership.PlatformManaged);
        var targeted = await _world.Quota.RunStore().UpdateJobAsync(agent.TenantId, job.Id,
            new BackupJobUpdate(job.Name, job.AgentId, BackupStoreHarness.Configuration("central", BackupStorageExecutionLocation.Backend, target.Id), null, null), default);
        Assert.Equal(target.Id, targeted!.Configuration.Destinations.Single(d => d.IsPrimary).StorageConnectionId);
        // Simulate a stale imported job reference without bypassing the store's in-use deletion guard.
        await ConnectionWorld.ExecuteAsync(fixture.ConnectionString,
            """UPDATE backup_jobs SET "Configuration" = jsonb_set("Configuration", '{Destinations,0,StorageConnectionId}', to_jsonb(@id::text)) WHERE "Id" = @job""",
            ("id", Guid.NewGuid().ToString("D")), ("job", job.Id));

        var started = await _world.Quota.RunStore().StartRunAsync(agent.TenantId, job.Id, "run-ghost", ConnectionWorld.Now, default);

        Assert.Equal(BackupStartOutcome.AdmissionDenied, started.Outcome);
        Assert.Equal(BackupAdmissionDenialCodes.StorageConnectionUnavailable, started.DenialCode);
    }

    [Fact]
    public async Task ARunSnapshotsTheResolvedConnectionIdAndItsReplicasInheritIt()
    {
        var agent = await _world.Quota.AgentAsync(limitBytes: null);
        var job = await _world.Quota.JobAsync(agent);
        var (_, central) = await _world.DefaultsAsync(agent.TenantId);
        var started = await _world.Quota.RunStore().StartRunAsync(agent.TenantId, job.Id, "run-snapshot", ConnectionWorld.Now, default);

        Assert.Equal(BackupStartOutcome.Started, started.Outcome);
        Assert.Equal(central.Id, started.Run!.Configuration.Destinations.Single(d => d.IsPrimary).StorageConnectionId);

        var claim = await BackupStoreHarness.ClaimAsync(_world.Quota.RunStore(), agent, ConnectionWorld.Now);
        await BackupStoreHarness.StartAttemptAsync(_world.Quota.RunStore(), claim!, ConnectionWorld.Now);
        await BackupStoreHarness.CompleteAsync(_world.Quota.RunStore(), claim!, QuotaWorld.Success(100, [ReplicaSpec.Central], ConnectionWorld.Now), ConnectionWorld.Now);

        var replicas = await _world.ReplicasAsync(await StoredArtifactIdAsync(agent.TenantId));
        Assert.Equal(central.Id, Assert.Single(replicas).StorageConnectionId);
    }

    private async Task<Guid> StoredArtifactIdAsync(TenantId tenantId)
    {
        await using var context = fixture.CreatePlatformContext();
        return await context.BackupArtifacts.AsNoTracking().Where(row => row.TenantId == tenantId).Select(row => row.Id).SingleAsync();
    }
}
