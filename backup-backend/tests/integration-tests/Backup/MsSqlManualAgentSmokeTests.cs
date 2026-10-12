using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace BackupPlatform.IntegrationTests.Backup;

/// <summary>Real HTTPS API, real published Agent.Host and real SQL Server. Missing runtime configuration fails explicitly.</summary>
public sealed class MsSqlManualAgentSmokeTests(PostgreSqlFixture database, ITestOutputHelper output) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task ManualRunExecutesOnceUnderDuplicatePushAndPollAndPersistsVerifiedArtifact()
    {
        await using var sql = new OrchestrationSqlSource();
        await sql.InitializeAsync();
        await using var world = await AgentWorld.CreateAsync(database, clock: new MutableClock(live: true), kestrel: true);
        var tenant = await world.CreateTenantAsync();
        await world.EntitleAsync(tenant.Tenant);
        var agent = await world.EnrollAsync(tenant);
        var configuration = Configuration(sql, sql.DatabaseName);
        var job = await CreateJobAsync(tenant, agent, configuration);
        var run = await StartRunAsync(tenant, job, "one-logical-execution");
        var runId = run.GetProperty("id").GetGuid();
        Assert.Equal("Pending", run.GetProperty("status").GetString());
        var duplicate = await StartRunAsync(tenant, job, "one-logical-execution");
        Assert.Equal(runId, duplicate.GetProperty("id").GetGuid());
        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);
        var dispatcher = world.Factory.Services.GetRequiredService<IAgentPushDispatcher>();
        var sawRunning = false;
        var sawDuplicatePoll = false;
        var deliveredDuplicatePush = false;
        var seen = new HashSet<string> { "Pending" };
        var detail = await WaitForTerminalAsync(tenant, runId, host, async current =>
        {
            var status = current.GetProperty("status").GetString()!;
            seen.Add(status);
            if (status != "Running") return;
            sawRunning = true;
            if (!deliveredDuplicatePush)
                for (var index = 0; index < 3; index++)
                    deliveredDuplicatePush |= await dispatcher.TrySendPollHintAsync(new AgentId(agent.AgentId), "WorkAvailable", CancellationToken.None);
            if (sawDuplicatePoll) return;
            using var poll = await world.PollAsync(agent);
            Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
            Assert.Empty(AgentWorld.ReadJson(poll).GetProperty("tasks").EnumerateArray());
            sawDuplicatePoll = true;
        });
        Assert.True(sawRunning, "The real attempt must be observed Running before its terminal result.");
        Assert.True(sawDuplicatePoll, "A competing authoritative poll was exercised during execution.");
        Assert.True(deliveredDuplicatePush, "Duplicate hints must actually traverse the connected SignalR channel.");
        Assert.Equal("Succeeded", detail.GetProperty("status").GetString());
        var attempt = Assert.Single(detail.GetProperty("attempts").EnumerateArray());
        Assert.Equal("Succeeded", attempt.GetProperty("status").GetString());
        var artifact = Assert.Single(attempt.GetProperty("artifacts").EnumerateArray());
        var replica = Assert.Single(artifact.GetProperty("replicas").EnumerateArray());
        Assert.Equal("Verified", replica.GetProperty("verificationState").GetString());
        var physical = Path.Combine(host.StorageDirectory, replica.GetProperty("key").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        await using var stored = File.OpenRead(physical);
        var checksum = Convert.ToHexStringLower(await SHA256.HashDataAsync(stored));
        Assert.Equal(checksum, artifact.GetProperty("sourceChecksum").GetString());
        Assert.Equal(checksum, artifact.GetProperty("artifactChecksum").GetString());
        Assert.Equal(checksum, replica.GetProperty("checksum").GetString());
        Assert.Equal(stored.Length, artifact.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(1, await sql.FullBackupCountAsync());
        var terminalDuplicate = await StartRunAsync(tenant, job, "one-logical-execution");
        Assert.Equal(runId, terminalDuplicate.GetProperty("id").GetGuid());
        Assert.Equal("Succeeded", terminalDuplicate.GetProperty("status").GetString());
        output.WriteLine("Actual Agent.Host + SQL Server: {0}; one SQL full backup, independent SHA256 matches source/artifact/replica; duplicate hints/poll/start did not execute twice.", string.Join(" -> ", seen));
    }

    [Fact]
    public async Task InvalidDatabaseFailsRealAttemptWithoutPublishingArtifact()
    {
        await using var sql = new OrchestrationSqlSource();
        await using var world = await AgentWorld.CreateAsync(database, clock: new MutableClock(live: true), kestrel: true);
        var tenant = await world.CreateTenantAsync();
        await world.EntitleAsync(tenant.Tenant);
        var agent = await world.EnrollAsync(tenant);
        var missingDatabase = "bp_absent_" + Guid.NewGuid().ToString("N");
        var job = await CreateJobAsync(tenant, agent, Configuration(sql, missingDatabase));
        var run = await StartRunAsync(tenant, job, "real-precheck-failure");
        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);
        var detail = await WaitForTerminalAsync(tenant, run.GetProperty("id").GetGuid(), host);
        Assert.Equal("Failed", detail.GetProperty("status").GetString());
        var attempt = Assert.Single(detail.GetProperty("attempts").EnumerateArray());
        Assert.Equal("Failed", attempt.GetProperty("status").GetString());
        Assert.Equal("DatabaseNotFound", attempt.GetProperty("reasonCode").GetString());
        Assert.False(attempt.GetProperty("retryable").GetBoolean());
        Assert.Equal("NotRetryable", detail.GetProperty("retry").GetProperty("state").GetString());
        Assert.Empty(attempt.GetProperty("artifacts").EnumerateArray());
        Assert.Empty(Directory.GetFiles(host.StorageDirectory, "*", SearchOption.AllDirectories));
        output.WriteLine("Actual invalid database: Run Failed (NotRetryable) -> Attempt Failed / DatabaseNotFound; no artifact or storage output.");
    }

    internal static BackupExecutionConfiguration Configuration(OrchestrationSqlSource sql, string databaseName) => new()
    {
        Host = sql.Host, Port = sql.Port, Database = databaseName,
        DatabaseCredentialReference = "sql_smoke", TrustServerCertificate = true,
        StagingReference = "default", Destinations = BackupTestDestinations.LocalAgent, StoragePrefix = "manual",
        CopyOnly = true, ChecksumPolicy = "Required", CompressionPolicy = "Preferred"
    };

    private static async Task<Guid> CreateJobAsync(TenantSetup tenant, EnrolledAgent agent, BackupExecutionConfiguration configuration)
    {
        using var response = await tenant.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant.Tenant.Id.Value}/backup-jobs",
            new { name = "Real manual SQL backup", agentId = agent.AgentId, configuration });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return AgentWorld.ReadJson(response).GetProperty("id").GetGuid();
    }

    internal static async Task<JsonElement> StartRunAsync(TenantSetup tenant, Guid jobId, string key)
    {
        using var tokenResponse = await tenant.Admin.GetAsync("/api/auth/antiforgery");
        var token = AgentWorld.ReadJson(tokenResponse).GetProperty("requestToken").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tenants/{tenant.Tenant.Id.Value}/backup-jobs/{jobId}/runs");
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.Add("Idempotency-Key", key);
        using var response = await tenant.Admin.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private static async Task<JsonElement> WaitForTerminalAsync(TenantSetup tenant, Guid runId, AgentHostSmokeProcess host,
        Func<JsonElement, Task>? observe = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (!timeout.IsCancellationRequested)
        {
            host.AssertAlive();
            using var response = await tenant.Admin.GetAsync($"/api/tenants/{tenant.Tenant.Id.Value}/backup-runs/{runId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var detail = AgentWorld.ReadJson(response);
            if (observe is not null) await observe(detail);
            if (detail.GetProperty("status").GetString() is "Succeeded" or "Failed" or "Cancelled" or "AgentLost") return detail;
            await Task.Delay(25, timeout.Token);
        }
        throw new TimeoutException("Actual Agent.Host did not report a terminal attempt.");
    }
}
