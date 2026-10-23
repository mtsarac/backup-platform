using System.Net;
using System.Text.Json;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Storage;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.IntegrationTests.Notifications;
using BackupPlatform.UnitTests.Notifications.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task Anomaly_evaluation_fault_is_logged_and_the_run_still_completes()
    {
        var kit = _world.Quota.Harness;
        var agent = await kit.AddAgentAsync();
        var job = await kit.AddJobAsync(agent);
        await _world.AddEmailAsync(agent.TenantId);
        await _world.EnableAsync(agent.TenantId, [NotificationEventTypes.BackupSizeAnomaly]);
        for (var index = 0; index < 3; index++) await CompleteStoreAsync(agent, job, 1000);
        var log = new CapturedLogger();
        var store = new BackupPlatform.Infrastructure.Backups.PostgreSqlBackupRunStore(
            new DbContextOptionsBuilder<BackupPlatform.Infrastructure.Persistence.PlatformDbContext>().UseNpgsql(_database.ConnectionString).Options,
            AdmitAllBackupAdmission.Instance, null, () => 0, null, log);
        var now = _world.Clock.GetUtcNow();
        var run = await kit.StartRunAsync(store, job, "anomaly-fault", now);
        var claim = (await ClaimAsync(store, agent, now))!;
        await StartAttemptAsync(store, claim, now);
        // The notification policy read fails inside the anomaly block and aborts the Postgres transaction unless the block is isolated.
        await using (var context = _database.CreatePlatformContext())
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE notification_policies RENAME TO notification_policies_fault");
        try { await CompleteAsync(store, claim, SizedSuccess(now, 500), now); }
        finally
        {
            await using var context = _database.CreatePlatformContext();
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE notification_policies_fault RENAME TO notification_policies");
        }
        var completed = (await kit.Store().GetRunAsync(agent.TenantId, run.Id, default))!;
        Assert.Equal(BackupStatus.Succeeded, completed.Status);
        Assert.Single(completed.CurrentAttempt.Artifacts);
        Assert.Null(completed.SizeAnomalyStatus);
        Assert.Empty(await _world.EventsAsync(agent.TenantId));
        await using var audit = _database.CreatePlatformContext();
        Assert.Empty(await audit.AuditEvents.Where(entry => entry.TenantId == agent.TenantId && entry.Action == AuditActions.BackupRunSizeAnomaly).ToListAsync());
        Assert.Contains(log.Errors, error => error.Contains(run.Id.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CapturedLogger : Microsoft.Extensions.Logging.ILogger<BackupPlatform.Infrastructure.Backups.PostgreSqlBackupRunStore>
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error) Errors.Add(formatter(state, exception));
        }
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
        using var payload = JsonDocument.Parse(Assert.Single(hook.Requests).Body);
        var webhook = payload.RootElement;
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

    [Fact]
    public async Task Capacity_and_size_warnings_flow_through_agent_http_to_health_summary_and_real_notifications()
    {
        await using var smtp = new TestSmtpServer();
        await using var hook = new TestHttpsServer();
        await using var scenario = await BackupApiScenario.CreateAsync(_database, clock: _world.Clock);
        await scenario.World.Factory.Services.GetRequiredService<BackupPlatform.Api.Storage.StorageCapacityPeriodicProbeWorker>().StopAsync(default);
        var tenant = scenario.Setup.Tenant.Id;
        var job = await scenario.CreateJobAsync();
        using (var schedule = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{job}/schedule",
            new { cronExpression = "0 0 * * *", timeZone = "UTC" }))
            Assert.Equal(HttpStatusCode.OK, schedule.StatusCode);
        await _world.AddEmailAsync(tenant);
        await _world.ConfigureSmtpAsync(smtp.Port);
        await _world.AddWebhookAsync(tenant, hook.Url());
        await _world.EnableAsync(tenant, [NotificationEventTypes.StorageCapacityCritical, NotificationEventTypes.BackupSizeAnomaly]);
        var baselineRuns = new List<JsonElement>();
        for (var index = 0; index < 3; index++)
        {
            var baseline = await CompleteHttpAsync(scenario, job, 1000);
            baselineRuns.Add(await scenario.RunAsync(baseline.Claim.RunId));
        }
        Guid connection;
        await using (var db = _database.CreatePlatformContext())
            connection = await db.StorageConnections.Where(row => row.TenantId == tenant && row.IsDefault && row.ProviderType == "local").Select(row => row.Id).SingleAsync();
        AgentCapabilityReport[] capabilities =
        [
            new() { Identifier = BackupTaskProtocol.MultiDestinationCapability, Support = "Supported" },
            new() { Identifier = BackupTaskProtocol.UploadBandwidthLimitCapability, Support = "Supported" },
            new() { Identifier = CapacityProbeProtocol.Capability, Support = "Supported" }
        ];
        using (var sync = await scenario.World.SendAgentAsync(HttpMethod.Put, "/api/agent/capabilities", scenario.Agent.CredentialId, scenario.Agent.Secret,
            body: new AgentCapabilitySyncRequest { Revision = AgentCapabilityRevision.Compute(capabilities), Capabilities = capabilities }))
            Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        using (var heartbeat = await scenario.World.HeartbeatAsync(scenario.Agent, AgentWorld.Heartbeat(capabilityRevision: AgentCapabilityRevision.Compute(capabilities))))
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        await MeasureCapacityHttpAsync(scenario, connection, 20);
        using var healthyResponse = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{job}/health");
        var healthy = AgentWorld.ReadJson(healthyResponse);
        Assert.Equal(10, healthy.GetProperty("factors").GetProperty("storageCapacity").GetProperty("score").GetInt32());
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        var critical = await MeasureCapacityHttpAsync(scenario, connection, 92);
        using var degradedResponse = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{job}/health");
        var degraded = AgentWorld.ReadJson(degradedResponse);
        Assert.Equal(healthy.GetProperty("score").GetInt32() - 10, degraded.GetProperty("score").GetInt32());
        Assert.Equal("capacity_critical", degraded.GetProperty("factors").GetProperty("storageCapacity").GetProperty("reason").GetString());
        Assert.Equal(NotificationEventTypes.StorageCapacityCritical, Assert.Single(await _world.EventsAsync(tenant)).Type);

        var completed = await CompleteHttpAsync(scenario, job, 600);
        var run = await scenario.RunAsync(completed.Claim.RunId);
        Assert.Equal("Succeeded", run.GetProperty("status").GetString());
        Assert.Equal("SizeAnomalyWarning", run.GetProperty("sizeAnomalyStatus").GetString());
        Assert.True(run.GetProperty("isSizeAnomaly").GetBoolean());
        var events = await _world.EventsAsync(tenant);
        Assert.Equal(2, events.Count);
        Assert.Single(events, notification => notification.Type == NotificationEventTypes.BackupSizeAnomaly &&
            notification.DedupKey == $"backup_size_anomaly:{completed.Claim.RunId:N}");
        Assert.Equal(4, (await _world.DeliveriesAsync(tenant)).Count);
        await using (var db = _database.CreatePlatformContext())
        {
            var stored = await db.BackupRuns.AsNoTracking().SingleAsync(row => row.Id == completed.Claim.RunId);
            Assert.Equal(BackupStatus.Succeeded, stored.Status);
            Assert.Equal(1000, stored.SizeAnomalyBaselineBytes);
            Assert.Equal(-0.4, stored.SizeAnomalyDeviationRatio!.Value, 8);
            Assert.Single(await db.AuditEvents.Where(entry => entry.TenantId == tenant && entry.Action == AuditActions.BackupRunSizeAnomaly).ToListAsync());
        }
        using var summaryResponse = await scenario.Admin.GetAsync($"/api/v1/observability/health-summary?tenantId={tenant.Value}");
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = AgentWorld.ReadJson(summaryResponse);
        Assert.Equal(1, summary.GetProperty("totalJobs").GetInt32());
        Assert.Equal(1, summary.GetProperty("healthy").GetInt32() + summary.GetProperty("degraded").GetInt32() + summary.GetProperty("critical").GetInt32());
        var summaryJob = Assert.Single(summary.GetProperty("jobs").EnumerateArray());
        Assert.Equal(job, summaryJob.GetProperty("jobId").GetGuid());
        Assert.Equal(degraded.GetProperty("score").GetInt32(), summaryJob.GetProperty("score").GetInt32());
        var risk = Assert.Single(summary.GetProperty("capacityRisks").EnumerateArray());
        Assert.Equal(connection, risk.GetProperty("connectionId").GetGuid());
        Assert.Equal("Critical", risk.GetProperty("status").GetString());
        Assert.Equal(92, risk.GetProperty("usedBytes").GetInt64());
        Assert.Equal(1, summary.GetProperty("recentSizeAnomaliesTotal").GetInt32());
        var anomaly = Assert.Single(summary.GetProperty("recentSizeAnomalies").EnumerateArray());
        Assert.Equal(completed.Claim.RunId, anomaly.GetProperty("runId").GetGuid());
        Assert.Equal(job, anomaly.GetProperty("jobId").GetGuid());
        Assert.Equal("Nightly full backup", anomaly.GetProperty("jobName").GetString());
        Assert.Equal(run.GetProperty("completedAt").GetDateTimeOffset(), anomaly.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(600, anomaly.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(1000, anomaly.GetProperty("baselineMeanBytes").GetInt64());
        Assert.Equal(-0.4, anomaly.GetProperty("deviationRatio").GetDouble(), 8);
        var other = await scenario.World.CreateTenantAsync();
        using (var isolated = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + "/observability/health-summary"))
        {
            var empty = AgentWorld.ReadJson(isolated);
            Assert.Empty(empty.GetProperty("jobs").EnumerateArray());
            Assert.Empty(empty.GetProperty("capacityRisks").EnumerateArray());
            Assert.Empty(empty.GetProperty("recentSizeAnomalies").EnumerateArray());
            Assert.Equal(0, empty.GetProperty("recentSizeAnomaliesTotal").GetInt32());
        }
        Assert.Empty(await _world.EventsAsync(other.Tenant.Id));
        Assert.Empty(await _world.DeliveriesAsync(other.Tenant.Id));
        using (var hidden = await other.Admin.GetAsync($"/api/v1/observability/health-summary?tenantId={tenant.Value}"))
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using (var replay = await scenario.CompleteRawAsync(completed.Claim, completed.Request))
            Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        using (var replay = await scenario.World.SendAgentAsync(HttpMethod.Post, $"/api/agent/capacity-probes/{critical.TaskId}/complete",
            scenario.Agent.CredentialId, scenario.Agent.Secret, body: new { leaseToken = critical.LeaseToken, result = critical.Result }))
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await _world.Engine().SweepAsync(default);
        Assert.Equal(2, (await _world.EventsAsync(tenant)).Count);
        Assert.Equal(4, await _world.DeliverAsync());
        Assert.Equal(2, smtp.Messages.Count);
        Assert.Contains(smtp.Messages, message => message.Contains("Backup size anomaly", StringComparison.Ordinal));
        Assert.Equal(new[] { "backup_size_anomaly", "storage_capacity_critical" }, hook.Requests.Select(request =>
        {
            using var payload = JsonDocument.Parse(request.Body);
            return payload.RootElement.GetProperty("type").GetString();
        }).OrderBy(type => type).ToArray());
        Assert.All(await _world.DeliveriesAsync(tenant), delivery => Assert.Equal(NotificationDeliveryStatus.Sent, delivery.Status));
        Assert.Equal(0, await _world.DeliverAsync());
        foreach (var baseline in baselineRuns)
        {
            var retained = await scenario.RunAsync(baseline.GetProperty("id").GetGuid());
            Assert.Equal(baseline.GetRawText(), retained.GetRawText());
        }
    }

    [Fact]
    public async Task Summary_returns_deterministic_latest_twenty_warnings_with_truthful_total_and_inclusive_seven_day_window()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(_database, clock: _world.Clock);
        var job = await scenario.CreateJobAsync();
        for (var index = 0; index < 3; index++) await CompleteHttpAsync(scenario, job, 1000);
        var completedAt = scenario.Clock.GetUtcNow();
        var warningIds = new List<Guid>();
        long bytes = 1000;
        for (var index = 0; index < 22; index++)
        {
            bytes *= 2;
            var completed = await CompleteHttpAsync(scenario, job, bytes);
            Assert.Equal("SizeAnomalyWarning", (await scenario.RunAsync(completed.Claim.RunId)).GetProperty("sizeAnomalyStatus").GetString());
            warningIds.Add(completed.Claim.RunId);
            scenario.Clock.Advance(TimeSpan.FromSeconds(-1));
        }
        using (var response = await scenario.Admin.GetAsync(scenario.Root + "/observability/health-summary"))
        {
            var summary = AgentWorld.ReadJson(response);
            Assert.Equal(22, summary.GetProperty("recentSizeAnomaliesTotal").GetInt32());
            var warnings = summary.GetProperty("recentSizeAnomalies").EnumerateArray().ToArray();
            Assert.Equal(warningIds.OrderByDescending(id => id).Take(20), warnings.Select(warning => warning.GetProperty("runId").GetGuid()));
            Assert.All(warnings, warning => Assert.Equal(completedAt, warning.GetProperty("completedAt").GetDateTimeOffset()));
        }
        scenario.Clock.Advance(TimeSpan.FromSeconds(-1));
        using (var future = await scenario.Admin.GetAsync(scenario.Root + "/observability/health-summary"))
            Assert.Equal(0, AgentWorld.ReadJson(future).GetProperty("recentSizeAnomaliesTotal").GetInt32());
        scenario.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        var renewed = await scenario.World.SignInAsync(scenario.Setup.Account);
        using (var boundary = await renewed.GetAsync(scenario.Root + "/observability/health-summary"))
            Assert.Equal(22, AgentWorld.ReadJson(boundary).GetProperty("recentSizeAnomaliesTotal").GetInt32());
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        using var expired = await renewed.GetAsync(scenario.Root + "/observability/health-summary");
        var empty = AgentWorld.ReadJson(expired);
        Assert.Equal(0, empty.GetProperty("recentSizeAnomaliesTotal").GetInt32());
        Assert.Empty(empty.GetProperty("recentSizeAnomalies").EnumerateArray());
    }

    [Fact]
    public async Task Observability_upgrade_preserves_existing_successes_artifacts_replicas_and_older_agent_completion()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(_database, clock: _world.Clock);
        await scenario.World.Factory.Services.GetRequiredService<BackupPlatform.Api.Storage.StorageCapacityPeriodicProbeWorker>().StopAsync(default);
        var job = await scenario.CreateJobAsync();
        var completed = await CompleteHttpAsync(scenario, job, 1000);
        var before = await scenario.RunAsync(completed.Claim.RunId);
        await using (var db = _database.CreatePlatformContext())
        {
            var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            var anomaly = Array.IndexOf(migrations, "20261021002856_BackupSizeAnomalyDetection");
            Assert.True(anomaly > 0);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[anomaly - 1]);
            await migrator.MigrateAsync();
        }
        var legacy = await scenario.RunAsync(completed.Claim.RunId);
        Assert.Equal("Succeeded", legacy.GetProperty("status").GetString());
        Assert.Equal(before.GetProperty("completedAt").GetDateTimeOffset(), legacy.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(before.GetProperty("attempts").GetRawText(), legacy.GetProperty("attempts").GetRawText());
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("sizeAnomalyStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("sizeAnomalyBaselineBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("sizeAnomalyDeviationRatio").ValueKind);
        Assert.False(legacy.GetProperty("isSizeAnomaly").GetBoolean());
        using (var summary = await scenario.Admin.GetAsync(scenario.Root + "/observability/health-summary"))
            Assert.Empty(AgentWorld.ReadJson(summary).GetProperty("recentSizeAnomalies").EnumerateArray());
        // The existing helper starts protocol 1.0/software 1.2.3 without the optional capacity feature.
        var next = await CompleteHttpAsync(scenario, job, 1000);
        Assert.Equal("Succeeded", (await scenario.RunAsync(next.Claim.RunId)).GetProperty("status").GetString());
        Assert.Equal(before.GetProperty("attempts").GetRawText(), (await scenario.RunAsync(completed.Claim.RunId)).GetProperty("attempts").GetRawText());
    }

    private static async Task<(Guid TaskId, Guid LeaseToken, CapacityProbeTaskResult Result)> MeasureCapacityHttpAsync(BackupApiScenario scenario, Guid connection, long used)
    {
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        using var probe = await scenario.Admin.SendAsync(HttpMethod.Post, $"/api/v1/storage/connections/{connection}/probe");
        Assert.Equal(HttpStatusCode.Accepted, probe.StatusCode);
        var taskId = AgentWorld.ReadJson(probe).GetProperty("taskId").GetGuid();
        using var poll = await scenario.World.PollAsync(scenario.Agent);
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        var task = Assert.Single(AgentWorld.ReadJson(poll).GetProperty("tasks").EnumerateArray());
        Assert.Equal("capacity-probe", task.GetProperty("type").GetString());
        var capacity = task.GetProperty("capacityProbe");
        Assert.Equal(taskId, capacity.GetProperty("taskId").GetGuid());
        Assert.Equal(connection, capacity.GetProperty("connectionId").GetGuid());
        var token = capacity.GetProperty("leaseToken").GetGuid();
        var result = CapacityProbeTaskResult.Success(taskId, connection, 100, used, 100 - used);
        using var complete = await scenario.World.SendAgentAsync(HttpMethod.Post, $"/api/agent/capacity-probes/{taskId}/complete",
            scenario.Agent.CredentialId, scenario.Agent.Secret, body: new { leaseToken = token, result });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using var measurement = await scenario.Admin.GetAsync($"/api/v1/storage/connections/{connection}/capacity");
        Assert.Equal(HttpStatusCode.OK, measurement.StatusCode);
        Assert.Equal(used, AgentWorld.ReadJson(measurement).GetProperty("usedBytes").GetInt64());
        return (taskId, token, result);
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
        return request with { Artifact = request.Artifact! with { SizeBytes = bytes, Replicas = [request.Artifact!.Replicas[0] with { SizeBytes = bytes }] } };
    }

    private static async Task<(TaskClaim Claim, BackupTaskCompletionRequest Request)> CompleteHttpAsync(BackupApiScenario scenario, Guid job, long bytes)
    {
        await scenario.StartRunAsync(job);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);
        var request = scenario.SuccessRequest(claim);
        request = request with { Artifact = request.Artifact! with { SizeBytes = bytes, Replicas = [request.Artifact!.Replicas[0] with { SizeBytes = bytes }] } };
        using var response = await scenario.CompleteRawAsync(claim, request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        return (claim, request);
    }
}
