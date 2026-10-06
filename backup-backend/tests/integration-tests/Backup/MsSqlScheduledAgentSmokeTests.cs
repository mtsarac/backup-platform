using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.IntegrationTests.Scheduling;
using Npgsql;
using Xunit.Abstractions;

namespace BackupPlatform.IntegrationTests.Backup;

/// <summary>
/// Scheduled backups against the real thing: a real HTTPS backend with Quartz on PostgreSQL, the real published Agent.Host and a real SQL
/// Server. Occurrences come from the wall clock (production five-field cron, or the six-field development form where a test needs a short
/// interval). Missing runtime configuration fails explicitly (see MsSqlManualAgentSmokeTests).
/// </summary>
public sealed class MsSqlScheduledAgentSmokeTests(PostgreSqlFixture database, ITestOutputHelper output) : IClassFixture<PostgreSqlFixture>
{
    private static Dictionary<string, string?> Backend(bool seconds) => new()
    {
        ["Backups:Scheduling:Enabled"] = "true",
        ["Backups:Scheduling:ReconcileInterval"] = "00:00:05",
        ["Backups:Scheduling:AllowSecondPrecisionCron"] = seconds ? "true" : "false"
    };

    // ---- one backend, one agent, one SQL Server: scheduled occurrence -> Pending run -> claim -> real full backup ----------------

    [Fact]
    public async Task AScheduledOccurrenceCreatesARunTheRealAgentClaimsAndRunsAsAVerifiedFullBackup()
    {
        await using var sql = new OrchestrationSqlSource();
        await sql.InitializeAsync();
        await using var world = await AgentWorld.CreateAsync(database, clock: new MutableClock(live: true), kestrel: true, configuration: Backend(seconds: false));
        var tenant = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(tenant);
        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);
        await WaitForAgentAsync(world, agent, host);

        var created = await CreateJobAsync(tenant, agent, MsSqlManualAgentSmokeTests.Configuration(sql, sql.DatabaseName),
            new { cronExpression = "* * * * *", timeZone = "UTC", missedRunGraceSeconds = 300 }, "Skip");
        var jobId = created.GetProperty("id").GetGuid();
        var next = created.GetProperty("schedule").GetProperty("nextOccurrenceAt").GetDateTimeOffset();
        Assert.Equal(0, next.Second); // a real minute boundary: production cron semantics
        Assert.InRange((next - DateTimeOffset.UtcNow).TotalSeconds, 0, 61);

        var first = await WaitForRunsAsync(tenant, jobId, host, runs => runs.Any(run => Status(run) == "Succeeded"));
        await DisableAsync(tenant, jobId);
        var runs = await WaitForRunsAsync(tenant, jobId, host, all => all.All(IsTerminal));

