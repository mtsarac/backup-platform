using System.Net;
using System.Text.Json;
using BackupPlatform.Api.Backups;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

/// <summary>What an agent receives from a poll: enough to call back for that attempt.</summary>
internal sealed record TaskClaim(Guid RunId, Guid AttemptId, int AttemptNumber, Guid LeaseToken);

/// <summary>
/// One tenant with an administrator and one enrolled agent on a real API host, driven only through HTTP (browser session for operators,
/// agent credentials for the agent) plus the two hosted-service entry points a test cannot wait for: the retry scan and the stale-lease sweep.
/// </summary>
internal sealed class BackupApiScenario : IAsyncDisposable
{
    private BackupApiScenario(AgentWorld world, TenantSetup setup, EnrolledAgent agent)
    {
        World = world;
        Setup = setup;
        Agent = agent;
    }

    public AgentWorld World { get; }
    public TenantSetup Setup { get; }
    public EnrolledAgent Agent { get; }
    public BrowserSession Admin => Setup.Admin;
    public string Root => RootOf(Setup);
    public MutableClock Clock => World.Clock;

    public static string RootOf(TenantSetup setup) => $"/api/tenants/{setup.Tenant.Id.Value}";

    public static async Task<BackupApiScenario> CreateAsync(PostgreSqlFixture database, IReadOnlyDictionary<string, string?>? configuration = null)
    {
        // Polling is rate limited per agent per minute; scenarios that loop must not trip it.
        var world = await AgentWorld.CreateAsync(database, limits: new AgentRateLimitOptions { PollPerAgent = 1000 }, configuration: configuration);
        var setup = await world.CreateTenantAsync();
        return new BackupApiScenario(world, setup, await world.EnrollAsync(setup));
    }

    public ValueTask DisposeAsync() => World.DisposeAsync();

    public async Task<BrowserSession> SessionAsync(TenantRole role)
    {
        var account = await World.SeedUserAsync();
        await World.GrantAsync(Setup.Tenant, account, role);
        return await World.SignInAsync(account);
    }

    // ---- jobs and runs (browser side) ----

    public static object JobBody(Guid agentId, object? retryPolicy = null, string? overlapPolicy = null) => new
    {
        name = "Nightly full backup",
        agentId,
        configuration = new BackupExecutionConfiguration { Database = "master", Host = "sql-server", IntegratedSecurity = true },
        retryPolicy,
        overlapPolicy
    };

    public Task<HttpResponseMessage> CreateJobRawAsync(object body, BrowserSession? session = null) =>
        (session ?? Admin).SendAsync(HttpMethod.Post, Root + "/backup-jobs", body);

    public async Task<Guid> CreateJobAsync(object? retryPolicy = null, string? overlapPolicy = null, EnrolledAgent? agent = null)
    {
        using var response = await CreateJobRawAsync(JobBody((agent ?? Agent).AgentId, retryPolicy, overlapPolicy));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return AgentWorld.ReadJson(response).GetProperty("id").GetGuid();
    }

