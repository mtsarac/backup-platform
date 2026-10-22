using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.IntegrationTests;

public sealed class BackupOrchestrationApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Theory]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Operator)]
    public async Task ManualStartIsIdempotentAndPendingCancellationPreventsClaim(TenantRole role)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        await world.EntitleAsync(setup.Tenant);
        var agent = await world.DeclareBackupCapabilityAsync(await world.EnrollAsync(setup));
        var account = await world.SeedUserAsync();
        await world.GrantAsync(setup.Tenant, account, role);
        var session = await world.SignInAsync(account);
        var root = $"/api/tenants/{setup.Tenant.Id.Value}";
        using var create = await session.SendAsync(HttpMethod.Post, root + "/backup-jobs", Job(agent.AgentId));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var jobId = AgentWorld.ReadJson(create).GetProperty("id").GetGuid();
        using var first = await StartAsync(session, root, jobId, "manual-1");
        using var repeat = await StartAsync(session, root, jobId, "manual-1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        var runId = AgentWorld.ReadJson(first).GetProperty("id").GetGuid();
        Assert.Equal(runId, AgentWorld.ReadJson(repeat).GetProperty("id").GetGuid());
        using var cancel = await session.SendAsync(HttpMethod.Post, root + $"/backup-runs/{runId}/cancel");
        Assert.Equal("Cancelled", AgentWorld.ReadJson(cancel).GetProperty("status").GetString());
        using var poll = await world.PollAsync(agent);
        Assert.Empty(AgentWorld.ReadJson(poll).GetProperty("tasks").EnumerateArray());
        using var list = await session.GetAsync(root + "/backup-runs");
        Assert.Equal(runId, Assert.Single(AgentWorld.ReadJson(list).GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task JobWithoutDestinationsIsRejectedOnTheDestinationsField()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        await world.EntitleAsync(setup.Tenant);
        var agent = await world.EnrollAsync(setup);
        var body = new CreateBackupJobRequestBody("No destinations", agent.AgentId, new BackupExecutionConfiguration
        {
            Database = "master", Host = "sql-server", IntegratedSecurity = true
        });

        using var response = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/backup-jobs", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("configuration.destinations", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ViewerCanReadButCannotCreateStartOrCancelAndCrossTenantReadsAreHidden()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        await world.EntitleAsync(setup.Tenant);
        var other = await world.CreateTenantAsync();
        var agent = await world.DeclareBackupCapabilityAsync(await world.EnrollAsync(setup));
        var root = $"/api/tenants/{setup.Tenant.Id.Value}";
        using var create = await setup.Admin.SendAsync(HttpMethod.Post, root + "/backup-jobs", Job(agent.AgentId));
        var jobId = AgentWorld.ReadJson(create).GetProperty("id").GetGuid();
        using var start = await StartAsync(setup.Admin, root, jobId, "viewer-test");
        var runId = AgentWorld.ReadJson(start).GetProperty("id").GetGuid();
        var viewer = await world.SeedUserAsync();
        await world.GrantAsync(setup.Tenant, viewer, TenantRole.Viewer);
        var session = await world.SignInAsync(viewer);
        using var detail = await session.GetAsync(root + $"/backup-runs/{runId}");
        using var list = await session.GetAsync(root + "/backup-runs");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(runId, Assert.Single(AgentWorld.ReadJson(list).GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        using var deniedCreate = await session.SendAsync(HttpMethod.Post, root + "/backup-jobs", Job(agent.AgentId));
        using var deniedStart = await StartAsync(session, root, jobId, "denied");
        using var deniedCancel = await session.SendAsync(HttpMethod.Post, root + $"/backup-runs/{runId}/cancel");
        Assert.Equal(HttpStatusCode.Forbidden, deniedCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedStart.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedCancel.StatusCode);
        using var hidden = await other.Admin.GetAsync($"/api/tenants/{other.Tenant.Id.Value}/backup-runs/{runId}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var body = await detail.Content.ReadAsStringAsync();
        Assert.DoesNotContain("leaseToken", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceMetadataJson", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(agent.Secret, body);
    }

    [Fact]
    public async Task StartRequiresBoundedKeyAndBrowserAntiforgery()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        await world.EntitleAsync(setup.Tenant);
        var agent = await world.DeclareBackupCapabilityAsync(await world.EnrollAsync(setup));
        var root = $"/api/tenants/{setup.Tenant.Id.Value}";
        using var create = await setup.Admin.SendAsync(HttpMethod.Post, root + "/backup-jobs", Job(agent.AgentId));
        var jobId = AgentWorld.ReadJson(create).GetProperty("id").GetGuid();
        using var missing = await setup.Admin.SendAsync(HttpMethod.Post, root + $"/backup-jobs/{jobId}/runs");
        using var oversized = await StartAsync(setup.Admin, root, jobId, new string('a', 129));
        using var noCsrf = await setup.Admin.SendAsync(HttpMethod.Post, root + "/backup-jobs", Job(agent.AgentId), includeAntiforgery: false);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
    }

    [Fact]
    public async Task ClaimedLeaseIsFencedAndRenewalObservesCancellation()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        await world.EntitleAsync(setup.Tenant);
        var agent = await world.DeclareBackupCapabilityAsync(await world.EnrollAsync(setup));
        var stranger = await world.EnrollAsync(setup);
        var root = $"/api/tenants/{setup.Tenant.Id.Value}";
        using var create = await setup.Admin.SendAsync(HttpMethod.Post, root + "/backup-jobs", Job(agent.AgentId));
        var jobId = AgentWorld.ReadJson(create).GetProperty("id").GetGuid();
        using var startRun = await StartAsync(setup.Admin, root, jobId, "claim-test");
        var runId = AgentWorld.ReadJson(startRun).GetProperty("id").GetGuid();
        using var poll = await world.PollAsync(agent);
        var task = Assert.Single(AgentWorld.ReadJson(poll).GetProperty("tasks").EnumerateArray()).GetProperty("backup");
        var attempt = task.GetProperty("attemptId").GetGuid();
        var token = task.GetProperty("leaseToken").GetGuid();
        using var busy = await world.PollAsync(agent);
        Assert.Empty(AgentWorld.ReadJson(busy).GetProperty("tasks").EnumerateArray());
        var taskRoot = $"/api/agent/tasks/{attempt}";
        var start = new BackupTaskStartRequest { LeaseToken = token, SoftwareVersion = "1.2.3", ProtocolVersion = "1.0", Platform = "linux" };
        using var foreign = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/start", stranger.CredentialId, stranger.Secret, body: start);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var wrongToken = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/start", agent.CredentialId, agent.Secret, body: start with { LeaseToken = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, wrongToken.StatusCode);
        using var started = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/start", agent.CredentialId, agent.Secret, body: start);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var missingArtifact = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/complete", agent.CredentialId, agent.Secret,
            body: new BackupTaskCompletionRequest { LeaseToken = token, Status = "Succeeded" });
        Assert.Equal(HttpStatusCode.BadRequest, missingArtifact.StatusCode);
        using var unsafeReason = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/complete", agent.CredentialId, agent.Secret,
            body: new BackupTaskCompletionRequest { LeaseToken = token, Status = "Failed", FailureCode = "Password=raw-diagnostic" });
        Assert.Equal(HttpStatusCode.BadRequest, unsafeReason.StatusCode);
        using var badProgress = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/progress", agent.CredentialId, agent.Secret,
            body: new BackupTaskProgressRequest { LeaseToken = token, Stage = "BackingUp", Percent = 101 });
        Assert.Equal(HttpStatusCode.BadRequest, badProgress.StatusCode);
        using var cancel = await setup.Admin.SendAsync(HttpMethod.Post, root + $"/backup-runs/{runId}/cancel");
        using var renew = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/lease", agent.CredentialId, agent.Secret, body: new BackupTaskLeaseRequest { LeaseToken = token });
        Assert.True(AgentWorld.ReadJson(renew).GetProperty("cancellationRequested").GetBoolean());
        using var complete = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/complete", agent.CredentialId, agent.Secret,
            body: new BackupTaskCompletionRequest { LeaseToken = token, Status = "Cancelled", FailureCode = "Cancelled" });
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        using var detail = await setup.Admin.GetAsync(root + $"/backup-runs/{runId}");
        Assert.Equal("Cancelled", AgentWorld.ReadJson(detail).GetProperty("status").GetString());
    }

    [Fact]
    public async Task TaskMutationsRequireAgentAuthenticationAndProtocol()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        await world.EntitleAsync(setup.Tenant);
        var agent = await world.EnrollAsync(setup);
        var path = $"/api/agent/tasks/{Guid.NewGuid()}/lease";
        var body = new BackupTaskLeaseRequest { LeaseToken = Guid.NewGuid() };
        using var anonymous = await world.Anonymous.PostAsJsonAsync(path, body);
        using var browser = await setup.Admin.SendAsync(HttpMethod.Post, path, body);
        using var noProtocol = await world.SendAgentAsync(HttpMethod.Post, path, agent.CredentialId, agent.Secret, protocol: null, body: body);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, browser.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noProtocol.StatusCode);
    }

    [Fact]
    public async Task PauseReleasesDatabaseWorkAndResumeFencesOldCallbacksAndRestoresDestinationCheckpoint()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var other = await world.CreateTenantAsync();
        await world.EntitleAsync(setup.Tenant);
        var agent = await world.DeclareBackupCapabilityAsync(await world.EnrollAsync(setup));
        var root = $"/api/tenants/{setup.Tenant.Id.Value}";
        using var create = await setup.Admin.SendAsync(HttpMethod.Post, root + "/backup-jobs", Job(agent.AgentId));
        var jobId = AgentWorld.ReadJson(create).GetProperty("id").GetGuid();
        using var run = await StartAsync(setup.Admin, root, jobId, "durable-pause");
        var runId = AgentWorld.ReadJson(run).GetProperty("id").GetGuid();
        using var poll = await world.PollAsync(agent);
        var task = Assert.Single(AgentWorld.ReadJson(poll).GetProperty("tasks").EnumerateArray()).GetProperty("backup")
            .Deserialize<BackupTaskSnapshot>(JsonSerializerOptions.Web)!;
        var taskRoot = $"/api/agent/tasks/{task.AttemptId}";
        using var started = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/start", agent.CredentialId, agent.Secret,
            body: new BackupTaskStartRequest { LeaseToken = task.LeaseToken, SoftwareVersion = "1.2.3", ProtocolVersion = "1.0", Platform = "linux" });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var destination = Assert.Single(task.Configuration.Destinations);
        var progress = new BackupTaskProgressRequest { LeaseToken = task.LeaseToken, Stage = "Uploading",
            Checkpoint = new TransferCheckpointContract { DestinationId = destination.StorageConnectionId!.Value, StorageProvider = destination.StorageProvider,
                VerifiedByteOffset = 123, ProviderState = "provider-session" } };
        using var checkpoint = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/progress", agent.CredentialId, agent.Secret, body: progress);
        Assert.Equal(HttpStatusCode.NoContent, checkpoint.StatusCode);
        var viewer = await world.SeedUserAsync();
        await world.GrantAsync(setup.Tenant, viewer, TenantRole.Viewer);
        var viewerSession = await world.SignInAsync(viewer);
        using var denied = await viewerSession.SendAsync(HttpMethod.Post, root + $"/backup-runs/{runId}/pause");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var hidden = await other.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{other.Tenant.Id.Value}/backup-runs/{runId}/pause");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var pause = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/backups/runs/{runId}/pause").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        // The operator request returns before the provider flush: another mutation of that attempt must not wait on a held transaction.
        progress = progress with { Checkpoint = progress.Checkpoint! with { VerifiedByteOffset = 456 } };
        using var flushed = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/progress", agent.CredentialId, agent.Secret, body: progress).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.NoContent, flushed.StatusCode);
        using var renewed = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/lease", agent.CredentialId, agent.Secret, body: new BackupTaskLeaseRequest { LeaseToken = task.LeaseToken });
        Assert.True(AgentWorld.ReadJson(renewed).GetProperty("pauseRequested").GetBoolean());
        using var paused = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/complete", agent.CredentialId, agent.Secret,
            body: new BackupTaskCompletionRequest { LeaseToken = task.LeaseToken, Status = "Paused", FailureCode = "Operator" });
        Assert.Equal(HttpStatusCode.NoContent, paused.StatusCode);
        using var detail = await setup.Admin.GetAsync(root + $"/backup-runs/{runId}");
        Assert.Equal("Paused", AgentWorld.ReadJson(detail).GetProperty("status").GetString());
        using var pinned = await world.SendAgentAsync(HttpMethod.Get, taskRoot + "/retained-transfer-release", agent.CredentialId, agent.Secret);
        Assert.False(AgentWorld.ReadJson(pinned).GetBoolean());
        var foreignAgent = await world.EnrollAsync(other);
        using var foreignCleanup = await world.SendAgentAsync(HttpMethod.Get, taskRoot + "/retained-transfer-release", foreignAgent.CredentialId, foreignAgent.Secret);
        Assert.Equal(HttpStatusCode.NotFound, foreignCleanup.StatusCode);
        Assert.DoesNotContain("provider-session", await detail.Content.ReadAsStringAsync());
        using var resume = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/backups/runs/{runId}/resume");
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        using var repoll = await world.PollAsync(agent);
        var resumed = Assert.Single(AgentWorld.ReadJson(repoll).GetProperty("tasks").EnumerateArray()).GetProperty("backup")
            .Deserialize<BackupTaskSnapshot>(JsonSerializerOptions.Web)!;
        Assert.Equal(task.AttemptId, resumed.AttemptId);
        Assert.NotEqual(task.LeaseToken, resumed.LeaseToken);
        Assert.True(resumed.ResumeTransfer);
        Assert.Equal(456, Assert.Single(resumed.Checkpoints).VerifiedByteOffset);
        using var stale = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/progress", agent.CredentialId, agent.Secret, body: progress);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var staleCompletion = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/complete", agent.CredentialId, agent.Secret,
            body: new BackupTaskCompletionRequest { LeaseToken = task.LeaseToken, Status = "Paused", FailureCode = "PausedByNetworkError" });
        Assert.Equal(HttpStatusCode.Conflict, staleCompletion.StatusCode);
        using var repaused = await world.SendAgentAsync(HttpMethod.Post, taskRoot + "/complete", agent.CredentialId, agent.Secret,
            body: new BackupTaskCompletionRequest { LeaseToken = resumed.LeaseToken, Status = "Paused", FailureCode = "Operator" });
        Assert.Equal(HttpStatusCode.NoContent, repaused.StatusCode);
        using var cancelled = await setup.Admin.SendAsync(HttpMethod.Post, root + $"/backup-runs/{runId}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        using var released = await world.SendAgentAsync(HttpMethod.Get, taskRoot + "/retained-transfer-release", agent.CredentialId, agent.Secret);
        Assert.True(AgentWorld.ReadJson(released).GetBoolean());
    }

    [Fact]
    public async Task File_backups_require_engine_support_at_admission_and_again_when_claimed()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        await world.EntitleAsync(setup.Tenant);
        var agent = await world.EnrollAsync(setup);
        var legacy = AgentWorld.CapabilitySnapshot(AgentWorld.CapabilityReport(BackupTaskProtocol.MultiDestinationCapability));
        using (var sync = await world.SyncCapabilitiesAsync(agent, legacy)) Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        var root = $"/api/tenants/{setup.Tenant.Id.Value}";
        var job = Job(agent.AgentId);
        using var created = await setup.Admin.SendAsync(HttpMethod.Post, root + "/backup-jobs", job with
        {
            Configuration = job.Configuration with { Engine = "file-system", Database = "documents", Host = "localhost", Files = new BackupFileSelection() }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        using var refused = await StartAsync(setup.Admin, root, jobId, "files-unsupported");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("agent_engine_unsupported", AgentWorld.ReadJson(refused).GetProperty("code").GetString());
        var capable = AgentWorld.CapabilitySnapshot(AgentWorld.CapabilityReport(BackupTaskProtocol.MultiDestinationCapability),
            AgentWorld.CapabilityReport("file-system.backup.full"));
        using (var sync = await world.SyncCapabilitiesAsync(agent, capable)) Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        using var started = await StartAsync(setup.Admin, root, jobId, "files-supported");
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using (var sync = await world.SyncCapabilitiesAsync(agent, legacy)) Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        using var held = await world.PollAsync(agent);
        Assert.Empty(AgentWorld.ReadJson(held).GetProperty("tasks").EnumerateArray());
        using (var sync = await world.SyncCapabilitiesAsync(agent, capable)) Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        using var poll = await world.PollAsync(agent);
        var task = Assert.Single(AgentWorld.ReadJson(poll).GetProperty("tasks").EnumerateArray()).GetProperty("backup");
        Assert.Equal("file-system", task.GetProperty("configuration").GetProperty("engine").GetString());
    }

    private static CreateBackupJobRequestBody Job(Guid agentId) => new("Manual full backup", agentId, new BackupExecutionConfiguration
    {
        Database = "master", Host = "sql-server", IntegratedSecurity = true, Destinations = BackupTestDestinations.LocalBackend
    });
    private sealed record CreateBackupJobRequestBody(string Name, Guid AgentId, BackupExecutionConfiguration Configuration);
    private static async Task<HttpResponseMessage> StartAsync(BrowserSession session, string root, Guid jobId, string key)
    {
        using var csrf = await session.Client.GetAsync("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, root + $"/backup-jobs/{jobId}/runs");
        request.Headers.Add("X-CSRF-TOKEN", AgentWorld.ReadJson(csrf).GetProperty("requestToken").GetString());
        request.Headers.Add("Idempotency-Key", key);
        return await session.Client.SendAsync(request);
    }
}