        var run = runs.Where(r => Status(r) == "Succeeded").OrderBy(r => r.GetProperty("scheduledFor").GetDateTimeOffset()).First();
        Assert.Equal("Scheduled", run.GetProperty("triggerType").GetString());
        var occurrence = run.GetProperty("scheduledFor").GetDateTimeOffset();
        Assert.Equal(0, occurrence.Second);
        Assert.Equal(occurrence, first.Min(r => r.GetProperty("scheduledFor").GetDateTimeOffset()));
        var delay = run.GetProperty("scheduleDelaySeconds").GetDouble();
        Assert.InRange(delay, 0, 40); // created by the wake-up or the 5 s sweep, never a minute late
        var attempt = Assert.Single(Attempts(run));
        Assert.Equal("Scheduled", attempt.GetProperty("trigger").GetString());
        Assert.Equal("Succeeded", attempt.GetProperty("status").GetString());
        var artifact = Assert.Single(attempt.GetProperty("artifacts").EnumerateArray());
        var replica = Assert.Single(artifact.GetProperty("replicas").EnumerateArray());
        Assert.Equal("Verified", replica.GetProperty("verificationState").GetString());
        var physical = Path.Combine(host.StorageDirectory, replica.GetProperty("key").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        await using (var stored = File.OpenRead(physical))
        {
            var checksum = Convert.ToHexStringLower(await SHA256.HashDataAsync(stored));
            Assert.Equal(checksum, artifact.GetProperty("sourceChecksum").GetString());
            Assert.Equal(checksum, artifact.GetProperty("artifactChecksum").GetString());
            Assert.Equal(stored.Length, artifact.GetProperty("sizeBytes").GetInt64());
        }

        // One logical occurrence, one run, one backup: every run that exists is a distinct instant with its own RunCreated record, and SQL Server saw exactly that many backups.
        var instants = runs.Select(r => r.GetProperty("scheduledFor").GetDateTimeOffset()).ToList();
        Assert.Equal(instants.Count, instants.Distinct().Count());
        var occurrences = await OccurrencesAsync(tenant, jobId);
        Assert.Equal(runs.Count, occurrences.Count(o => o.GetProperty("outcome").GetString() == "RunCreated"));
        Assert.Equal(runs.Count(r => Status(r) == "Succeeded"), await sql.FullBackupCountAsync());
        Assert.Equal(runs.Count(r => Status(r) == "Succeeded"), Directory.GetFiles(host.StorageDirectory, "*", SearchOption.AllDirectories).Length);
        Assert.Empty(await QuartzNode.WakeUpsAsync(database, jobId)); // disabled: the wake-up is gone
        output.WriteLine("Scheduled smoke: occurrence {0:O}, run created {1:F1}s later by the scheduler, claimed by the real Agent.Host, MSSQL full backup Succeeded " +
            "(attempt trigger Scheduled), SHA256 source==artifact==replica; {2} run(s), {3} SQL backup(s).", occurrence, delay, runs.Count, await sql.FullBackupCountAsync());
    }

    // ---- the backend is down when an occurrence passes ---------------------------------------------------------------------------

    [Fact]
    public async Task AnOccurrenceMissedWhileTheBackendWasDownProducesExactlyOneCatchUpRunAfterTheRestartWithinGrace()
    {
        await using var sql = new OrchestrationSqlSource();
        await sql.InitializeAsync();
        var clock = new MutableClock(live: true);
        TenantSetup tenant;
        EnrolledAgent agent;
        Guid jobId;
        DateTimeOffset shutdownAt;
        await using (var first = await AgentWorld.CreateAsync(database, clock: clock, kestrel: true, configuration: Backend(seconds: false)))
        {
            tenant = await first.CreateTenantAsync();
            agent = await first.EnrollAsync(tenant);
            await using var firstHost = await AgentHostSmokeProcess.StartAsync(first, agent, output.WriteLine);
            await WaitForAgentAsync(first, agent, firstHost);
            await WaitForSecondAsync(5, 35); // far from a minute boundary: nothing runs between the job's creation and the shutdown
            var created = await CreateJobAsync(tenant, agent, MsSqlManualAgentSmokeTests.Configuration(sql, sql.DatabaseName),
                new { cronExpression = "* * * * *", timeZone = "UTC", missedRunGraceSeconds = 300 }, "Skip");
            jobId = created.GetProperty("id").GetGuid();
            Assert.Single(await QuartzNode.WakeUpsAsync(database, jobId)); // the wake-up is in PostgreSQL, not in this process
            shutdownAt = DateTimeOffset.UtcNow;
        } // the agent and then the whole backend stop here; the scheduler goes with them
        Assert.Equal(0, await RunCountAsync(jobId));
        Assert.Single(await QuartzNode.WakeUpsAsync(database, jobId)); // ...and it survived the shutdown

        await WaitForSecondAsync(5, 35, minimumElapsed: TimeSpan.FromSeconds(130), since: shutdownAt); // at least two occurrences pass with nobody home
        var restartAt = DateTimeOffset.UtcNow;
        Assert.Equal(0, await RunCountAsync(jobId));

        await using var world = await AgentWorld.CreateAsync(database, clock: clock, kestrel: true, configuration: Backend(seconds: false));
        var admin = await world.SignInAsync(tenant.Account);
        tenant = tenant with { Admin = admin };
        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);
        var runs = await WaitForRunsAsync(tenant, jobId, host, found => found.Any(run => Status(run) == "Succeeded"));
        await DisableAsync(tenant, jobId);
        runs = await WaitForRunsAsync(tenant, jobId, host, all => all.All(IsTerminal));

