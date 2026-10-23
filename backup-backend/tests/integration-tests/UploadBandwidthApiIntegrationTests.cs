extern alias AgentCore;
extern alias AgentTransport;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agents;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Cryptography;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
using BackupPlatform.IntegrationTests.PostgreSql;
using BackupPlatform.StorageProviders.Local;
using Microsoft.Extensions.Logging.Abstractions;
using AgentCore::BackupPlatform.Agent.Core.Backup;
using AgentCore::BackupPlatform.Agent.Core.Connectivity;
using AgentCore::BackupPlatform.Agent.Core.Identity;
using AgentCore::BackupPlatform.Agent.Core.Tasks;
using AgentTransport::BackupPlatform.Agent.Transport;

namespace BackupPlatform.IntegrationTests;

/// <summary>The management API (tenant administrators), the policy call (the running attempt's agent) and the relay's deadline allowance, through real HTTP.</summary>
public sealed class UploadBandwidthApiIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static async Task<JsonElement> GetAsync(BackupApiScenario scenario, BrowserSession? session = null)
    {
        using var response = await (session ?? scenario.Admin).GetAsync(scenario.Root + "/upload-bandwidth");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private static Task<HttpResponseMessage> PutAsync(BackupApiScenario scenario, string path, long? bytesPerSecond, BrowserSession? session = null) =>
        (session ?? scenario.Admin).SendAsync(HttpMethod.Put, scenario.Root + "/upload-bandwidth/" + path, new { bytesPerSecond });

    private static Task<HttpResponseMessage> PolicyAsync(BackupApiScenario scenario, TaskClaim claim, Guid? lease = null, bool release = false, EnrolledAgent? agent = null) =>
        scenario.AgentCallAsync(claim, "upload-policy", new UploadPolicyRequest { LeaseToken = lease ?? claim.LeaseToken, Release = release }, agent);

    [Theory]
    [InlineData(TenantRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(TenantRole.Operator, HttpStatusCode.Forbidden)]
    [InlineData(TenantRole.Admin, HttpStatusCode.OK)]
    public async Task Every_member_reads_the_limits_and_only_administrators_change_them(TenantRole role, HttpStatusCode expected)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var jobId = await scenario.CreateJobAsync();
        var session = await scenario.SessionAsync(role);

        var initial = await GetAsync(scenario, session);
        Assert.Equal(UploadBandwidth_Min, initial.GetProperty("minimumBytesPerSecond").GetInt64());
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("tenant").GetProperty("bytesPerSecond").ValueKind);
        Assert.Empty(initial.GetProperty("agents").EnumerateArray());

        using var tenant = await PutAsync(scenario, "tenant", 5_000_000, session);
        using var agent = await PutAsync(scenario, $"agents/{scenario.Agent.AgentId}", 2_000_000, session);
        using var job = await PutAsync(scenario, $"jobs/{jobId}", 1_000_000, session);
        Assert.Equal(expected, tenant.StatusCode);
        Assert.Equal(expected, agent.StatusCode);
        Assert.Equal(expected, job.StatusCode);

        var after = await GetAsync(scenario, session);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal(5_000_000, after.GetProperty("tenant").GetProperty("bytesPerSecond").GetInt64());
            Assert.Equal(2_000_000, Assert.Single(after.GetProperty("agents").EnumerateArray()).GetProperty("bytesPerSecond").GetInt64());
            Assert.Equal(jobId, Assert.Single(after.GetProperty("jobs").EnumerateArray()).GetProperty("jobId").GetGuid());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, after.GetProperty("tenant").GetProperty("bytesPerSecond").ValueKind);
            Assert.Empty(after.GetProperty("jobs").EnumerateArray());
        }
    }
    [Fact]
    public async Task Bandwidth_profiles_exist_only_under_the_tenant_route()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        using var tenantScoped = await scenario.Admin.GetAsync(scenario.Root + "/bandwidth-profiles");
        Assert.Equal(HttpStatusCode.OK, tenantScoped.StatusCode);
        using var tenantless = await scenario.Admin.GetAsync("/api/bandwidth-profiles");
        Assert.Equal(HttpStatusCode.NotFound, tenantless.StatusCode);
    }

    [Fact]
    public async Task Bandwidth_profiles_can_be_created_listed_assigned_and_deleted()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var jobId = await scenario.CreateJobAsync();

        // Create a profile
        var newProfile = new
        {
            name = "Office Hours",
            timeZoneId = "Europe/Istanbul",
            windows = new[]
            {
                new { dayOfWeek = (int)DayOfWeek.Monday, startTime = "09:00:00", endTime = "18:00:00", bytesPerSecond = 500_000 }
            }
        };
        using var createRes = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + "/bandwidth-profiles", newProfile);
        Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);
        var createdJson = AgentWorld.ReadJson(createRes);
        var profileId = createdJson.GetProperty("id").GetGuid();
        Assert.Equal("Office Hours", createdJson.GetProperty("name").GetString());

        // List profiles
        using var listRes = await scenario.Admin.GetAsync(scenario.Root + "/bandwidth-profiles");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        var list = AgentWorld.ReadJson(listRes).EnumerateArray().ToList();
        Assert.Contains(list, p => p.GetProperty("id").GetGuid() == profileId);

        // Assign to Job
        using var assignJobRes = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/upload-bandwidth/jobs/{jobId}/profile", new { profileId });
        Assert.Equal(HttpStatusCode.OK, assignJobRes.StatusCode);

        // Verify in settings
        var settings = await GetAsync(scenario);
        var jobItem = settings.GetProperty("jobs").EnumerateArray().Single(j => j.GetProperty("jobId").GetGuid() == jobId);
        Assert.Equal(profileId, jobItem.GetProperty("profileId").GetGuid());

        // Delete profile
        using var deleteRes = await scenario.Admin.SendAsync(HttpMethod.Delete, scenario.Root + $"/bandwidth-profiles/{profileId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);
    }

    [Theory]
    [InlineData("2026-10-25T00:44:59Z", "01:30:00", "01:45:00", 500_000L)]
    [InlineData("2026-10-25T01:29:59Z", "01:30:00", "01:45:00", null)]
    [InlineData("2026-03-29T00:59:59Z", "01:30:00", "03:00:00", null)]
    public async Task Policy_refreshes_at_the_actual_dst_window_transition(string instant, string start, string end, long? expectedRate)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(DateTimeOffset.Parse(instant)));
        var jobId = await scenario.CreateJobAsync();
        using var created = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + "/bandwidth-profiles", new
        {
            name = "DST", timeZoneId = "Europe/London",
            windows = new[] { new { dayOfWeek = 0, startTime = start, endTime = end, bytesPerSecond = 500_000 } }
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var profileId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        using var assigned = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/upload-bandwidth/jobs/{jobId}/profile", new { profileId });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);
        using var response = await PolicyAsync(scenario, claim);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var policy = AgentWorld.ReadJson(response);
        Assert.Equal(expectedRate, policy.GetProperty("jobBytesPerSecond").ValueKind == JsonValueKind.Null ? null : policy.GetProperty("jobBytesPerSecond").GetInt64());
        Assert.Equal(1, policy.GetProperty("refreshAfterSeconds").GetInt32());
    }

    [Fact]
    public async Task Overnight_profile_remains_effective_after_midnight_and_rejects_cross_day_overlap()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(DateTimeOffset.Parse("2026-06-16T00:59:59Z")));
        var jobId = await scenario.CreateJobAsync();
        using var created = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + "/bandwidth-profiles", new
        {
            name = "Night", timeZoneId = "UTC",
            windows = new[] { new { dayOfWeek = 1, startTime = "22:00:00", endTime = "01:00:00", bytesPerSecond = 500_000 } }
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var profileId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        using var assigned = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/upload-bandwidth/jobs/{jobId}/profile", new { profileId });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);
        using var policy = await PolicyAsync(scenario, claim);
        Assert.Equal(500_000, AgentWorld.ReadJson(policy).GetProperty("jobBytesPerSecond").GetInt64());
        Assert.Equal(1, AgentWorld.ReadJson(policy).GetProperty("refreshAfterSeconds").GetInt32());

        using var overlap = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/bandwidth-profiles/{profileId}", new
        {
            name = "Night", timeZoneId = "UTC",
            windows = new[]
            {
                new { dayOfWeek = 6, startTime = "22:00:00", endTime = "02:00:00", bytesPerSecond = 500_000 },
                new { dayOfWeek = 0, startTime = "01:00:00", endTime = "03:00:00", bytesPerSecond = 500_000 }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, overlap.StatusCode);
    }
    [Fact]
    public async Task The_same_profile_assigned_to_tenant_and_agent_uses_the_winning_agent_scope()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(DateTimeOffset.Parse("2026-06-15T10:00:00Z")));
        var jobId = await scenario.CreateJobAsync();
        using var created = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + "/bandwidth-profiles", new
        {
            name = "Shared", timeZoneId = "UTC",
            windows = new[] { new { dayOfWeek = 1, startTime = "09:00:00", endTime = "18:00:00", bytesPerSecond = 500_000 } }
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var profileId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        using var tenant = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + "/upload-bandwidth/tenant/profile", new { profileId });
        using var agent = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/upload-bandwidth/agents/{scenario.Agent.AgentId}/profile", new { profileId });
        Assert.Equal(HttpStatusCode.OK, tenant.StatusCode);
        Assert.Equal(HttpStatusCode.OK, agent.StatusCode);
        await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);
        using var response = await PolicyAsync(scenario, claim);
        var policy = AgentWorld.ReadJson(response);
        Assert.Equal(500_000, policy.GetProperty("agentBytesPerSecond").GetInt64());
        Assert.Equal(JsonValueKind.Null, policy.GetProperty("tenantShareBytesPerSecond").ValueKind);
        await using var context = fixture.CreatePlatformContext();
        Assert.False(await context.UploadBandwidthGrants.AnyAsync(g => g.AttemptId == claim.AttemptId));

        using var unassign = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/upload-bandwidth/agents/{scenario.Agent.AgentId}/profile", new { profileId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, unassign.StatusCode);
        using var tenantResponse = await PolicyAsync(scenario, claim);
        Assert.Equal(500_000, AgentWorld.ReadJson(tenantResponse).GetProperty("tenantShareBytesPerSecond").GetInt64());
        Assert.True(await context.UploadBandwidthGrants.AnyAsync(g => g.AttemptId == claim.AttemptId));
        using var reassign = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/upload-bandwidth/agents/{scenario.Agent.AgentId}/profile", new { profileId });
        Assert.Equal(HttpStatusCode.OK, reassign.StatusCode);
        using var agentResponse = await PolicyAsync(scenario, claim);
        Assert.Equal(JsonValueKind.Null, AgentWorld.ReadJson(agentResponse).GetProperty("tenantShareBytesPerSecond").ValueKind);
        Assert.False(await context.UploadBandwidthGrants.AnyAsync(g => g.AttemptId == claim.AttemptId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_job_profile_replaces_every_static_ceiling_including_unlimited_outside_windows(bool insideWindow)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(DateTimeOffset.Parse(insideWindow ? "2026-06-15T10:00:00Z" : "2026-06-15T19:00:00Z")));
        var jobId = await scenario.CreateJobAsync();
        using var tenantLimit = await PutAsync(scenario, "tenant", 48_000);
        using var agentLimit = await PutAsync(scenario, $"agents/{scenario.Agent.AgentId}", 32_000);
        using var jobLimit = await PutAsync(scenario, $"jobs/{jobId}", 16_000);
        Assert.Equal(HttpStatusCode.OK, tenantLimit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, agentLimit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, jobLimit.StatusCode);
        using var created = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + "/bandwidth-profiles", new
        {
            name = "Emergency", timeZoneId = "UTC",
            windows = new[] { new { dayOfWeek = 1, startTime = "09:00:00", endTime = "18:00:00", bytesPerSecond = 5_000_000 } }
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var profileId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        using var assigned = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/upload-bandwidth/jobs/{jobId}/profile", new { profileId });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);
        using var response = await PolicyAsync(scenario, claim);
        var policy = AgentWorld.ReadJson(response).Deserialize<UploadPolicyResponse>(JsonSerializerOptions.Web)!;
        Assert.Equal(insideWindow ? 5_000_000L : (long?)null, policy.JobBytesPerSecond);
        Assert.Equal(policy.JobBytesPerSecond, policy.EffectiveBytesPerSecond);
        Assert.Null(policy.AgentBytesPerSecond);
        Assert.Null(policy.TenantShareBytesPerSecond);
        await using var context = fixture.CreatePlatformContext();
        Assert.False(await context.UploadBandwidthGrants.AnyAsync(g => g.AttemptId == claim.AttemptId));
        using var unassigned = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/upload-bandwidth/jobs/{jobId}/profile", new { profileId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, unassigned.StatusCode);
        using var fallback = await PolicyAsync(scenario, claim);
        var defaults = AgentWorld.ReadJson(fallback).Deserialize<UploadPolicyResponse>(JsonSerializerOptions.Web)!;
        Assert.Equal(16_000, defaults.JobBytesPerSecond);
        Assert.Equal(32_000, defaults.AgentBytesPerSecond);
        Assert.Equal(48_000, defaults.TenantShareBytesPerSecond);
    }

    [Fact]
    public async Task A_zero_rate_window_signals_TimeRule_and_manual_resume_uses_a_fresh_lease_without_bypassing_the_active_window()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(DateTimeOffset.Parse("2026-06-15T08:59:59Z")));
        var jobId = await scenario.CreateJobAsync();
        using var created = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + "/bandwidth-profiles", new
        {
            name = "Pause", timeZoneId = "UTC",
            windows = new[]
            {
                new { dayOfWeek = 1, startTime = "08:00:00", endTime = "09:00:00", bytesPerSecond = 500_000 },
                new { dayOfWeek = 1, startTime = "09:00:00", endTime = "09:01:00", bytesPerSecond = 0 }
            }
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains(AgentWorld.ReadJson(created).GetProperty("windows").EnumerateArray(), w => w.GetProperty("bytesPerSecond").GetInt64() == 0);
        var profileId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        using var assigned = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + "/upload-bandwidth/tenant/profile", new { profileId });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var runId = await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);
        using var before = await PolicyAsync(scenario, claim);
        Assert.False(AgentWorld.ReadJson(before).GetProperty("pauseRequested").GetBoolean());
        Assert.Equal(1, AgentWorld.ReadJson(before).GetProperty("refreshAfterSeconds").GetInt32());
        await using var context = fixture.CreatePlatformContext();
        Assert.True(await context.UploadBandwidthGrants.AnyAsync(g => g.AttemptId == claim.AttemptId));
        var run = await context.BackupRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
        var destination = Assert.Single(run.Configuration.Destinations);
        using var checkpoint = await scenario.AgentCallAsync(claim, "progress", new BackupTaskProgressRequest
        {
            LeaseToken = claim.LeaseToken, Stage = "Uploading",
            Checkpoint = new TransferCheckpointContract { StorageProvider = destination.StorageProvider, DestinationId = destination.StorageConnectionId!.Value,
                VerifiedByteOffset = 4096, ProviderState = "time-rule-session" }
        });
        Assert.Equal(HttpStatusCode.NoContent, checkpoint.StatusCode);
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        using var during = await PolicyAsync(scenario, claim);
        Assert.True(AgentWorld.ReadJson(during).GetProperty("pauseRequested").GetBoolean());
        Assert.Equal(0, AgentWorld.ReadJson(during).GetProperty("effectiveBytesPerSecond").GetInt64());
        Assert.False(await context.UploadBandwidthGrants.AnyAsync(g => g.AttemptId == claim.AttemptId));
        using var lease = await scenario.AgentCallAsync(claim, "lease", new BackupTaskLeaseRequest { LeaseToken = claim.LeaseToken });
        Assert.True(AgentWorld.ReadJson(lease).GetProperty("timeRulePauseRequested").GetBoolean());
        Assert.False(AgentWorld.ReadJson(lease).GetProperty("pauseRequested").GetBoolean());
        using var completed = await scenario.CompleteRawAsync(claim, new BackupTaskCompletionRequest
        {
            LeaseToken = claim.LeaseToken, Status = "Paused", FailureCode = "TimeRule"
        });
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        var attempt = await context.BackupRunAttempts.AsNoTracking().SingleAsync(a => a.Id == claim.AttemptId);
        Assert.Equal(BackupPlatform.Domain.Backups.BackupStatus.Paused, attempt.Status);
        Assert.Equal(BackupPlatform.Domain.Backups.TransferPauseReason.TimeRule, attempt.PauseReason);
        Assert.Null(attempt.LeaseToken);
        Assert.Null(attempt.LeaseExpiresAt);
        using var resume = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/backup-runs/{runId}/resume");
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        var resumed = await scenario.ClaimAsync();
        Assert.Equal(claim.AttemptId, resumed.AttemptId);
        Assert.NotEqual(claim.LeaseToken, resumed.LeaseToken);
        using var restarted = await scenario.AgentCallAsync(resumed, "start", new BackupTaskStartRequest
        {
            LeaseToken = resumed.LeaseToken, SoftwareVersion = "1.2.3", ProtocolVersion = "1.0", Platform = "linux"
        });
        Assert.Equal(HttpStatusCode.OK, restarted.StatusCode);
        Assert.True(AgentWorld.ReadJson(restarted).GetProperty("timeRulePauseRequested").GetBoolean());
        using var stale = await PolicyAsync(scenario, claim);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var saved = await context.Set<BackupPlatform.Domain.Backups.TransferCheckpoint>().AsNoTracking().SingleAsync(c => c.AttemptId == claim.AttemptId);
        Assert.Equal(4096, saved.VerifiedByteOffset);
        using var repaused = await scenario.CompleteRawAsync(resumed, new BackupTaskCompletionRequest
        {
            LeaseToken = resumed.LeaseToken, Status = "Paused", FailureCode = "TimeRule"
        });
        Assert.Equal(HttpStatusCode.NoContent, repaused.StatusCode);
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(BackupPlatform.Domain.Backups.BackupStatus.Paused,
            (await context.BackupRunAttempts.AsNoTracking().SingleAsync(a => a.Id == claim.AttemptId)).Status);
        using var resumeAfter = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/backup-runs/{runId}/resume");
        Assert.Equal(HttpStatusCode.OK, resumeAfter.StatusCode);
        var outside = await scenario.ClaimAsync();
        Assert.NotEqual(resumed.LeaseToken, outside.LeaseToken);
        await scenario.StartAttemptAsync(outside);
        using var after = await PolicyAsync(scenario, outside);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.False(AgentWorld.ReadJson(after).GetProperty("pauseRequested").GetBoolean());
        Assert.Equal(JsonValueKind.Null, AgentWorld.ReadJson(after).GetProperty("effectiveBytesPerSecond").ValueKind);
    }

    [Fact]
    public async Task A_real_PostgreSql_dump_upload_automatically_pauses_on_the_schedule_and_commits_TimeRule_with_its_verified_prefix_and_released_lease()
    {
        var database = new PostgreSqlProviderFixture();
        await database.InitializeAsync();
        try
        {
            var source = await database.DatabaseAsync();
            await database.ExecuteAsync(source, "CREATE TABLE payload AS SELECT md5(g::text) AS value FROM generate_series(1, 32768) g");
            await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(DateTimeOffset.Parse("2026-06-15T08:59:59Z")));
            await using (var capabilities = fixture.CreatePlatformContext())
            {
                capabilities.AgentCapabilities.Add(new AgentCapabilityRecord(new AgentId(scenario.Agent.AgentId), new TenantId(scenario.Agent.TenantId),
                    database.Backup.Engine.Value + ".backup.full", CapabilitySupport.Supported, null, "schedule-smoke", scenario.Clock.GetUtcNow()));
                await capabilities.SaveChangesAsync();
            }
            using var jobResponse = await scenario.CreateJobRawAsync(new
            {
                name = "Scheduled real upload", agentId = scenario.Agent.AgentId,
                configuration = new BackupExecutionConfiguration
                {
                    Engine = database.Backup.Engine.Value, Database = source, Host = database.Settings.Endpoint.Host,
                    Port = database.Settings.Endpoint.Port, IntegratedSecurity = false, Encrypt = false, CompressionPolicy = "Disabled",
                    DatabaseLogin = new(database.Credential.UserName, database.Credential.Password.Reveal()),
                    Destinations = BackupTestDestinations.LocalAgent
                }
            });
            Assert.True(jobResponse.StatusCode == HttpStatusCode.Created, await jobResponse.Content.ReadAsStringAsync());
            var jobId = AgentWorld.ReadJson(jobResponse).GetProperty("id").GetGuid();
            using var profileResponse = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + "/bandwidth-profiles", new
            {
                name = "Automatic pause", timeZoneId = "UTC",
                windows = new[]
                {
                    new { dayOfWeek = 1, startTime = "08:00:00", endTime = "09:00:00", bytesPerSecond = 16_000 },
                    new { dayOfWeek = 1, startTime = "09:00:00", endTime = "09:01:00", bytesPerSecond = 0 }
                }
            });
            Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);
            var profileId = AgentWorld.ReadJson(profileResponse).GetProperty("id").GetGuid();
            using var assigned = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + "/upload-bandwidth/tenant/profile", new { profileId });
            Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
            var runId = await scenario.StartRunAsync(jobId);
            using var poll = await scenario.World.PollAsync(scenario.Agent);
            var snapshot = Assert.Single(AgentWorld.ReadJson(poll).GetProperty("tasks").EnumerateArray()).GetProperty("backup")
                .Deserialize<BackupTaskSnapshot>(JsonSerializerOptions.Web)!;
            var identity = new AgentLocalIdentity(scenario.Agent.AgentId, new Uri("https://localhost/"), scenario.Agent.InstallationId,
                new AgentStoredCredential(scenario.Agent.CredentialId, scenario.Agent.Secret, 1));
            var identities = new ScheduleIdentity(identity);
            var client = new HttpBackupTaskClient(scenario.World.Anonymous, new AgentBackendEndpoint(identity.ServerUri), scenario.Clock);
            var storageRoot = Path.Combine(database.DirectoryPath, "storage");
            var staging = Path.Combine(database.DirectoryPath, "staging");
            Directory.CreateDirectory(storageRoot);
            Directory.CreateDirectory(staging);
            var storage = new LocalStorageProvider(new(storageRoot));
            var pipeline = new DatabaseBackupPipeline([database.Backup], [storage], new PassThroughArtifactPreparer(),
                NullLogger<DatabaseBackupPipeline>.Instance, scenario.Clock);
            var requests = new BackupTaskRequestFactory(new ScheduleDatabaseCredential(database.Credential), [StagingArea.SameHost(staging)],
                uploadBandwidth: task => new UploadBandwidthGovernor(client, identities, task.AttemptId, task.LeaseToken, scenario.Clock));
            var worker = new BackupTaskWorker(client, identities, pipeline, requests, new AgentSoftwareInfo("1.2.3", "linux", null, null),
                scenario.Clock, NullLogger<BackupTaskWorker>.Instance);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var running = worker.RunAsync(deadline.Token);
            try
            {
                Assert.True(worker.TryPublish(new AgentTaskEnvelope { Id = snapshot.AttemptId.ToString(), Type = "database-backup", Backup = snapshot }));
                await using var context = fixture.CreatePlatformContext();
                while (!await context.Set<BackupPlatform.Domain.Backups.TransferCheckpoint>().AnyAsync(
                    checkpoint => checkpoint.AttemptId == snapshot.AttemptId && checkpoint.VerifiedByteOffset > 0, deadline.Token))
                    await Task.Delay(20, deadline.Token);
                Assert.True(await context.UploadBandwidthGrants.AnyAsync(grant => grant.AttemptId == snapshot.AttemptId, deadline.Token));
                scenario.Clock.Advance(TimeSpan.FromSeconds(1));
                while (!await context.BackupRunAttempts.AnyAsync(attempt => attempt.Id == snapshot.AttemptId &&
                    attempt.Status == BackupPlatform.Domain.Backups.BackupStatus.Paused, deadline.Token))
                    await Task.Delay(20, deadline.Token);
                var attempt = await context.BackupRunAttempts.AsNoTracking().SingleAsync(a => a.Id == snapshot.AttemptId, deadline.Token);
                Assert.Equal(BackupPlatform.Domain.Backups.TransferPauseReason.TimeRule, attempt.PauseReason);
                Assert.Null(attempt.LeaseToken);
                Assert.Null(attempt.LeaseExpiresAt);
                Assert.False(await context.UploadBandwidthGrants.AnyAsync(grant => grant.AttemptId == snapshot.AttemptId, deadline.Token));
                var checkpoint = await context.Set<BackupPlatform.Domain.Backups.TransferCheckpoint>().AsNoTracking()
                    .SingleAsync(c => c.AttemptId == snapshot.AttemptId, deadline.Token);
                Assert.True(checkpoint.VerifiedByteOffset > 0);
                var partial = Assert.Single(Directory.GetFiles(storageRoot, "*.partial", SearchOption.AllDirectories));
                var artifact = Assert.Single(Directory.GetFiles(staging), path => !path.EndsWith(".json", StringComparison.Ordinal)
                    && !path.EndsWith(".resume-pin", StringComparison.Ordinal));
                var retained = await File.ReadAllBytesAsync(partial, deadline.Token);
                var staged = await File.ReadAllBytesAsync(artifact, deadline.Token);
                Assert.Equal(checkpoint.VerifiedByteOffset, retained.Length);
                Assert.True(retained.Length < staged.Length);
                Assert.Equal(SHA256.HashData(staged.AsSpan(0, retained.Length)), SHA256.HashData(retained));
                using var detail = await scenario.Admin.GetAsync(scenario.Root + $"/backup-runs/{runId}");
                Assert.Equal("Paused", AgentWorld.ReadJson(detail).GetProperty("status").GetString());
            }
            finally
            {
                await deadline.CancelAsync();
                await running;
            }
        }
        finally { await database.DisposeAsync(); }
    }

    private sealed class ScheduleIdentity(AgentLocalIdentity identity) : IAgentCredentialStore
    {
        public Task<AgentLocalIdentity?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<AgentLocalIdentity?>(identity);
        public Task SaveAsync(AgentLocalIdentity value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ScheduleDatabaseCredential(DatabaseCredential credential) : IBackupTaskCredentialResolver
    {
        public ValueTask<DatabaseCredential> ResolveAsync(string reference, CancellationToken cancellationToken) => ValueTask.FromResult(credential);
    }


    private const long UploadBandwidth_Min = BackupPlatform.Domain.Backups.UploadBandwidth.MinimumBytesPerSecond;

    [Theory]
    [InlineData(0L, HttpStatusCode.BadRequest)]
    [InlineData(1L, HttpStatusCode.BadRequest)]
    [InlineData(UploadBandwidth_Min - 1, HttpStatusCode.BadRequest)]
    [InlineData(BackupPlatform.Domain.Backups.UploadBandwidth.MaximumBytesPerSecond + 1, HttpStatusCode.BadRequest)]
    [InlineData(-5L, HttpStatusCode.BadRequest)]
    [InlineData(UploadBandwidth_Min, HttpStatusCode.OK)]
    [InlineData(null, HttpStatusCode.OK)]
    public async Task A_limit_outside_the_supported_range_is_refused_and_null_means_unlimited(long? bytesPerSecond, HttpStatusCode expected)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);

        using var response = await PutAsync(scenario, "tenant", bytesPerSecond);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
        {
            var error = AgentWorld.ReadJson(response);
            Assert.Equal("out_of_range", error.GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, (await GetAsync(scenario)).GetProperty("tenant").GetProperty("bytesPerSecond").ValueKind);
        }
    }

    [Fact]
    public async Task An_agent_or_job_of_another_tenant_is_not_found_and_the_tenant_route_cannot_name_another_tenant()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var other = await scenario.World.CreateTenantAsync();
        var foreignAgent = await scenario.World.EnrollAsync(other);
        using var foreignJob = await other.Admin.SendAsync(HttpMethod.Post, BackupApiScenario.RootOf(other) + "/backup-jobs", BackupApiScenario.JobBody(foreignAgent.AgentId));
        var foreignJobId = AgentWorld.ReadJson(foreignJob).GetProperty("id").GetGuid();

        using var agent = await PutAsync(scenario, $"agents/{foreignAgent.AgentId}", 2_000_000);
        using var job = await PutAsync(scenario, $"jobs/{foreignJobId}", 2_000_000);
        using var unknown = await PutAsync(scenario, $"agents/{Guid.NewGuid()}", 2_000_000);

        Assert.Equal(HttpStatusCode.NotFound, agent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, job.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Empty((await GetAsync(scenario)).GetProperty("agents").EnumerateArray());
        using var theirs = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + "/upload-bandwidth");
        Assert.Empty(AgentWorld.ReadJson(theirs).GetProperty("agents").EnumerateArray());
        // A member of another tenant cannot read or change this tenant's limits through this tenant's route.
        using var read = await other.Admin.GetAsync(scenario.Root + "/upload-bandwidth");
        using var write = await other.Admin.SendAsync(HttpMethod.Put, scenario.Root + "/upload-bandwidth/tenant", new { bytesPerSecond = 2_000_000 });
        Assert.NotEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, write.StatusCode);
    }

    [Fact]
    public async Task The_running_attempts_agent_learns_every_ceiling_and_nobody_else_does()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var jobId = await scenario.CreateJobAsync();
        await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();
        using (var early = await PolicyAsync(scenario, claim)) Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        await scenario.StartAttemptAsync(claim);
        using (await PutAsync(scenario, $"jobs/{jobId}", 1_000_000)) { }
        using (await PutAsync(scenario, $"agents/{scenario.Agent.AgentId}", 2_000_000)) { }

        using var response = await PolicyAsync(scenario, claim);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var policy = AgentWorld.ReadJson(response);
        Assert.Equal(1_000_000, policy.GetProperty("jobBytesPerSecond").GetInt64());
        Assert.Equal(2_000_000, policy.GetProperty("agentBytesPerSecond").GetInt64());
        Assert.Equal(JsonValueKind.Null, policy.GetProperty("tenantShareBytesPerSecond").ValueKind);
        Assert.Equal(1_000_000, policy.GetProperty("minimumBytesPerSecond").GetInt64());
        using var wrongLease = await PolicyAsync(scenario, claim, lease: Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, wrongLease.StatusCode);
        using var emptyLease = await PolicyAsync(scenario, claim, lease: Guid.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, emptyLease.StatusCode);
        using var anonymous = await scenario.World.Anonymous.PostAsJsonAsync($"/api/agent/tasks/{claim.AttemptId}/upload-policy", new UploadPolicyRequest { LeaseToken = claim.LeaseToken });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        // Another tenant's agent holding the right lease token still cannot read this attempt's policy.
        var other = await scenario.World.CreateTenantAsync();
        var stranger = await scenario.World.DeclareBackupCapabilityAsync(await scenario.World.EnrollAsync(other));
        using var foreign = await PolicyAsync(scenario, claim, agent: stranger);
        Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
    }

    [Fact]
    public async Task Two_agents_of_one_tenant_split_the_tenant_limit_through_the_api()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var second = await scenario.World.DeclareBackupCapabilityAsync(await scenario.World.EnrollAsync(scenario.Setup));
        var firstJob = await scenario.CreateJobAsync();
        var secondJob = await scenario.CreateJobAsync(agent: second);
        await scenario.StartRunAsync(firstJob);
        await scenario.StartRunAsync(secondJob);
        var first = await scenario.ClaimAsync();
        var other = await scenario.ClaimAsync(second);
        await scenario.StartAttemptAsync(first);
        using (var started = await scenario.AgentCallAsync(other, "start",
            new BackupTaskStartRequest { LeaseToken = other.LeaseToken, SoftwareVersion = "1.2.3", ProtocolVersion = "1.0", Platform = "linux" }, second))
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using (await PutAsync(scenario, "tenant", 1_000_000)) { }

        async Task<long> ShareAsync(TaskClaim claim, EnrolledAgent? agent = null)
        {
            using var response = await PolicyAsync(scenario, claim, agent: agent);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return AgentWorld.ReadJson(response).GetProperty("tenantShareBytesPerSecond").GetInt64();
        }

        Assert.Equal(1_000_000, await ShareAsync(first));
        Assert.Equal(0, await ShareAsync(other, second));
        Assert.Equal(500_000, await ShareAsync(first));
        Assert.Equal(500_000, await ShareAsync(other, second));

        using (await PolicyAsync(scenario, first, release: true)) { }
        Assert.Equal(1_000_000, await ShareAsync(other, second));
    }

    [Fact]
    public async Task The_relay_itself_paces_the_body_and_widens_the_provider_deadline_by_the_throttled_time()
    {
        var provider = new CapturingProvider();
        await using var world = await RelayWorldAsync(provider, kestrel: false);
        var run = await StartRelayRunAsync(world);
        var body = new byte[200_000];

        using (var unlimited = await world.Anonymous.SendAsync(UploadRequest(run, new ByteArrayContent(body)))) Assert.Equal(HttpStatusCode.OK, unlimited.StatusCode);
        Assert.Equal(TimeSpan.Zero, provider.Options.Single().TransferAllowance);

        using (await run.Setup.Admin.SendAsync(HttpMethod.Put, run.Root + $"/upload-bandwidth/jobs/{run.JobId}", new { bytesPerSecond = 100_000 })) { }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // The client sends at full speed and does no pacing of its own: whatever the agent does, the relay holds the upload to the limit.
        using (var limited = await world.Anonymous.SendAsync(UploadRequest(run, new ByteArrayContent(body)))) Assert.Equal(HttpStatusCode.OK, limited.StatusCode);
        watch.Stop();

        Assert.Equal(TimeSpan.FromSeconds(2), provider.Options[^1].TransferAllowance);
        Assert.Equal(200_000, provider.Received[^1]);
        var floor = (body.Length - StorageProviders.Abstractions.Transfer.TransferRateLimiter.Capacity(100_000)) / 100_000;
        Assert.True(watch.Elapsed.TotalSeconds >= floor - 0.1, $"{body.Length} bytes were relayed in {watch.Elapsed}, faster than the limit allows ({floor:F2} s)");
        // Paced once, not twice: nothing else slows it down.
        Assert.True(watch.Elapsed.TotalSeconds < floor * 2 + 1, $"{watch.Elapsed} is far slower than one pacing");
    }

    // Real sockets and real Kestrel: the RELAY reads a body at the lowest allowed limit for longer than Kestrel's 5 s minimum-data-rate grace period. The client is
    // unpaced (as a modified agent would be); the request must neither be rejected as too slow nor lose a byte.
    [Fact]
    public async Task A_body_the_relay_reads_at_the_lowest_limit_crosses_real_kestrel_with_the_bytes_intact_and_at_the_limit()
    {
        const long rate = 16_000;
        var provider = new CapturingProvider();
        await using var world = await RelayWorldAsync(provider, kestrel: true);
        var run = await StartRelayRunAsync(world);
        using (var set = await run.Setup.Admin.SendAsync(HttpMethod.Put, run.Root + $"/upload-bandwidth/jobs/{run.JobId}", new { bytesPerSecond = rate }))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var body = Enumerable.Range(0, 96_000).Select(i => (byte)(i * 31 % 251)).ToArray();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await world.Anonymous.SendAsync(UploadRequest(run, new ByteArrayContent(body)));
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body.LongLength, provider.Received.Single());
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(body), provider.Hashes.Single());
        var floor = (body.Length - StorageProviders.Abstractions.Transfer.TransferRateLimiter.Capacity(rate)) / rate;
        Assert.True(watch.Elapsed.TotalSeconds >= floor - 0.1, $"{body.Length} bytes crossed in {watch.Elapsed}, faster than the {rate} B/s ceiling allows ({floor:F1} s)");
        Assert.True(watch.Elapsed.TotalSeconds < floor * 3, $"{watch.Elapsed} is far slower than the ceiling");
        Assert.True(watch.Elapsed > TimeSpan.FromSeconds(5), "the transfer must outlast Kestrel's minimum-data-rate grace period to prove anything");
    }

    // A relayed upload under a tenant limit starts only when a slice is free: it neither runs beside an upload that still holds the whole limit nor sits open
    // without a byte flowing.
    [Fact]
    public async Task A_relayed_upload_under_a_tenant_limit_waits_for_a_free_slice_before_it_reads_a_byte()
    {
        var provider = new CapturingProvider();
        await using var world = await RelayWorldAsync(provider, kestrel: false);
        var run = await StartRelayRunAsync(world);
        using (await run.Setup.Admin.SendAsync(HttpMethod.Put, run.Root + "/upload-bandwidth/tenant", new { bytesPerSecond = 150_000 })) { }
        // Another running attempt of the tenant already holds the whole limit.
        var other = await world.DeclareBackupCapabilityAsync(await world.EnrollAsync(run.Setup));
        var otherRun = await StartRunOnAsync(world, run, other);
        async Task<long> RenewOtherAsync()
        {
            using var response = await world.SendAgentAsync(HttpMethod.Post, $"/api/agent/tasks/{otherRun.AttemptId}/upload-policy", other.CredentialId, other.Secret,
                body: new UploadPolicyRequest { LeaseToken = otherRun.Lease });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return AgentWorld.ReadJson(response).GetProperty("tenantShareBytesPerSecond").GetInt64();
        }
        Assert.Equal(150_000, await RenewOtherAsync());

        var upload = world.Anonymous.SendAsync(UploadRequest(run, new ByteArrayContent(new byte[60_000])));
        await Task.Delay(1500);
        Assert.False(upload.IsCompleted);
        Assert.Empty(provider.Received);

        Assert.Equal(75_000, await RenewOtherAsync());
        using var response = await upload.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(60_000, provider.Received.Single());
    }

    [Fact]
    public async Task A_relayed_upload_whose_lease_is_gone_is_refused_before_any_byte_is_read()
    {
        var provider = new CapturingProvider();
        await using var world = await RelayWorldAsync(provider, kestrel: false);
        var run = await StartRelayRunAsync(world);
        await using (var db = fixture.CreatePlatformContext())
            await db.BackupRunAttempts.Where(attempt => attempt.Id == run.AttemptId).ExecuteUpdateAsync(set => set.SetProperty(attempt => attempt.LeaseToken, Guid.NewGuid()));

        using var response = await world.Anonymous.SendAsync(UploadRequest(run, new ByteArrayContent(new byte[1_000])));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(provider.Received);
    }

    private sealed record RelayRun(TenantSetup Setup, EnrolledAgent Agent, Guid ConnectionId, Guid JobId, Guid AttemptId, Guid Lease, string Key, string Root);

    private Task<AgentWorld> RelayWorldAsync(IStorageProvider provider, bool kestrel) =>
        AgentWorld.CreateAsync(fixture, kestrel: kestrel, limits: new AgentRateLimitOptions { PollPerAgent = 1000 }, configureServices: services =>
        {
            services.RemoveAll<IBackupStorageResolver>();
            services.AddSingleton<IBackupStorageResolver>(new FixedResolver(provider));
        });

    /// <summary>A tenant with a running attempt whose only destination is a backend-relayed storage connection.</summary>
    private async Task<RelayRun> StartRelayRunAsync(AgentWorld world)
    {
        var setup = await world.CreateTenantAsync();
        await world.EntitleAsync(setup.Tenant);
        var agent = await world.DeclareBackupCapabilityAsync(await world.EnrollAsync(setup));
        var connectionId = Guid.NewGuid();
        await using (var db = fixture.CreatePlatformContext())
        {
            var connection = new StorageConnection(connectionId, setup.Tenant.Id, "Relay allowance", "google-drive", StorageOwnership.TenantManaged,
                "tenant", new Dictionary<string, string>(), false, DateTimeOffset.UtcNow);
            connection.AttachSecret($"sc-{connectionId:N}-allowance", ["refreshToken"], DateTimeOffset.UtcNow);
            db.StorageConnections.Add(connection);
            await db.SaveChangesAsync();
        }
        var root = BackupApiScenario.RootOf(setup);
        using var created = await setup.Admin.SendAsync(HttpMethod.Post, root + "/backup-jobs", new
        {
            name = "Relay allowance", agentId = agent.AgentId,
            configuration = new BackupExecutionConfiguration
            {
                Database = "master", Host = "sql-server", IntegratedSecurity = true,
                Destinations = [BackupTestDestinations.Relayed("google-drive", connectionId, primary: true)]
            }
        });
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        using var csrf = await setup.Admin.Client.GetAsync("/api/auth/antiforgery");
        using var start = new HttpRequestMessage(HttpMethod.Post, root + $"/backup-jobs/{jobId}/runs");
        start.Headers.Add("X-CSRF-TOKEN", AgentWorld.ReadJson(csrf).GetProperty("requestToken").GetString());
        start.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var started = await setup.Admin.Client.SendAsync(start);
        var runId = AgentWorld.ReadJson(started).GetProperty("id").GetGuid();
        using var polled = await world.PollAsync(agent);
        var task = Assert.Single(AgentWorld.ReadJson(polled).GetProperty("tasks").EnumerateArray()).GetProperty("backup");
        var attemptId = task.GetProperty("attemptId").GetGuid();
        var lease = task.GetProperty("leaseToken").GetGuid();
        using var running = await world.SendAgentAsync(HttpMethod.Post, $"/api/agent/tasks/{attemptId}/start", agent.CredentialId, agent.Secret,
            body: new BackupTaskStartRequest { LeaseToken = lease, SoftwareVersion = "1.2.3", ProtocolVersion = "1.0", Platform = "linux" });
        Assert.Equal(HttpStatusCode.OK, running.StatusCode);
        var key = ManagedBackupLayout.AttemptScope(setup.Tenant.Id.Value, jobId, runId, attemptId) + "/" + ManagedBackupLayout.StagedFileName("master", DateTimeOffset.UtcNow, Guid.NewGuid());
        return new RelayRun(setup, agent, connectionId, jobId, attemptId, lease, key, root);
    }

    /// <summary>A second running attempt of the same tenant, on another agent.</summary>
    private static async Task<(Guid AttemptId, Guid Lease)> StartRunOnAsync(AgentWorld world, RelayRun run, EnrolledAgent agent)
    {
        using var created = await run.Setup.Admin.SendAsync(HttpMethod.Post, run.Root + "/backup-jobs", BackupApiScenario.JobBody(agent.AgentId));
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        using var csrf = await run.Setup.Admin.Client.GetAsync("/api/auth/antiforgery");
        using var start = new HttpRequestMessage(HttpMethod.Post, run.Root + $"/backup-jobs/{jobId}/runs");
        start.Headers.Add("X-CSRF-TOKEN", AgentWorld.ReadJson(csrf).GetProperty("requestToken").GetString());
        start.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var started = await run.Setup.Admin.Client.SendAsync(start);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var polled = await world.PollAsync(agent);
        var task = Assert.Single(AgentWorld.ReadJson(polled).GetProperty("tasks").EnumerateArray()).GetProperty("backup");
        var attemptId = task.GetProperty("attemptId").GetGuid();
        var lease = task.GetProperty("leaseToken").GetGuid();
        using var running = await world.SendAgentAsync(HttpMethod.Post, $"/api/agent/tasks/{attemptId}/start", agent.CredentialId, agent.Secret,
            body: new BackupTaskStartRequest { LeaseToken = lease, SoftwareVersion = "1.2.3", ProtocolVersion = "1.0", Platform = "linux" });
        Assert.Equal(HttpStatusCode.OK, running.StatusCode);
        return (attemptId, lease);
    }

    private static HttpRequestMessage UploadRequest(RelayRun run, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/agent/tasks/{run.AttemptId}/storage/content");
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(run.Agent.CredentialId, run.Agent.Secret));
        request.Headers.Add(AgentProtocolHeader.Name, "1.0");
        request.Headers.Add(TaskStorageProtocol.LeaseHeader, run.Lease.ToString("D"));
        request.Headers.Add(TaskStorageProtocol.KeyHeader, run.Key);
        request.Headers.Add(TaskStorageProtocol.DestinationHeader, run.ConnectionId.ToString("D"));
        request.Headers.Add(TaskStorageProtocol.ChecksumHeader, new string('a', 64));
        request.Content = content;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    private sealed class CapturingProvider : IStorageProvider
    {
        public List<StoragePutOptions> Options { get; } = [];
        public List<long> Received { get; } = [];
        public List<byte[]> Hashes { get; } = [];
        public StorageProviderId Id => new("google-drive");
        public StorageTopology Topology => StorageTopology.BackendRelayed;
        public StorageCapabilities Capabilities => StorageCapabilities.None;

        public async Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            Options.Add(options);
            using var sink = new MemoryStream();
            await content.CopyToAsync(sink, cancellationToken);
            Received.Add(sink.Length);
            Hashes.Add(System.Security.Cryptography.SHA256.HashData(sink.ToArray()));
            return new StorageObjectMetadata(new StorageLocation(Id, key), sink.Length, null, DateTimeOffset.UtcNow);
        }

        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedResolver(IStorageProvider provider) : IBackupStorageResolver
    {
        public IReadOnlyList<BackupManagedStorage> ManagedStorages => [];
        public IStorageProvider? Resolve(BackupPlatform.Domain.Identity.TenantId tenantId, string storageProvider, string storageTarget) => provider;
    }
}
