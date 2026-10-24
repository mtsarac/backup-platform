using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Contracts.Storage;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

public sealed class BackupHealthApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task FreshVerifiedRestoredBackupHasFullHealthThroughBothReadApis()
    {
        await using var scenario = await CreateScenarioAsync();
        var job = await scenario.CreateJobAsync();
        await ConfigureScheduleAsync(scenario, job);
        await scenario.StartRunAsync(job);
        await scenario.SucceedNextAsync();
        await AddEvidenceAsync(scenario, job, restorePassed: true);

        var health = await HealthAsync(scenario, job);
        Assert.Equal(100, health.GetProperty("score").GetInt32());
        Assert.Equal("Healthy", health.GetProperty("state").GetString());
        var factors = health.GetProperty("factors");
        Assert.Equal(40, factors.GetProperty("freshness").GetProperty("score").GetInt32());
        Assert.Equal(30, factors.GetProperty("replication").GetProperty("score").GetInt32());
        Assert.Equal(20, factors.GetProperty("restoreVerification").GetProperty("score").GetInt32());
        Assert.Equal(10, factors.GetProperty("storageCapacity").GetProperty("score").GetInt32());
        using var versioned = await scenario.Admin.GetAsync($"/api/v1/backups/jobs/{job}/health");
        Assert.Equal(HttpStatusCode.OK, versioned.StatusCode);
        Assert.Equal(100, AgentWorld.ReadJson(versioned).GetProperty("score").GetInt32());
        using var summary = await scenario.Admin.GetAsync(scenario.Root + "/observability/health-summary");
        var json = AgentWorld.ReadJson(summary);
        Assert.Equal(1, json.GetProperty("totalJobs").GetInt32());
        Assert.Equal(100, json.GetProperty("averageScore").GetDouble());
        Assert.Equal(1, json.GetProperty("healthy").GetInt32());
        Assert.Equal(job, Assert.Single(json.GetProperty("jobs").EnumerateArray()).GetProperty("jobId").GetGuid());
        using var summaryV1 = await scenario.Admin.GetAsync($"/api/v1/observability/health-summary?tenantId={scenario.Setup.Tenant.Id.Value}");
        Assert.Equal(HttpStatusCode.OK, summaryV1.StatusCode);
        Assert.Equal(100, AgentWorld.ReadJson(summaryV1).GetProperty("averageScore").GetDouble());
    }

    [Fact]
    public async Task FreshnessDecaysLinearlyAndMissingReplicasAndFailedRestoreRemainVisible()
    {
        await using var scenario = await CreateScenarioAsync();
        var job = await scenario.CreateJobAsync();
        await ConfigureScheduleAsync(scenario, job, "0 * * * *");
        await scenario.StartRunAsync(job);
        await scenario.SucceedNextAsync();
        await AddEvidenceAsync(scenario, job, restorePassed: false);
        scenario.Clock.Advance(TimeSpan.FromMinutes(90));
        var stale = await HealthAsync(scenario, job);
        Assert.Equal(20, stale.GetProperty("factors").GetProperty("freshness").GetProperty("score").GetInt32());
        Assert.Equal(0, stale.GetProperty("factors").GetProperty("restoreVerification").GetProperty("score").GetInt32());
        // A changed future replication policy must not borrow old copies or count unverified copies.
        await using (var db = fixture.CreatePlatformContext())
        {
            var stored = await db.BackupJobs.SingleAsync(row => row.Id == job);
            stored.Reconfigure(stored.Configuration with { Destinations = [.. stored.Configuration.Destinations, new("sftp", Guid.NewGuid())], RequiredVerifiedReplicaCount = 2 });
            await db.SaveChangesAsync();
        }
        var degraded = await HealthAsync(scenario, job);
        Assert.Equal(15, degraded.GetProperty("factors").GetProperty("replication").GetProperty("score").GetInt32());
        Assert.Equal("Critical", degraded.GetProperty("state").GetString());
        scenario.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, (await HealthAsync(scenario, job)).GetProperty("factors").GetProperty("freshness").GetProperty("score").GetInt32());
    }

    [Theory]
    [InlineData(79, 10, "Available")]
    [InlineData(80, 5, "Warning")]
    [InlineData(90, 0, "Critical")]
    [InlineData(91, 0, "Critical")]
    public async Task CapacityUsesDocumentedHealthBoundaries(int used, int score, string status)
    {
        await using var scenario = await CreateScenarioAsync();
        var job = await scenario.CreateJobAsync();
        await scenario.StartRunAsync(job);
        await scenario.SucceedNextAsync();
        await AddEvidenceAsync(scenario, job, restorePassed: true, used: used);
        var factor = (await HealthAsync(scenario, job)).GetProperty("factors").GetProperty("storageCapacity");
        Assert.Equal(score, factor.GetProperty("score").GetInt32());
        Assert.Equal(status, factor.GetProperty("status").GetString());
        await using var db = fixture.CreatePlatformContext();
        var connection = await db.StorageConnections.Where(c => c.TenantId == scenario.Setup.Tenant.Id && c.ProviderType == "local" && c.IsDefault).Select(c => c.Id).SingleAsync();
        using var capacity = await scenario.Admin.GetAsync($"/api/v1/storage/connections/{connection}/capacity");
        Assert.Equal(status, AgentWorld.ReadJson(capacity).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("CapacityUnavailable", 100L, 20L, 80L)]
    [InlineData("Available", null, 20L, null)]
    [InlineData("Available", 100L, -1L, 101L)]
    public async Task LatestUnavailableOrUnmeasurableCapacityNeverBorrowsAnOlderHealthyMeasurement(string status, long? total, long? used, long? free)
    {
        await using var scenario = await CreateScenarioAsync();
        var job = await scenario.CreateJobAsync();
        await scenario.StartRunAsync(job);
        await scenario.SucceedNextAsync();
        await AddEvidenceAsync(scenario, job, restorePassed: true);
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        await using (var db = fixture.CreatePlatformContext())
        {
            var connection = await db.StorageConnections.SingleAsync(row => row.TenantId == scenario.Setup.Tenant.Id && row.ProviderType == "local" && row.IsDefault);
            db.StorageCapacitySnapshots.Add(new(Guid.NewGuid(), scenario.Setup.Tenant.Id, connection.Id, total, used, free, status, scenario.Clock.GetUtcNow()));
            await db.SaveChangesAsync();
        }
        var factor = (await HealthAsync(scenario, job)).GetProperty("factors").GetProperty("storageCapacity");
        Assert.Equal(0, factor.GetProperty("score").GetInt32());
        Assert.Equal("capacity_unavailable", factor.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task SummaryCapacityRisksUseLatestSnapshotAndStayInsideTenant()
    {
        await using var scenario = await CreateScenarioAsync();
        var job = await scenario.CreateJobAsync();
        await scenario.StartRunAsync(job);
        await scenario.SucceedNextAsync();
        await AddEvidenceAsync(scenario, job, restorePassed: true, used: 95);
        using (var summary = await scenario.Admin.GetAsync(scenario.Root + "/observability/health-summary"))
        {
            var risk = Assert.Single(AgentWorld.ReadJson(summary).GetProperty("capacityRisks").EnumerateArray());
            Assert.Equal("Critical", risk.GetProperty("status").GetString());
            Assert.Equal(95, risk.GetProperty("usedBytes").GetInt64());
            Assert.DoesNotContain("secret", risk.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        var other = await scenario.World.CreateTenantAsync();
        using var isolated = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + "/observability/health-summary");
        Assert.Empty(AgentWorld.ReadJson(isolated).GetProperty("capacityRisks").EnumerateArray());
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        await using (var db = fixture.CreatePlatformContext())
        {
            var connectionId = await db.StorageConnections.Where(row => row.TenantId == scenario.Setup.Tenant.Id && row.ProviderType == "local" && row.IsDefault).Select(row => row.Id).SingleAsync();
            db.StorageCapacitySnapshots.Add(StorageCapacitySnapshot.UnavailableResult(scenario.Setup.Tenant.Id, connectionId, scenario.Clock.GetUtcNow()));
            await db.SaveChangesAsync();
        }
        using var latest = await scenario.Admin.GetAsync(scenario.Root + "/observability/health-summary");
        Assert.Empty(AgentWorld.ReadJson(latest).GetProperty("capacityRisks").EnumerateArray());
    }

    [Fact]
    public async Task MissingEvidenceIsTruthfulTenantIsolatedAndNeverBlocksManualOrScheduledRuns()
    {
        await using var scenario = await CreateScenarioAsync();
        var job = await scenario.CreateJobAsync();
        var health = await HealthAsync(scenario, job);
        Assert.Equal("Critical", health.GetProperty("state").GetString());
        Assert.Equal("schedule_unavailable", health.GetProperty("factors").GetProperty("freshness").GetProperty("reason").GetString());
        Assert.Equal("capacity_unavailable", health.GetProperty("factors").GetProperty("storageCapacity").GetProperty("reason").GetString());
        var viewer = await scenario.SessionAsync(TenantRole.Viewer);
        using var read = await viewer.GetAsync(scenario.Root + $"/backup-jobs/{job}/health");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var anonymous = await scenario.World.Anonymous.GetAsync($"/api/v1/backups/jobs/{job}/health");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var other = await scenario.World.CreateTenantAsync();
        using var hidden = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + $"/backup-jobs/{job}/health");
        using var hiddenV1 = await other.Admin.GetAsync($"/api/v1/backups/jobs/{job}/health");
        using var deniedSummary = await other.Admin.GetAsync($"/api/v1/observability/health-summary?tenantId={scenario.Setup.Tenant.Id.Value}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenV1.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedSummary.StatusCode);
        using var empty = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + "/observability/health-summary");
        var summary = AgentWorld.ReadJson(empty);
        Assert.Equal(0, summary.GetProperty("totalJobs").GetInt32());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("averageScore").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("state").ValueKind);
        Assert.Empty(summary.GetProperty("jobs").EnumerateArray());
        await scenario.StartRunAsync(job);
        await scenario.SucceedNextAsync();
        var scheduledJob = await scenario.CreateJobAsync();
        using (var schedule = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{scheduledJob}/schedule",
            new { cronExpression = "* * * * *", timeZone = "UTC" }))
            Assert.Equal(HttpStatusCode.OK, schedule.StatusCode);
        Assert.Equal("Critical", (await HealthAsync(scenario, scheduledJob)).GetProperty("state").GetString());
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var heartbeat = await scenario.World.HeartbeatAsync(scenario.Agent)) Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        await using (var scope = scenario.World.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<BackupPlatform.Application.Backups.BackupScheduleService>().EvaluateAsync(scenario.Setup.Tenant.Id, scheduledJob, CancellationToken.None);
        var scheduled = await scenario.SucceedNextAsync();
        Assert.Equal("Succeeded", (await scenario.RunAsync(scheduled.RunId)).GetProperty("status").GetString());
        Assert.Equal("Scheduled", (await scenario.RunAsync(scheduled.RunId)).GetProperty("triggerType").GetString());
    }

    [Fact]
    public async Task HealthClassificationUsesThePublishedEightyPointBoundary()
    {
        await using var scenario = await CreateScenarioAsync();
        var job = await scenario.CreateJobAsync();
        await ConfigureScheduleAsync(scenario, job, "0 * * * *");
        await scenario.StartRunAsync(job);
        await scenario.SucceedNextAsync();
        var fresh = await HealthAsync(scenario, job);
        Assert.Equal(80, fresh.GetProperty("score").GetInt32());
        Assert.Equal("Healthy", fresh.GetProperty("state").GetString());
        scenario.Clock.Advance(TimeSpan.FromMinutes(61.5));
        var late = await HealthAsync(scenario, job);
        Assert.Equal(79, late.GetProperty("score").GetInt32());
        Assert.Equal("Degraded", late.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(30, 20)]
    [InlineData(31, 10)]
    public async Task RestoreVerificationExpiresAfterThirtyDaysAndCapacityCannotStayFresh(int days, int expectedRestoreScore)
    {
        await using var scenario = await CreateScenarioAsync();
        var job = await scenario.CreateJobAsync();
        await scenario.StartRunAsync(job);
        await scenario.SucceedNextAsync();
        await AddEvidenceAsync(scenario, job, restorePassed: true);
        scenario.Clock.Advance(TimeSpan.FromDays(days));
        var renewed = await scenario.World.SignInAsync(scenario.Setup.Account);
        using var response = await renewed.GetAsync(scenario.Root + $"/backup-jobs/{job}/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var factors = AgentWorld.ReadJson(response).GetProperty("factors");
        Assert.Equal(expectedRestoreScore, factors.GetProperty("restoreVerification").GetProperty("score").GetInt32());
        Assert.Equal("capacity_unavailable", factors.GetProperty("storageCapacity").GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("unsupported")]
    [InlineData("expired")]
    public async Task CapacityAndHealthReadsAgreeWhenFreshAgentMeasurementIsNoLongerApplicable(string reason)
    {
        await using var scenario = await CreateScenarioAsync();
        var job = await scenario.CreateJobAsync();
        await scenario.StartRunAsync(job);
        await scenario.SucceedNextAsync();
        await AddEvidenceAsync(scenario, job, restorePassed: true);
        Assert.Equal(10, (await HealthAsync(scenario, job)).GetProperty("factors").GetProperty("storageCapacity").GetProperty("score").GetInt32());
        Guid connection;
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        await using (var db = fixture.CreatePlatformContext())
        {
            connection = await db.StorageConnections.Where(c => c.TenantId == scenario.Setup.Tenant.Id && c.ProviderType == "local" && c.IsDefault).Select(c => c.Id).SingleAsync();
            db.StorageCapacitySnapshots.Add(StorageCapacitySnapshot.AvailableResult(scenario.Setup.Tenant.Id, connection, 100, 95, 5, scenario.Clock.GetUtcNow()));
            await db.SaveChangesAsync();
        }
        using (var summary = await scenario.Admin.GetAsync(scenario.Root + "/observability/health-summary"))
            Assert.Single(AgentWorld.ReadJson(summary).GetProperty("capacityRisks").EnumerateArray());
        if (reason == "offline") scenario.Clock.Advance(TimeSpan.FromSeconds(121));
        else if (reason == "unsupported")
        {
            var unsupported = AgentWorld.CapabilitySnapshot(AgentWorld.CapabilityReport(CapacityProbeProtocol.Capability, "Unsupported"));
            using var sync = await scenario.World.SyncCapabilitiesAsync(scenario.Agent, unsupported);
            Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
            using var heartbeat = await scenario.World.HeartbeatAsync(scenario.Agent, AgentWorld.Heartbeat(capabilityRevision: unsupported.Revision));
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        }
        else
        {
            await using var db = fixture.CreatePlatformContext();
            db.StorageCapacityTasks.Add(new StorageCapacityTask(scenario.Setup.Tenant.Id, connection, scenario.Agent.AgentId, scenario.Clock.GetUtcNow().AddMinutes(-11)));
            await db.SaveChangesAsync();
        }
        using var capacity = await scenario.Admin.GetAsync($"/api/v1/storage/connections/{connection}/capacity");
        Assert.Equal(HttpStatusCode.OK, capacity.StatusCode);
        Assert.Equal("CapacityUnavailable", AgentWorld.ReadJson(capacity).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, AgentWorld.ReadJson(capacity).GetProperty("totalBytes").ValueKind);
        var factor = (await HealthAsync(scenario, job)).GetProperty("factors").GetProperty("storageCapacity");
        Assert.Equal(0, factor.GetProperty("score").GetInt32());
        Assert.Equal("capacity_unavailable", factor.GetProperty("reason").GetString());
        using var unavailableSummary = await scenario.Admin.GetAsync(scenario.Root + "/observability/health-summary");
        Assert.Empty(AgentWorld.ReadJson(unavailableSummary).GetProperty("capacityRisks").EnumerateArray());
        await using var unchanged = fixture.CreatePlatformContext();
        Assert.Equal(2, await unchanged.StorageCapacitySnapshots.CountAsync(s => s.TenantId == scenario.Setup.Tenant.Id && s.ConnectionId == connection));
        if (reason == "expired") Assert.Null((await unchanged.StorageCapacityTasks.SingleAsync(t => t.TenantId == scenario.Setup.Tenant.Id && t.ConnectionId == connection)).CompletedAt);
    }

    [Fact]
    public async Task TenantStateIsCriticalWhenOneJobIsCriticalAmongHealthyJobsAndAverageIsStillReported()
    {
        await using var scenario = await CreateScenarioAsync();
        for (var i = 0; i < 9; i++) await CreateJobWithStateAsync(scenario, "Healthy");
        await CreateJobWithStateAsync(scenario, "Critical");
        var summary = await SummaryAsync(scenario);
        Assert.Equal("Critical", summary.GetProperty("state").GetString());
        Assert.Equal(9, summary.GetProperty("healthy").GetInt32());
        Assert.Equal(1, summary.GetProperty("critical").GetInt32());
        // The old rule classified this average as Healthy; it stays informational.
        Assert.InRange(summary.GetProperty("averageScore").GetDouble(), 80, 99);
    }

    [Fact]
    public async Task TenantStateIsTheWorstJobStateWithCriticalBeatingDegradedBeatingHealthy()
    {
        await using var scenario = await CreateScenarioAsync();
        await CreateJobWithStateAsync(scenario, "Healthy");
        Assert.Equal("Healthy", (await SummaryAsync(scenario)).GetProperty("state").GetString());
        await CreateJobWithStateAsync(scenario, "Degraded");
        Assert.Equal("Degraded", (await SummaryAsync(scenario)).GetProperty("state").GetString());
        await CreateJobWithStateAsync(scenario, "Critical");
        Assert.Equal("Critical", (await SummaryAsync(scenario)).GetProperty("state").GetString());
    }

    private async Task<Guid> CreateJobWithStateAsync(BackupApiScenario scenario, string state)
    {
        var job = await scenario.CreateJobAsync();
        if (state == "Critical") return job;
        // Degraded: an hourly job with a stale backup scoring 79; Healthy: a daily job with full evidence.
        await ConfigureScheduleAsync(scenario, job, state == "Degraded" ? "0 * * * *" : "0 0 * * *");
        await scenario.StartRunAsync(job);
        await scenario.SucceedNextAsync();
        if (state == "Healthy") await AddEvidenceAsync(scenario, job, restorePassed: true);
        else scenario.Clock.Advance(TimeSpan.FromMinutes(61.5));
        Assert.Equal(state, (await HealthAsync(scenario, job)).GetProperty("state").GetString());
        return job;
    }

    private static async Task<JsonElement> SummaryAsync(BackupApiScenario scenario)
    {
        using var response = await scenario.Admin.GetAsync(scenario.Root + "/observability/health-summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private async Task<BackupApiScenario> CreateScenarioAsync()
    {
        var scenario = await BackupApiScenario.CreateAsync(fixture);
        // Health fixtures own measurement timestamps; live capacity transport is tested at its separate consumer seam.
        await scenario.World.Factory.Services.GetRequiredService<BackupPlatform.Api.Storage.StorageCapacityPeriodicProbeWorker>().StopAsync(CancellationToken.None);
        var capabilities = AgentWorld.CapabilitySnapshot(
            AgentWorld.CapabilityReport(BackupTaskProtocol.MultiDestinationCapability),
            AgentWorld.CapabilityReport(BackupTaskProtocol.UploadBandwidthLimitCapability),
            AgentWorld.CapabilityReport(CapacityProbeProtocol.Capability));
        using (var sync = await scenario.World.SyncCapabilitiesAsync(scenario.Agent, capabilities)) Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        using (var heartbeat = await scenario.World.HeartbeatAsync(scenario.Agent, AgentWorld.Heartbeat(capabilityRevision: capabilities.Revision))) Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        return scenario;
    }

    private static async Task ConfigureScheduleAsync(BackupApiScenario scenario, Guid job, string cron = "0 0 * * *")
    {
        using var response = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{job}/schedule", new { cronExpression = cron, timeZone = "UTC" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task AddEvidenceAsync(BackupApiScenario scenario, Guid job, bool restorePassed, int used = 20)
    {
        await using var db = fixture.CreatePlatformContext();
        var run = await db.BackupRuns.Include(row => row.Attempts).ThenInclude(attempt => attempt.Artifacts).ThenInclude(artifact => artifact.Replicas).SingleAsync(row => row.JobId == job);
        var artifact = Assert.Single(run.CurrentAttempt.Artifacts);
        var replica = Assert.Single(artifact.Replicas);
        var now = scenario.Clock.GetUtcNow();
        db.StorageCapacitySnapshots.Add(new(Guid.NewGuid(), run.TenantId, replica.StorageConnectionId, 100, used, 100 - used, StorageCapacityStatus.Evaluate(100, used, 100 - used), now));
        var restore = new RestoreOperation(Guid.NewGuid(), run.TenantId, artifact.Id, replica.Id, RestoreSourceAccess.BackendRelayed, run.AgentId,
            new RestoreTarget("sql-target", null, true, null, true, false), "bp_verify_" + Guid.NewGuid().ToString("N"), RestoreMode.NewDatabase, false, null,
            Guid.NewGuid().ToString("N"), "health-fixture", null, now, RestorePurpose.Verification);
        restore.QueueFirstAttempt(Guid.NewGuid(), now);
        var token = Guid.NewGuid();
        restore.Claim(token, now, TimeSpan.FromMinutes(5));
        restore.Start(token, now, "1.0", "1.0", "linux");
        restore.Complete(token, now, new(restorePassed ? RestoreStatus.Succeeded : RestoreStatus.Failed,
            restorePassed ? null : "VerificationFailed", RestoreTargetState.NotModified, null, true));
        db.RestoreOperations.Add(restore);
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> HealthAsync(BackupApiScenario scenario, Guid job)
    {
        using var response = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{job}/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }
}
