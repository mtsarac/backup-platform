using System.Net;
using System.Text.Json;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.IntegrationTests.Notifications;
using BackupPlatform.UnitTests.Notifications.Support;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.BackupStoreHarness;

namespace BackupPlatform.IntegrationTests;

public sealed class BackupSizeAnomalyIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture _database = new();
    private NotificationWorld _world = null!;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _world = new NotificationWorld(_database);
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(1050, 0.05, false)]
    [InlineData(1300, 0.30, false)]
    [InlineData(500, -0.50, true)]
    [InlineData(3000, 2.0, true)]
    [InlineData(4000, 3.0, true)]
    public async Task Successful_completion_exposes_advisory_metadata_and_atomically_enqueues_one_notification(long bytes, double deviation, bool anomalous)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(_database, clock: _world.Clock);
        var tenant = scenario.Setup.Tenant.Id;
        var job = await scenario.CreateJobAsync();
        await _world.AddEmailAsync(tenant);
        await _world.EnableAsync(tenant, [NotificationEventTypes.BackupSizeAnomaly]);
        for (var index = 0; index < 3; index++)
        {
            var baseline = await CompleteHttpAsync(scenario, job, 1000);
            var pending = await scenario.RunAsync(baseline.Claim.RunId);
            Assert.Equal("BaselinePending", pending.GetProperty("sizeAnomalyStatus").GetString());
            Assert.False(pending.GetProperty("isSizeAnomaly").GetBoolean());
            Assert.Equal(JsonValueKind.Null, pending.GetProperty("sizeAnomalyBaselineBytes").ValueKind);
            Assert.Empty(await _world.EventsAsync(tenant));
        }

        var completed = await CompleteHttpAsync(scenario, job, bytes);
        var run = await scenario.RunAsync(completed.Claim.RunId);
        Assert.Equal("Succeeded", run.GetProperty("status").GetString());
        Assert.Equal(anomalous ? "SizeAnomalyWarning" : "Normal", run.GetProperty("sizeAnomalyStatus").GetString());
        Assert.Equal(anomalous, run.GetProperty("isSizeAnomaly").GetBoolean());
        Assert.Equal(1000, run.GetProperty("sizeAnomalyBaselineBytes").GetInt64());
        Assert.Equal(deviation, run.GetProperty("sizeAnomalyDeviationRatio").GetDouble(), 8);
        var attempt = Assert.Single(run.GetProperty("attempts").EnumerateArray());
        Assert.Equal("Succeeded", attempt.GetProperty("status").GetString());
        var artifact = Assert.Single(attempt.GetProperty("artifacts").EnumerateArray());
        Assert.Equal(bytes, artifact.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(new string('b', 64), artifact.GetProperty("artifactChecksum").GetString());
        Assert.Equal("Verified", artifact.GetProperty("replicas")[0].GetProperty("verificationState").GetString());

        // No notification sweep has run: completion and its outbox must already be durable together.
        var events = await _world.EventsAsync(tenant);
        Assert.Equal(anomalous ? 1 : 0, events.Count);
        if (anomalous)
        {
            var notification = Assert.Single(events);
            Assert.Equal(NotificationSeverity.Warning, notification.Severity);
            Assert.Equal($"backup_size_anomaly:{completed.Claim.RunId:N}", notification.DedupKey);
            Assert.Equal("1000", notification.Facts["baselineBytes"]);
            Assert.Equal(bytes.ToString(System.Globalization.CultureInfo.InvariantCulture), notification.Facts["sizeBytes"]);
            Assert.Single(await _world.DeliveriesAsync(tenant));
        }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var replay = await scenario.CompleteRawAsync(completed.Claim, completed.Request);
            Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        }));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _world.Engine().SweepAsync(default)));
        Assert.Equal(anomalous ? 1 : 0, (await _world.EventsAsync(tenant)).Count);

        using var history = await scenario.Admin.GetAsync(scenario.Root + "/backup-runs?page=1");
        var listed = AgentWorld.ReadJson(history).GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == completed.Claim.RunId);
        Assert.Equal(run.GetProperty("sizeAnomalyStatus").GetString(), listed.GetProperty("sizeAnomalyStatus").GetString());
        Assert.Equal(deviation, listed.GetProperty("sizeAnomalyDeviationRatio").GetDouble(), 8);
        var other = await scenario.World.CreateTenantAsync();
        using var hidden = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + $"/backup-runs/{completed.Claim.RunId}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var otherHistory = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + "/backup-runs?page=1");
        Assert.Equal(0, AgentWorld.ReadJson(otherHistory).GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task Baseline_uses_only_the_last_ten_successes_of_the_same_job_and_tenant()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(_database, clock: _world.Clock);
        var job = await scenario.CreateJobAsync();
        for (var index = 0; index < 2; index++) await CompleteHttpAsync(scenario, job, 100_000);
        for (var index = 0; index < 10; index++) await CompleteHttpAsync(scenario, job, 1000);
        for (var index = 0; index < 3; index++)
        {
            await scenario.StartRunAsync(job);
            await scenario.FailNextAsync(BackupFailureCodes.InvalidConfiguration);
            scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        var differentJob = await scenario.CreateJobAsync();
        for (var index = 0; index < 3; index++) await CompleteHttpAsync(scenario, differentJob, 100_000);
        var last = await CompleteHttpAsync(scenario, job, 1050);
        var run = await scenario.RunAsync(last.Claim.RunId);
        Assert.Equal("Normal", run.GetProperty("sizeAnomalyStatus").GetString());
        Assert.Equal(1000, run.GetProperty("sizeAnomalyBaselineBytes").GetInt64());
        Assert.Equal(0.05, run.GetProperty("sizeAnomalyDeviationRatio").GetDouble(), 8);
        var freshJob = await scenario.CreateJobAsync();
        var fresh = await CompleteHttpAsync(scenario, freshJob, 100_000);
        Assert.Equal("BaselinePending", (await scenario.RunAsync(fresh.Claim.RunId)).GetProperty("sizeAnomalyStatus").GetString());
    }

    [Fact]
    public async Task Anomaly_and_outbox_roll_back_together_and_completion_retry_recovers_without_duplicates()
    {
        var kit = _world.Quota.Harness;
        var agent = await kit.AddAgentAsync();
        var job = await kit.AddJobAsync(agent);
        await _world.AddEmailAsync(agent.TenantId);
        await _world.EnableAsync(agent.TenantId, [NotificationEventTypes.BackupSizeAnomaly]);
        for (var index = 0; index < 3; index++) await CompleteStoreAsync(agent, job, 1000);
        var store = kit.Store();
        var now = _world.Clock.GetUtcNow();
        await kit.StartRunAsync(store, job, "atomic-warning", now);
        var claim = (await ClaimAsync(store, agent, now))!;
        await StartAttemptAsync(store, claim, now);
        var request = SizedSuccess(now, 500);
        await using (var context = _database.CreatePlatformContext())
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE notification_deliveries ADD CONSTRAINT test_reject_anomaly_delivery CHECK (false) NOT VALID");
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => store.CompleteAttemptAsync(Command(claim, now), request, default));
            var unchanged = (await kit.Store().GetRunAsync(agent.TenantId, claim.Run.Id, default))!;
            Assert.Equal(BackupStatus.Running, unchanged.Status);
            Assert.Null(unchanged.SizeAnomalyStatus);
            Assert.Empty(unchanged.CurrentAttempt.Artifacts);
            Assert.Empty(await _world.EventsAsync(agent.TenantId));
        }
        finally
        {
            await using var context = _database.CreatePlatformContext();
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE notification_deliveries DROP CONSTRAINT test_reject_anomaly_delivery");
        }
        await CompleteAsync(kit.Store(), claim, request, now);
        await CompleteAsync(kit.Store(), claim, request, now);
        Assert.Single(await _world.EventsAsync(agent.TenantId));
        Assert.Single(await _world.DeliveriesAsync(agent.TenantId));
        await using var audit = _database.CreatePlatformContext();
        Assert.Single(await audit.AuditEvents.Where(entry => entry.TenantId == agent.TenantId && entry.Action == AuditActions.BackupRunSizeAnomaly).ToListAsync());
    }

    [Fact]
    public async Task Anomaly_reaches_configured_email_and_webhook_once_without_changing_backup_success()
    {
        await using var smtp = new TestSmtpServer();
        await using var hook = new TestHttpsServer();
        var kit = _world.Quota.Harness;
        var agent = await kit.AddAgentAsync();
        var job = await kit.AddJobAsync(agent);
        await _world.AddEmailAsync(agent.TenantId);
        await _world.ConfigureSmtpAsync(smtp.Port);
        await _world.AddWebhookAsync(agent.TenantId, hook.Url());
        await _world.EnableAsync(agent.TenantId, [NotificationEventTypes.BackupSizeAnomaly]);
        for (var index = 0; index < 3; index++) await CompleteStoreAsync(agent, job, 1000);
        var run = await CompleteStoreAsync(agent, job, 500);
        Assert.Equal(2, (await _world.DeliveriesAsync(agent.TenantId)).Count);
        Assert.Equal(2, await _world.DeliverAsync());
        Assert.Contains("Backup size anomaly", Assert.Single(smtp.Messages));
        var webhook = JsonDocument.Parse(Assert.Single(hook.Requests).Body).RootElement;
        Assert.Equal("backup_size_anomaly", webhook.GetProperty("type").GetString());
        Assert.Equal("warning", webhook.GetProperty("severity").GetString());
        Assert.Equal(0, await _world.Engine().SweepAsync(default));
        Assert.Equal(0, await _world.DeliverAsync());
        Assert.Equal(BackupStatus.Succeeded, (await kit.Store().GetRunAsync(agent.TenantId, run.Id, default))!.Status);
        Assert.All(await _world.DeliveriesAsync(agent.TenantId), delivery => Assert.Equal(NotificationDeliveryStatus.Sent, delivery.Status));
    }

    [Fact]
    public async Task A_large_percentage_change_inside_normal_historical_variance_is_not_an_anomaly()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(_database, clock: _world.Clock);
        var job = await scenario.CreateJobAsync();
        await _world.AddEmailAsync(scenario.Setup.Tenant.Id);
        await _world.EnableAsync(scenario.Setup.Tenant.Id, [NotificationEventTypes.BackupSizeAnomaly]);
        foreach (var bytes in new long[] { 1000, 1000, 4000 }) await CompleteHttpAsync(scenario, job, bytes);
        var completed = await CompleteHttpAsync(scenario, job, 3000);
        var run = await scenario.RunAsync(completed.Claim.RunId);
        Assert.Equal("Normal", run.GetProperty("sizeAnomalyStatus").GetString());
        Assert.Equal(2000, run.GetProperty("sizeAnomalyBaselineBytes").GetInt64());
        Assert.Equal(0.5, run.GetProperty("sizeAnomalyDeviationRatio").GetDouble(), 8);
        Assert.Empty(await _world.EventsAsync(scenario.Setup.Tenant.Id));
    }

    [Fact]
    public async Task Policy_filtered_warning_is_still_persisted_and_observation_source_can_later_announce_it_once()
    {
        var kit = _world.Quota.Harness;
        var agent = await kit.AddAgentAsync();
        var job = await kit.AddJobAsync(agent);
        await _world.AddEmailAsync(agent.TenantId);
        await _world.EnableAsync(agent.TenantId, [NotificationEventTypes.BackupSizeAnomaly], severity: "critical");
        for (var index = 0; index < 3; index++) await CompleteStoreAsync(agent, job, 1000);
        var run = await CompleteStoreAsync(agent, job, 500);
        Assert.True((await kit.Store().GetRunAsync(agent.TenantId, run.Id, default))!.IsSizeAnomaly);
        Assert.Empty(await _world.EventsAsync(agent.TenantId));
        await _world.EnableAsync(agent.TenantId, [NotificationEventTypes.BackupSizeAnomaly]);
        Assert.Equal(1, await _world.Engine().SweepAsync(default));
        Assert.Equal(0, await _world.Engine().SweepAsync(default));
        Assert.Single(await _world.EventsAsync(agent.TenantId));
    }

    private async Task<BackupRun> CompleteStoreAsync(StoreAgent agent, BackupJob job, long bytes)
    {
        var kit = _world.Quota.Harness;
        var store = kit.Store();
        var now = _world.Clock.GetUtcNow();
        var run = await kit.StartRunAsync(store, job, Guid.NewGuid().ToString("N"), now);
        var claim = (await ClaimAsync(store, agent, now))!;
        await StartAttemptAsync(store, claim, now);
        await CompleteAsync(store, claim, SizedSuccess(now, bytes), now);
        _world.Clock.Advance(TimeSpan.FromSeconds(1));
        return run;
    }

    private static BackupPlatform.Application.Backups.BackupCompletion SizedSuccess(DateTimeOffset now, long bytes)
    {
        var request = Success(now);
        return request with { Artifact = request.Artifact! with { SizeBytes = bytes, Replicas = [request.Artifact.Replicas[0] with { SizeBytes = bytes }] } };
    }

    private static async Task<(TaskClaim Claim, BackupTaskCompletionRequest Request)> CompleteHttpAsync(BackupApiScenario scenario, Guid job, long bytes)
    {
        await scenario.StartRunAsync(job);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);
        var request = scenario.SuccessRequest(claim);
        request = request with { Artifact = request.Artifact! with { SizeBytes = bytes, Replicas = [request.Artifact.Replicas[0] with { SizeBytes = bytes }] } };
        using var response = await scenario.CompleteRawAsync(claim, request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        return (claim, request);
    }
}
