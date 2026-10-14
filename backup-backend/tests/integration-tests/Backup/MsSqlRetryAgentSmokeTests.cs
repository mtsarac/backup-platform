using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Contracts.Backups;
using Xunit.Abstractions;

namespace BackupPlatform.IntegrationTests.Backup;

/// <summary>
/// Retry and cancellation against the real backend, the real published Agent.Host and a real SQL Server. Transient failures are produced
/// by pointing the job at a <see cref="TcpForwarder"/> whose port is closed (connection refused) until the test opens it: the real agent
/// pre-check fails for real and the real agent classifies it. Missing runtime configuration fails explicitly (see MsSqlManualAgentSmokeTests).
/// </summary>
public sealed class MsSqlRetryAgentSmokeTests(PostgreSqlFixture database, ITestOutputHelper output) : IClassFixture<PostgreSqlFixture>
{
    private static readonly TimeSpan BackoffSeconds = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task TransientFailureRetriesUnderTheSameRunAndSucceedsWithExactlyOneStoredBackup()
    {
        await using var sql = new OrchestrationSqlSource();
        await sql.InitializeAsync();
        await using var forwarder = new TcpForwarder(sql.Host, sql.Port ?? 1433);
        await using var world = await AgentWorld.CreateAsync(database, clock: new MutableClock(live: true), kestrel: true);
        var tenant = await world.CreateTenantAsync();
        await world.EntitleAsync(tenant.Tenant);
        var agent = await world.DeclareBackupCapabilityAsync(await world.EnrollAsync(tenant));
        var job = await CreateJobAsync(tenant, agent, Configuration(sql, forwarder, sql.DatabaseName), maxAttempts: 3, initialDelaySeconds: 3, overlap: "Queue");
        var run = await MsSqlManualAgentSmokeTests.StartRunAsync(tenant, job, "retry-success");
        var runId = run.GetProperty("id").GetGuid();
        var trace = new Trace();
        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);

        // Attempt 1 hits a closed port: a real, retryable connectivity failure that schedules a retry instead of ending the run.
        var scheduled = await WaitAsync(tenant, runId, host, trace, detail => Retry(detail).GetProperty("state").GetString() == "Scheduled");
        Assert.Equal("RetryPending", scheduled.GetProperty("status").GetString());
        var first = Assert.Single(Attempts(scheduled));
        Assert.Equal(1, first.GetProperty("number").GetInt32());
        Assert.Equal("Failed", first.GetProperty("status").GetString());
        Assert.Equal("DatabaseUnavailable", first.GetProperty("reasonCode").GetString());
        Assert.True(first.GetProperty("retryable").GetBoolean());
        Assert.Empty(first.GetProperty("artifacts").EnumerateArray());
        var retry = Retry(scheduled);
        Assert.Equal(1, retry.GetProperty("attemptsUsed").GetInt32());
        Assert.Equal(3, retry.GetProperty("maxAttempts").GetInt32());
        Assert.Equal("DatabaseUnavailable", retry.GetProperty("lastFailureCode").GetString());
        Assert.True(retry.GetProperty("lastFailureRetryable").GetBoolean());
        var nextAttemptAt = retry.GetProperty("nextAttemptAt").GetDateTimeOffset();
        Assert.InRange((nextAttemptAt - first.GetProperty("completedAt").GetDateTimeOffset()).TotalSeconds,
            BackoffSeconds.TotalSeconds - 0.01, BackoffSeconds.TotalSeconds + 0.01);