    public async Task<HttpResponseMessage> StartRawAsync(Guid jobId, string key, BrowserSession? session = null)
    {
        var browser = session ?? Admin;
        using var csrf = await browser.Client.GetAsync("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, Root + $"/backup-jobs/{jobId}/runs");
        request.Headers.Add("X-CSRF-TOKEN", AgentWorld.ReadJson(csrf).GetProperty("requestToken").GetString());
        request.Headers.Add("Idempotency-Key", key);
        return await browser.Client.SendAsync(request);
    }

    public async Task<Guid> StartRunAsync(Guid jobId, string? key = null)
    {
        using var response = await StartRawAsync(jobId, key ?? Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response).GetProperty("id").GetGuid();
    }

    public async Task<JsonElement> RunAsync(Guid runId)
    {
        using var response = await Admin.GetAsync(Root + $"/backup-runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    public Task<HttpResponseMessage> RetryRawAsync(Guid runId, BrowserSession? session = null) =>
        (session ?? Admin).SendAsync(HttpMethod.Post, Root + $"/backup-runs/{runId}/retry");

    public Task<HttpResponseMessage> CancelRawAsync(Guid runId) =>
        Admin.SendAsync(HttpMethod.Post, Root + $"/backup-runs/{runId}/cancel");

    // ---- the agent side ----

    public async Task<TaskClaim?> PollAsync(EnrolledAgent? agent = null)
    {
        using var response = await World.PollAsync(agent ?? Agent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tasks = AgentWorld.ReadJson(response).GetProperty("tasks").EnumerateArray().ToList();
        if (tasks.Count == 0) return null;
        var backup = Assert.Single(tasks).GetProperty("backup");
        return new TaskClaim(backup.GetProperty("runId").GetGuid(), backup.GetProperty("attemptId").GetGuid(),
            backup.GetProperty("attemptNumber").GetInt32(), backup.GetProperty("leaseToken").GetGuid());
    }

    public async Task<TaskClaim> ClaimAsync(EnrolledAgent? agent = null) =>
        await PollAsync(agent) ?? throw new Xunit.Sdk.XunitException("The agent was expected to receive a task but the poll was empty.");

    public Task<HttpResponseMessage> AgentCallAsync(TaskClaim claim, string action, object body, EnrolledAgent? agent = null)
    {
        var caller = agent ?? Agent;
        return World.SendAgentAsync(HttpMethod.Post, $"/api/agent/tasks/{claim.AttemptId}/{action}", caller.CredentialId, caller.Secret, body: body);
    }

    public Task<HttpResponseMessage> StartAttemptRawAsync(TaskClaim claim, Guid? token = null) => AgentCallAsync(claim, "start",
        new BackupTaskStartRequest { LeaseToken = token ?? claim.LeaseToken, SoftwareVersion = "1.2.3", ProtocolVersion = "1.0", Platform = "linux" });

    public Task<HttpResponseMessage> RenewRawAsync(TaskClaim claim, Guid? token = null) =>
        AgentCallAsync(claim, "lease", new BackupTaskLeaseRequest { LeaseToken = token ?? claim.LeaseToken });

    public Task<HttpResponseMessage> ProgressRawAsync(TaskClaim claim, Guid? token = null) =>
        AgentCallAsync(claim, "progress", new BackupTaskProgressRequest { LeaseToken = token ?? claim.LeaseToken, Stage = "BackingUp", Percent = 10 });

    public Task<HttpResponseMessage> CompleteRawAsync(TaskClaim claim, BackupTaskCompletionRequest request) => AgentCallAsync(claim, "complete", request);

    public Task<HttpResponseMessage> FailRawAsync(TaskClaim claim, string code, Guid? token = null) =>
        CompleteRawAsync(claim, new BackupTaskCompletionRequest { LeaseToken = token ?? claim.LeaseToken, Status = "Failed", FailureCode = code });

    public async Task StartAttemptAsync(TaskClaim claim)
    {
        using var response = await StartAttemptRawAsync(claim);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Claims, starts and fails the next task the way an agent does, and returns the claim.</summary>
    public async Task<TaskClaim> FailNextAsync(string code)
    {
        var claim = await ClaimAsync();
        await StartAttemptAsync(claim);
        using var response = await FailRawAsync(claim, code);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return claim;
    }

    public async Task<TaskClaim> SucceedNextAsync()
    {
        var claim = await ClaimAsync();
        await StartAttemptAsync(claim);
        using var response = await CompleteRawAsync(claim, SuccessRequest(claim));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return claim;
    }

    /// <summary>A completion that passes the backend's artifact validation for the job created by <see cref="JobBody"/>.</summary>
    public BackupTaskCompletionRequest SuccessRequest(TaskClaim claim, Guid? token = null)
    {
        var now = Clock.GetUtcNow();
        var checksum = new string('b', 64);
        return new BackupTaskCompletionRequest
        {
            LeaseToken = token ?? claim.LeaseToken,
            Status = "Succeeded",
            Artifact = new BackupTaskArtifact
            {
                SizeBytes = 1024,
                SourceChecksum = new string('a', 64),
                ArtifactChecksum = checksum,
                CreatedAt = now,
                SourceMetadataJson = JsonSerializer.Serialize(new
                {
                    engine = "mssql",
                    databaseName = "master",
                    backupType = "Full",
                    backupStartedAt = now.AddSeconds(-10),
                    backupFinishedAt = now.AddSeconds(-5),
                    backupVerification = new { state = "Passed", method = "VerifyOnly" }
                }),
                AppliedOptionsJson = "{\"copyOnly\":true,\"checksum\":true,\"compression\":true}",
                Replicas =
                [
                    new BackupTaskReplica
                    {
                        StorageProvider = "local", StorageTarget = "default", Key = $"{claim.AttemptId:N}/backup.bak", SizeBytes = 1024,
                        Checksum = checksum, VerificationState = "Verified", VerificationMethod = "ReadBack", CreatedAt = now, VerifiedAt = now
                    }
                ]
            }
        };
    }

    // ---- time-driven hosted services, driven directly ----

    /// <summary>One retry scan. The host's own worker may have started the retry first; assert on the run, not on this count.</summary>
    public Task<int> ScanRetriesAsync() => World.Factory.Services.GetRequiredService<BackupRetryWorker>().ScanAsync(CancellationToken.None);

    /// <summary>The stale-lease sweep at the scenario clock's current time (the host's own detector may have run it already).</summary>
    public async Task ExpireStaleAsync()
    {
        await using var scope = World.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IBackupRunStore>().ExpireStaleAsync(Clock.GetUtcNow(), CancellationToken.None);
    }

    // ---- reading run views ----

    public static IReadOnlyList<JsonElement> AttemptsOf(JsonElement run) => run.GetProperty("attempts").EnumerateArray().ToList();

    public static string StatusOf(JsonElement run) => run.GetProperty("status").GetString()!;
}
