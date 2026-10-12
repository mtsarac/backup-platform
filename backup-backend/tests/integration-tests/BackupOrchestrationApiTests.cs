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