        // The outage ends during the backoff window; the scheduled retry must now succeed.
        forwarder.Start();
        var detail = await WaitAsync(tenant, runId, host, trace, IsTerminal);
        Assert.Equal("Succeeded", detail.GetProperty("status").GetString());
        Assert.Equal(runId, detail.GetProperty("id").GetGuid());
        var attempts = Attempts(detail);
        Assert.Equal(new[] { 1, 2 }, attempts.Select(a => a.GetProperty("number").GetInt32()));
        Assert.Equal("Failed", attempts[0].GetProperty("status").GetString());
        Assert.Empty(attempts[0].GetProperty("artifacts").EnumerateArray());
        Assert.Equal("Succeeded", attempts[1].GetProperty("status").GetString());
        Assert.True(attempts[1].GetProperty("createdAt").GetDateTimeOffset() >= nextAttemptAt, "The retry must not start before its backoff elapsed.");
        var artifact = Assert.Single(attempts[1].GetProperty("artifacts").EnumerateArray());
        var replica = Assert.Single(artifact.GetProperty("replicas").EnumerateArray());
        Assert.Equal("Verified", replica.GetProperty("verificationState").GetString());
        var physical = Path.Combine(host.StorageDirectory, replica.GetProperty("key").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        await using var stored = File.OpenRead(physical);
        var checksum = Convert.ToHexStringLower(await SHA256.HashDataAsync(stored));
        Assert.Equal(checksum, artifact.GetProperty("sourceChecksum").GetString());
        Assert.Equal(checksum, artifact.GetProperty("artifactChecksum").GetString());
        Assert.Equal(checksum, replica.GetProperty("checksum").GetString());
        Assert.Equal(stored.Length, artifact.GetProperty("sizeBytes").GetInt64());
        Assert.Single(Directory.GetFiles(host.StorageDirectory, "*", SearchOption.AllDirectories));
        Assert.Equal(1, await sql.FullBackupCountAsync());
        Assert.Equal(2, Retry(detail).GetProperty("attemptsUsed").GetInt32());
        Assert.Equal(3, Retry(detail).GetProperty("maxAttempts").GetInt32());
        output.WriteLine("Retry success: {0}; attempts [1 Failed/DatabaseUnavailable(retryable), 2 Succeeded]; one SQL full backup; SHA256 matches source/artifact/replica.", trace);
    }

    [Fact]
    public async Task PersistentTransientFailureExhaustsTheAttemptBudgetAndNeverCreatesAnotherAttempt()
    {
        await using var sql = new OrchestrationSqlSource();
        await sql.InitializeAsync();
        await using var forwarder = new TcpForwarder(sql.Host, sql.Port ?? 1433); // never started: every attempt is refused
        await using var world = await AgentWorld.CreateAsync(database, clock: new MutableClock(live: true), kestrel: true);
        var tenant = await world.CreateTenantAsync();
        await world.EntitleAsync(tenant.Tenant);
        var agent = await world.DeclareBackupCapabilityAsync(await world.EnrollAsync(tenant));
        var job = await CreateJobAsync(tenant, agent, Configuration(sql, forwarder, sql.DatabaseName), maxAttempts: 2, initialDelaySeconds: 3, overlap: "Queue");
        var run = await MsSqlManualAgentSmokeTests.StartRunAsync(tenant, job, "retry-exhaustion");
        var runId = run.GetProperty("id").GetGuid();
        var trace = new Trace();
        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);

        var scheduled = await WaitAsync(tenant, runId, host, trace, detail => Retry(detail).GetProperty("state").GetString() == "Scheduled");
        Assert.Equal("RetryPending", scheduled.GetProperty("status").GetString());
        var nextAttemptAt = Retry(scheduled).GetProperty("nextAttemptAt").GetDateTimeOffset();
        var detail = await WaitAsync(tenant, runId, host, trace, IsTerminal);