        var missed = (await OccurrencesAsync(tenant, jobId)).Where(o => o.GetProperty("scheduledAt").GetDateTimeOffset() <= restartAt).ToList();
        Assert.True(missed.Count >= 2, $"at least two occurrences must have passed while down; saw {missed.Count}");
        var latestMissed = missed.Max(o => o.GetProperty("scheduledAt").GetDateTimeOffset());
        var catchUp = Assert.Single(runs, run => run.GetProperty("scheduledFor").GetDateTimeOffset() <= restartAt); // exactly one run for everything missed
        Assert.Equal(latestMissed, catchUp.GetProperty("scheduledFor").GetDateTimeOffset()); // ...and it is the latest one
        Assert.Equal("Succeeded", Status(catchUp));
        Assert.Equal("Scheduled", catchUp.GetProperty("triggerType").GetString());
        Assert.Equal(missed.Count - 1, missed.Count(o => o.GetProperty("outcome").GetString() is "MissedSuperseded" or "MissedGraceExpired"));
        Assert.All(missed.Where(o => o.GetProperty("outcome").GetString() != "RunCreated"), o => Assert.Equal(JsonValueKind.Null, o.GetProperty("runId").ValueKind));
        Assert.True(catchUp.GetProperty("scheduleDelaySeconds").GetDouble() >= 1, "created after the outage, not on time");
        Assert.Equal(runs.Count(run => Status(run) == "Succeeded"), await sql.FullBackupCountAsync());
        output.WriteLine("Restart smoke: backend down {0:F0}s, {1} occurrences passed ({2}); after restart ONE catch-up run for {3:O}, the others recorded Missed*, run Succeeded on the real agent; {4} SQL backup(s).",
            (restartAt - shutdownAt).TotalSeconds, missed.Count, string.Join(",", missed.Select(o => o.GetProperty("outcome").GetString())), latestMissed, await sql.FullBackupCountAsync());
    }

    // ---- a long-running backup while the next occurrences arrive ----------------------------------------------------------------

    [Fact]
    public async Task SkipPolicyNeverStartsASecondExecutionWhileALongBackupIsStillRunning()
    {
        await using var sql = new OrchestrationSqlSource(rows: 80000);
        await sql.InitializeAsync();
        await using var world = await AgentWorld.CreateAsync(database, clock: new MutableClock(live: true), kestrel: true, configuration: Backend(seconds: true));
        var tenant = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(tenant);
        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);
        await WaitForAgentAsync(world, agent, host);
        var job = await CreateJobAsync(tenant, agent, MsSqlManualAgentSmokeTests.Configuration(sql, sql.DatabaseName),
            new { cronExpression = "*/2 * * * * *", timeZone = "UTC", missedRunGraceSeconds = 60 }, "Skip");
        var jobId = job.GetProperty("id").GetGuid();

        // A real backup is Running and the next occurrences keep arriving every two seconds.
        await WaitForRunsAsync(tenant, jobId, host, runs => runs.Any(run => Status(run) is "Running"));
        await WaitForOccurrencesAsync(tenant, jobId, host, occurrences => occurrences.Count(o => o.GetProperty("outcome").GetString() == "SkippedOverlap") >= 3);
        await DisableAsync(tenant, jobId);
        var runs = await WaitForRunsAsync(tenant, jobId, host, all => all.All(IsTerminal));
        var occurrences = await OccurrencesAsync(tenant, jobId);

        // However long the backups took, no run was ever created while another was still active, so no two executions overlapped.
        var intervals = runs.Select(run => (Id: run.GetProperty("id").GetGuid(), Created: run.GetProperty("createdAt").GetDateTimeOffset(),
            Completed: run.GetProperty("completedAt").GetDateTimeOffset())).OrderBy(interval => interval.Created).ToList();
        for (var index = 1; index < intervals.Count; index++)
            Assert.True(intervals[index].Created >= intervals[index - 1].Completed, $"run {intervals[index].Id} was created while {intervals[index - 1].Id} was still active");
        var first = intervals[0];
        var skipped = occurrences.Where(o => o.GetProperty("outcome").GetString() == "SkippedOverlap").ToList();
        Assert.Contains(skipped, o => o.GetProperty("runId").GetGuid() == first.Id); // skipped because of the long-running first backup
        Assert.All(skipped, o =>
        {
            var blocking = intervals.Single(interval => interval.Id == o.GetProperty("runId").GetGuid());
            var decided = o.GetProperty("decidedAt").GetDateTimeOffset();
            Assert.True(decided >= blocking.Created && decided <= blocking.Completed, "a skip must name the run that was active when it was decided");
            Assert.Equal("active_run", o.GetProperty("reason").GetString());
        });
        Assert.All(runs, run => Assert.Equal("Succeeded", Status(run)));
        Assert.Equal(runs.Count, occurrences.Count(o => o.GetProperty("outcome").GetString() == "RunCreated"));
        Assert.Equal(runs.Count, await sql.FullBackupCountAsync()); // each run executed exactly once, never twice
        Assert.Equal(runs.Count, Directory.GetFiles(host.StorageDirectory, "*", SearchOption.AllDirectories).Length);
        output.WriteLine("Skip smoke: first real backup ran {0:F1}s; {1} occurrence(s) recorded SkippedOverlap while a run was active; {2} run(s), never overlapping; {2} SQL backup(s).",
            (first.Completed - first.Created).TotalSeconds, skipped.Count, runs.Count);
    }

    [Fact]
    public async Task QueuePolicyQueuesBehindALongBackupAndTheAgentStillRunsOneBackupAtATime()
    {
        await using var sql = new OrchestrationSqlSource(rows: 80000);
        await sql.InitializeAsync();
        await using var world = await AgentWorld.CreateAsync(database, clock: new MutableClock(live: true), kestrel: true, configuration: Backend(seconds: true));
        var tenant = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(tenant);
        await using var host = await AgentHostSmokeProcess.StartAsync(world, agent, output.WriteLine);
        await WaitForAgentAsync(world, agent, host);
        var job = await CreateJobAsync(tenant, agent, MsSqlManualAgentSmokeTests.Configuration(sql, sql.DatabaseName),
            new { cronExpression = "*/3 * * * * *", timeZone = "UTC", missedRunGraceSeconds = 120 }, "Queue");
        var jobId = job.GetProperty("id").GetGuid();

        var observed = new List<int>();
        var runs = await WaitForRunsAsync(tenant, jobId, host, found =>
        {
            observed.Add(found.Count(run => Status(run) is "Leased" or "Running"));
            return found.Any(run => Status(run) == "Running") && found.Any(run => Status(run) == "Pending");
        });
        await DisableAsync(tenant, jobId);
        var running = runs.Single(run => Status(run) == "Running").GetProperty("id").GetGuid();
        foreach (var queued in runs.Where(run => Status(run) == "Pending"))
        {
            Assert.Equal("Scheduled", queued.GetProperty("triggerType").GetString());
            using var cancel = await tenant.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant.Tenant.Id.Value}/backup-runs/{queued.GetProperty("id").GetGuid()}/cancel");
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        var done = await WaitForRunsAsync(tenant, jobId, host, all => { observed.Add(all.Count(run => Status(run) is "Leased" or "Running")); return all.All(IsTerminal); });
        Assert.True(observed.Max() <= 1, "the agent must never execute two backups at once");
        Assert.Equal("Succeeded", Status(done.Single(run => run.GetProperty("id").GetGuid() == running)));
        Assert.All(done.Where(run => run.GetProperty("id").GetGuid() != running), run => Assert.Equal("Cancelled", Status(run)));
        Assert.Equal(1, await sql.FullBackupCountAsync());
        output.WriteLine("Queue smoke: {0} scheduled run(s) queued behind one real long backup (never more than {1} executing); queued ones cancelled; one SQL backup.", done.Count - 1, observed.Max());
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------------

    private static async Task<JsonElement> CreateJobAsync(TenantSetup tenant, EnrolledAgent agent, BackupExecutionConfiguration configuration, object schedule, string overlap)
    {
        using var response = await tenant.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant.Tenant.Id.Value}/backup-jobs",
            new { name = "Real scheduled SQL backup", agentId = agent.AgentId, configuration, overlapPolicy = overlap, schedule });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private static async Task DisableAsync(TenantSetup tenant, Guid jobId)
    {
        using var response = await tenant.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{tenant.Tenant.Id.Value}/backup-jobs/{jobId}/disable");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<List<JsonElement>> RunsAsync(TenantSetup tenant, Guid jobId)
    {
        using var response = await tenant.Admin.GetAsync($"/api/tenants/{tenant.Tenant.Id.Value}/backup-jobs/{jobId}/runs?pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response).GetProperty("items").EnumerateArray().OrderBy(run => run.GetProperty("createdAt").GetDateTimeOffset()).ToList();
    }

    private static async Task<List<JsonElement>> OccurrencesAsync(TenantSetup tenant, Guid jobId)
    {
        using var response = await tenant.Admin.GetAsync($"/api/tenants/{tenant.Tenant.Id.Value}/backup-jobs/{jobId}/schedule/occurrences?limit=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response).EnumerateArray().OrderBy(o => o.GetProperty("scheduledAt").GetDateTimeOffset()).ToList();
    }

    private static async Task<List<JsonElement>> WaitForRunsAsync(TenantSetup tenant, Guid jobId, AgentHostSmokeProcess host, Func<List<JsonElement>, bool> reached)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        List<JsonElement> last = [];
        try
        {
            while (true)
            {
                host.AssertAlive();
                last = await RunsAsync(tenant, jobId);
                if (reached(last)) return last;
                await Task.Delay(200, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The expected scheduled-run state was not reached within 4 minutes. Runs: " + string.Join(", ", last.Select(Status)));
        }
    }

    private static async Task<List<JsonElement>> WaitForOccurrencesAsync(TenantSetup tenant, Guid jobId, AgentHostSmokeProcess host, Func<List<JsonElement>, bool> reached)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            while (true)
            {
                host.AssertAlive();
                var occurrences = await OccurrencesAsync(tenant, jobId);
                if (reached(occurrences)) return occurrences;
                await Task.Delay(250, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The expected occurrence decisions were not recorded within 2 minutes.");
        }
    }

    /// <summary>The agent is Online for the scheduler once its first sign of life is persisted; scheduling an occurrence earlier would only defer it.</summary>
    private static async Task WaitForAgentAsync(AgentWorld world, EnrolledAgent agent, AgentHostSmokeProcess host)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!timeout.IsCancellationRequested)
        {
            host.AssertAlive();
            if (await world.ConnectivityOfAsync(agent.AgentId) is not null) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("The real Agent.Host never reported in.");
    }

    /// <summary>Waits until the wall clock's seconds-of-minute fall in [from, to], and (optionally) until <paramref name="minimumElapsed"/> has passed since <paramref name="since"/>.</summary>
    private static async Task WaitForSecondAsync(int from, int to, TimeSpan? minimumElapsed = null, DateTimeOffset? since = null)
    {
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            var seconds = now.Second + now.Millisecond / 1000.0;
            if (seconds >= from && seconds <= to && (minimumElapsed is null || now - since >= minimumElapsed)) return;
            await Task.Delay(250);
        }
    }

    private async Task<int> RunCountAsync(Guid jobId)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM backup_runs WHERE \"JobId\" = @job", connection);
        command.Parameters.AddWithValue("job", jobId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Status(JsonElement run) => run.GetProperty("status").GetString()!;
    private static bool IsTerminal(JsonElement run) => Status(run) is "Succeeded" or "Failed" or "Cancelled" or "AgentLost";
    private static List<JsonElement> Attempts(JsonElement run) => run.GetProperty("attempts").EnumerateArray().OrderBy(a => a.GetProperty("number").GetInt32()).ToList();
}
