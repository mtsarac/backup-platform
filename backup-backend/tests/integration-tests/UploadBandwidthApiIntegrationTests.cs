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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    private const long UploadBandwidth_Min = BackupPlatform.Domain.Backups.UploadBandwidth.MinimumBytesPerSecond;

    [Theory]
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
    public async Task The_relay_widens_the_provider_deadline_by_the_throttled_time_and_never_throttles_again()
    {
        var provider = new CapturingProvider();
        await using var world = await RelayWorldAsync(provider, kestrel: false);
        var run = await StartRelayRunAsync(world);
        var body = new byte[1_000_000];

        using (var unlimited = await world.Anonymous.SendAsync(UploadRequest(run, new ByteArrayContent(body)))) Assert.Equal(HttpStatusCode.OK, unlimited.StatusCode);
        Assert.Equal(TimeSpan.Zero, provider.Options.Single().TransferAllowance);

        using (await run.Setup.Admin.SendAsync(HttpMethod.Put, run.Root + $"/upload-bandwidth/jobs/{run.JobId}", new { bytesPerSecond = 16_000 })) { }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using (var limited = await world.Anonymous.SendAsync(UploadRequest(run, new ByteArrayContent(body)))) Assert.Equal(HttpStatusCode.OK, limited.StatusCode);

        // 1 MB at the 16 kB/s floor: a minute and a quarter that the provider's own total-transfer limit must not count against the upload.
        Assert.Equal(TimeSpan.FromSeconds(62.5), provider.Options[^1].TransferAllowance);
        Assert.Equal(1_000_000, provider.Received[^1]);
        // The relay only reads what the agent sends: it adds no delay of its own.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the relay held a limited upload for {watch.Elapsed}");
    }

    // Real sockets and real Kestrel: the agent paces its stream at the lowest allowed limit for longer than Kestrel's 5 s minimum-data-rate grace period;
    // the relay must neither reject the slow body nor alter a byte of it.
    [Fact]
    public async Task A_stream_paced_to_the_lowest_limit_crosses_real_kestrel_with_the_bytes_intact_and_at_the_limit()
    {
        const long rate = 16_000;
        var provider = new CapturingProvider();
        await using var world = await RelayWorldAsync(provider, kestrel: true);
        var run = await StartRelayRunAsync(world);
        var body = Enumerable.Range(0, 96_000).Select(i => (byte)(i * 31 % 251)).ToArray();
        var limiter = new StorageProviders.Abstractions.Transfer.TransferRateLimiter(rate);
        var content = new StreamContent(new StorageProviders.Abstractions.Transfer.ThrottledStream(new MemoryStream(body), limiter), 64 * 1024);
        content.Headers.ContentLength = body.Length;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await world.Anonymous.SendAsync(UploadRequest(run, content));
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body.LongLength, provider.Received.Single());
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(body), provider.Hashes.Single());
        var floor = (body.Length - StorageProviders.Abstractions.Transfer.TransferRateLimiter.Capacity(rate)) / rate;
        Assert.True(watch.Elapsed.TotalSeconds >= floor - 0.1, $"{body.Length} bytes crossed in {watch.Elapsed}, faster than the {rate} B/s ceiling allows ({floor:F1} s)");
        Assert.True(watch.Elapsed.TotalSeconds < floor * 3, $"{watch.Elapsed} is far slower than the ceiling");
        Assert.True(watch.Elapsed > TimeSpan.FromSeconds(5), "the transfer must outlast Kestrel's minimum-data-rate grace period to prove anything");
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