        Assert.Equal("Failed", detail.GetProperty("status").GetString());
        Assert.Equal("Exhausted", Retry(detail).GetProperty("state").GetString());
        Assert.Equal(2, Retry(detail).GetProperty("attemptsUsed").GetInt32());
        Assert.Equal(2, Retry(detail).GetProperty("maxAttempts").GetInt32());
        Assert.Equal("DatabaseUnavailable", Retry(detail).GetProperty("lastFailureCode").GetString());
        Assert.True(Retry(detail).GetProperty("lastFailureRetryable").GetBoolean(), "The failure is retryable; it is the budget that ran out.");
        Assert.Equal(JsonValueKind.Null, Retry(detail).GetProperty("nextAttemptAt").ValueKind);
        var attempts = Attempts(detail);
        Assert.Equal(new[] { 1, 2 }, attempts.Select(a => a.GetProperty("number").GetInt32()));
        Assert.All(attempts, attempt =>
        {
            Assert.Equal("Failed", attempt.GetProperty("status").GetString());
            Assert.Equal("DatabaseUnavailable", attempt.GetProperty("reasonCode").GetString());
            Assert.Empty(attempt.GetProperty("artifacts").EnumerateArray());
        });
        Assert.True(attempts[1].GetProperty("createdAt").GetDateTimeOffset() >= nextAttemptAt);

        // Several retry-scan intervals (5s each by default) later nothing may have changed, and the agent has nothing to claim.
        await AssertStableAsync(tenant, runId, host, TimeSpan.FromSeconds(8), current =>
        {
            Assert.Equal("Failed", current.GetProperty("status").GetString());
            Assert.Equal("Exhausted", Retry(current).GetProperty("state").GetString());
            Assert.Equal(2, Attempts(current).Count);
        });
        await AssertNoTaskAsync(world, agent);
        using var refused = await tenant.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant.Tenant.Id.Value}/backup-runs/{runId}/retry");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var refusal = AgentWorld.ReadJson(refused);
        Assert.Equal("retry_not_allowed", refusal.GetProperty("error").GetString());
        Assert.Equal("attempts_exhausted", refusal.GetProperty("reason").GetString());
        Assert.Equal(2, Attempts(await GetRunAsync(tenant, runId)).Count);
        Assert.Empty(Directory.GetFiles(host.StorageDirectory, "*", SearchOption.AllDirectories));
        Assert.Equal(0, await sql.FullBackupCountAsync());
        output.WriteLine("Retry exhaustion: {0}; attempts [1 Failed, 2 Failed/DatabaseUnavailable]; stayed at 2 attempts for 8s; manual retry 409 attempts_exhausted; no artifact, no storage file, no SQL backup.", trace);
    }

    [Fact]
    public async Task CancellationDuringBackoffEndsTheRunImmediatelyAndNoFurtherAttemptIsCreated()
    {
        await using var sql = new OrchestrationSqlSource();
        await sql.InitializeAsync();
        await using var forwarder = new TcpForwarder(sql.Host, sql.Port ?? 1433);
        await using var world = await AgentWorld.CreateAsync(database, clock: new MutableClock(live: true), kestrel: true);
        var tenant = await world.CreateTenantAsync();
        await world.EntitleAsync(tenant.Tenant);
        var agent = await world.DeclareBackupCapabilityAsync(await world.EnrollAsync(tenant));
        var job = await CreateJobAsync(tenant, agent, Configuration(sql, forwarder, sql.DatabaseName), maxAttempts: 3, initialDelaySeconds: 60, maxDelaySeconds: 120, overlap: "Queue");
        var run = await MsSqlManualAgentSmokeTests.StartRunAsync(tenant, job, "cancel-during-backoff");
        var runId = run.GetProperty("id").GetGuid();
        var trace = new Trace();
        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);

        var scheduled = await WaitAsync(tenant, runId, host, trace, detail => Retry(detail).GetProperty("state").GetString() == "Scheduled");
        Assert.Equal("RetryPending", scheduled.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, Retry(scheduled).GetProperty("nextAttemptAt").ValueKind);

        using var cancel = await tenant.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant.Tenant.Id.Value}/backup-runs/{runId}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelled = AgentWorld.ReadJson(cancel);
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal("None", Retry(cancelled).GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, Retry(cancelled).GetProperty("nextAttemptAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, cancelled.GetProperty("completedAt").ValueKind);
        trace.Observe(cancelled);

        // The outage ends and well over a retry-scan interval passes: a cancelled run must never be revived by the scheduler or a poll.
        forwarder.Start();
        await AssertStableAsync(tenant, runId, host, TimeSpan.FromSeconds(8), current =>
        {
            Assert.Equal("Cancelled", current.GetProperty("status").GetString());
            Assert.Equal("None", Retry(current).GetProperty("state").GetString());
            var only = Assert.Single(Attempts(current));
            Assert.Equal("Failed", only.GetProperty("status").GetString());
            Assert.Equal("DatabaseUnavailable", only.GetProperty("reasonCode").GetString());
        });
        await AssertNoTaskAsync(world, agent);
        using var refused = await tenant.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant.Tenant.Id.Value}/backup-runs/{runId}/retry");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("cancelled", AgentWorld.ReadJson(refused).GetProperty("reason").GetString());
        Assert.Equal(0, await sql.FullBackupCountAsync());
        Assert.Empty(Directory.GetFiles(host.StorageDirectory, "*", SearchOption.AllDirectories));
        output.WriteLine("Cancel during backoff: {0}; cancel -> Cancelled at once (retry state None, no nextAttemptAt); still 1 attempt after 8s with the database reachable; no task claimable; manual retry 409 cancelled.", trace);
    }

    [Fact]
    public async Task ManualRetryOfNonRetryableFailureRunsANewAttemptOnTheSameRun()
    {
        await using var sql = new OrchestrationSqlSource(); // deliberately not initialised yet: the database does not exist
        await using var world = await AgentWorld.CreateAsync(database, clock: new MutableClock(live: true), kestrel: true);
        var tenant = await world.CreateTenantAsync();
        await world.EntitleAsync(tenant.Tenant);
        var agent = await world.DeclareBackupCapabilityAsync(await world.EnrollAsync(tenant));
        var job = await CreateJobAsync(tenant, agent, MsSqlManualAgentSmokeTests.Configuration(sql, sql.DatabaseName), maxAttempts: 3, initialDelaySeconds: 3);
        var run = await MsSqlManualAgentSmokeTests.StartRunAsync(tenant, job, "manual-retry");
        var runId = run.GetProperty("id").GetGuid();
        var trace = new Trace();
        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);

        var failed = await WaitAsync(tenant, runId, host, trace, IsTerminal);
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("NotRetryable", Retry(failed).GetProperty("state").GetString());
        var first = Assert.Single(Attempts(failed));
        Assert.Equal("DatabaseNotFound", first.GetProperty("reasonCode").GetString());
        Assert.False(first.GetProperty("retryable").GetBoolean());
        await AssertStableAsync(tenant, runId, host, TimeSpan.FromSeconds(6), current => Assert.Single(Attempts(current)));

        // The operator fixes the cause and asks for a retry: same run, new attempt, monotonic number.
        await sql.InitializeAsync();
        using var retried = await tenant.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant.Tenant.Id.Value}/backup-runs/{runId}/retry");
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var reopened = AgentWorld.ReadJson(retried);
        Assert.Equal(runId, reopened.GetProperty("id").GetGuid());
        Assert.Equal(2, reopened.GetProperty("currentAttemptNumber").GetInt32());
        trace.Observe(reopened);
        var detail = await WaitAsync(tenant, runId, host, trace, IsTerminal);

        Assert.Equal("Succeeded", detail.GetProperty("status").GetString());
        var attempts = Attempts(detail);
        Assert.Equal(new[] { 1, 2 }, attempts.Select(a => a.GetProperty("number").GetInt32()));
        Assert.Equal("DatabaseNotFound", attempts[0].GetProperty("reasonCode").GetString());
        Assert.Empty(attempts[0].GetProperty("artifacts").EnumerateArray());
        Assert.Equal("Succeeded", attempts[1].GetProperty("status").GetString());
        var artifact = Assert.Single(attempts[1].GetProperty("artifacts").EnumerateArray());
        var replica = Assert.Single(artifact.GetProperty("replicas").EnumerateArray());
        Assert.Equal("Verified", replica.GetProperty("verificationState").GetString());
        Assert.Equal(1, await sql.FullBackupCountAsync());
        Assert.Equal(2, Retry(detail).GetProperty("attemptsUsed").GetInt32());
        output.WriteLine("Manual retry: {0}; attempts [1 Failed/DatabaseNotFound(non-retryable), 2 Succeeded] on the same run; one SQL full backup.", trace);
    }

    private static BackupExecutionConfiguration Configuration(OrchestrationSqlSource sql, TcpForwarder forwarder, string databaseName) =>
        MsSqlManualAgentSmokeTests.Configuration(sql, databaseName) with { Host = forwarder.Host, Port = forwarder.Port };

    private static async Task<Guid> CreateJobAsync(TenantSetup tenant, EnrolledAgent agent, BackupExecutionConfiguration configuration,
        int maxAttempts, int initialDelaySeconds, int maxDelaySeconds = 6, string? overlap = null)
    {
        using var response = await tenant.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant.Tenant.Id.Value}/backup-jobs", new
        {
            name = "Real retry SQL backup", agentId = agent.AgentId, configuration, overlapPolicy = overlap,
            retryPolicy = new { maxAttempts, initialDelaySeconds, maxDelaySeconds, backoff = "Exponential", jitter = 0 }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = AgentWorld.ReadJson(response);
        Assert.Equal(maxAttempts, created.GetProperty("retryPolicy").GetProperty("maxAttempts").GetInt32());
        return created.GetProperty("id").GetGuid();
    }

    private static JsonElement Retry(JsonElement run) => run.GetProperty("retry");
    private static List<JsonElement> Attempts(JsonElement run) => run.GetProperty("attempts").EnumerateArray().OrderBy(a => a.GetProperty("number").GetInt32()).ToList();
    private static bool IsTerminal(JsonElement run) => run.GetProperty("status").GetString() is "Succeeded" or "Failed" or "Cancelled" or "AgentLost";

    private static async Task<JsonElement> GetRunAsync(TenantSetup tenant, Guid runId)
    {
        using var response = await tenant.Admin.GetAsync($"/api/tenants/{tenant.Tenant.Id.Value}/backup-runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private static async Task<JsonElement> WaitAsync(TenantSetup tenant, Guid runId, AgentHostSmokeProcess host, Trace trace, Func<JsonElement, bool> reached)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        JsonElement last = default;
        try
        {
            while (true)
            {
                host.AssertAlive();
                last = await GetRunAsync(tenant, runId);
                trace.Observe(last);
                if (reached(last)) return last;
                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Expected run state was not reached within 3 minutes. Observed {trace}; last detail: {last}");
        }
    }

    /// <summary>Negative assertions need a bounded observation window: poll the run for <paramref name="window"/> and check the invariant every time.</summary>
    private static async Task AssertStableAsync(TenantSetup tenant, Guid runId, AgentHostSmokeProcess host, TimeSpan window, Action<JsonElement> invariant)
    {
        var until = DateTimeOffset.UtcNow + window;
        do
        {
            host.AssertAlive();
            invariant(await GetRunAsync(tenant, runId));
            await Task.Delay(250);
        }
        while (DateTimeOffset.UtcNow < until);
    }

    private static async Task AssertNoTaskAsync(AgentWorld world, EnrolledAgent agent)
    {
        using var poll = await world.PollAsync(agent);
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        Assert.Empty(AgentWorld.ReadJson(poll).GetProperty("tasks").EnumerateArray());
    }

    /// <summary>Distinct consecutive run statuses observed while polling, for diagnostics ("Pending -> Running -> RetryPending -> ...").</summary>
    private sealed class Trace
    {
        private readonly List<string> _states = ["Pending"];
        public void Observe(JsonElement run)
        {
            var status = run.GetProperty("status").GetString()!;
            if (_states[^1] != status) _states.Add(status);
        }

        public override string ToString() => string.Join(" -> ", _states);
    }
}
