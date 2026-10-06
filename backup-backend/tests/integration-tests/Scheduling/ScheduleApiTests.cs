using System.Net;
using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests.Scheduling;

/// <summary>The schedule over HTTP: validation, the read model, authorization, tenant isolation and the explanation of every decision.</summary>
public sealed class ScheduleApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 8, 30, 0, TimeSpan.Zero);

    private static object Body(Guid agentId, object? schedule, string? overlap = null) => new
    {
        name = "Scheduled full backup",
        agentId,
        configuration = new BackupPlatform.Contracts.Backups.BackupExecutionConfiguration { Database = "master", Host = "sql-server", IntegratedSecurity = true },
        overlapPolicy = overlap,
        schedule
    };

    private static object Schedule(string cron = "0 3 * * *", string zone = "Europe/Istanbul", double grace = 5400, string? overlap = null, bool? enabled = null) =>
        new { enabled, cronExpression = cron, timeZone = zone, missedRunGraceSeconds = grace, overlapPolicy = overlap };

    private async Task<(BackupApiScenario Scenario, Guid JobId, JsonElement Job)> CreateAsync(object? schedule = null, string? overlap = null, MutableClock? clock = null)
    {
        var scenario = await BackupApiScenario.CreateAsync(fixture, clock: clock ?? new MutableClock(Start));
        using var response = await scenario.CreateJobRawAsync(Body(scenario.Agent.AgentId, schedule ?? Schedule(), overlap));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var job = AgentWorld.ReadJson(response);
        return (scenario, job.GetProperty("id").GetGuid(), job);
    }

    // ---- create and read --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACreatedJobShowsItsScheduleWithTheNextOccurrenceInUtcAndNoSchedulerInternals()
    {
        var (scenario, jobId, created) = await CreateAsync(Schedule("0 3 * * *", "Europe/Istanbul", 5400, "Queue"));
        await using var _ = scenario;

        var schedule = created.GetProperty("schedule");
        Assert.True(schedule.GetProperty("enabled").GetBoolean());
        Assert.Equal("0 3 * * *", schedule.GetProperty("cronExpression").GetString());
        Assert.Equal("Europe/Istanbul", schedule.GetProperty("timeZone").GetString());
        Assert.Equal(5400, schedule.GetProperty("missedRunGraceSeconds").GetDouble());
        Assert.Equal("Queue", schedule.GetProperty("overlapPolicy").GetString());
        Assert.Equal("Queue", created.GetProperty("overlapPolicy").GetString());
        // 03:00 Istanbul is 00:00 UTC; the clock stands at 2026-10-05 08:30 UTC, so the next one is the following midnight.
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero), schedule.GetProperty("nextOccurrenceAt").GetDateTimeOffset());

        using var read = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{jobId}");
        var text = await read.Content.ReadAsStringAsync();
        Assert.Equal(created.GetProperty("schedule").GetRawText(), JsonDocument.Parse(text).RootElement.GetProperty("schedule").GetRawText());
        foreach (var internalName in new[] { "quartz", "trigger", "fingerprint", "qrtz", "triggerId", "jobKey" })
            Assert.DoesNotContain(internalName, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AJobWithoutAScheduleReportsNone()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Start));
        using var response = await scenario.CreateJobRawAsync(BackupApiScenario.JobBody(scenario.Agent.AgentId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var job = AgentWorld.ReadJson(response);
        Assert.True(!job.TryGetProperty("schedule", out var schedule) || schedule.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task OmittedScheduleMembersTakeDefaultsAndAJobOverlapMayBeSetOnTheScheduleAlone()
    {
        var (scenario, _, created) = await CreateAsync(new { cronExpression = "*/20 * * * *", timeZone = "UTC", overlapPolicy = "Queue" });
        await using var _ = scenario;

        var schedule = created.GetProperty("schedule");
        Assert.True(schedule.GetProperty("enabled").GetBoolean());
        Assert.Equal(1800, schedule.GetProperty("missedRunGraceSeconds").GetDouble());
        Assert.Equal("Queue", created.GetProperty("overlapPolicy").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 8, 40, 0, TimeSpan.Zero), schedule.GetProperty("nextOccurrenceAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task ConflictingJobAndScheduleOverlapPoliciesAreRejectedNotGuessed()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Start));
        using var response = await scenario.CreateJobRawAsync(Body(scenario.Agent.AgentId, Schedule(overlap: "Queue"), "Skip"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- validation -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("not a cron", "UTC", 1800, "cronExpression", "invalid_cron")]
    [InlineData("*/5 * * * * *", "UTC", 1800, "cronExpression", "invalid_cron")]
    [InlineData("60 * * * *", "UTC", 1800, "cronExpression", "invalid_cron")]
    [InlineData("", "UTC", 1800, "cronExpression", "invalid_cron")]
    [InlineData("0 3 * * *", "Mars/Base", 1800, "timeZone", "invalid_time_zone")]
    [InlineData("0 3 * * *", "Turkey Standard Time", 1800, "timeZone", "invalid_time_zone")]
    [InlineData("0 3 * * *", "", 1800, "timeZone", "invalid_time_zone")]
    [InlineData("0 3 * * *", "UTC", 10, "missedRunGraceSeconds", "invalid_grace")]
    [InlineData("0 3 * * *", "UTC", 700000, "missedRunGraceSeconds", "invalid_grace")]
    public async Task AnInvalidScheduleOnCreateIsRejectedWithAMachineReadableErrorAndNoJobIsSaved(string cron, string zone, double grace, string field, string code)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Start));

        using var response = await scenario.CreateJobRawAsync(Body(scenario.Agent.AgentId, Schedule(cron, zone, grace)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = AgentWorld.ReadJson(response);
        Assert.Equal("invalid_schedule", error.GetProperty("error").GetString());
        Assert.Equal(field, error.GetProperty("field").GetString());
        Assert.Equal(code, error.GetProperty("code").GetString());
        var message = error.GetProperty("message").GetString()!;
        Assert.NotEmpty(message);
        Assert.DoesNotContain("Cronos", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ", message);
        await using var context = fixture.CreatePlatformContext();
        Assert.Empty(context.BackupJobs.Where(job => job.TenantId == scenario.Setup.Tenant.Id));
    }

    [Fact]
    public async Task AnInvalidScheduleOnUpdateIsRejectedAndTheStoredScheduleIsUntouched()
    {
        var (scenario, jobId, _) = await CreateAsync();
        await using var _ = scenario;

        using var response = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{jobId}/schedule", Schedule("nonsense"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_cron", AgentWorld.ReadJson(response).GetProperty("code").GetString());
        using var read = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{jobId}");
        Assert.Equal("0 3 * * *", AgentWorld.ReadJson(read).GetProperty("schedule").GetProperty("cronExpression").GetString());
    }

    // ---- update, remove, enable, disable ----------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdatingTheScheduleChangesWhatIsReadAndLeavesRunHistoryAlone()
    {
        var (scenario, jobId, _) = await CreateAsync();
        await using var _ = scenario;
        var manual = await scenario.StartRunAsync(jobId, "history");
        var before = await scenario.RunAsync(manual);

        using var response = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{jobId}/schedule",
            Schedule("*/30 * * * *", "America/New_York", 900, "Queue"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var schedule = AgentWorld.ReadJson(response).GetProperty("schedule");
        Assert.Equal(("*/30 * * * *", "America/New_York", 900d, "Queue"), (schedule.GetProperty("cronExpression").GetString(), schedule.GetProperty("timeZone").GetString(),
            schedule.GetProperty("missedRunGraceSeconds").GetDouble(), schedule.GetProperty("overlapPolicy").GetString()));
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero), schedule.GetProperty("nextOccurrenceAt").GetDateTimeOffset());
        var after = await scenario.RunAsync(manual);
        Assert.Equal(before.GetRawText(), after.GetRawText());
    }

    [Fact]
    public async Task RemovingTheScheduleLeavesAnOnDemandJob()
    {
        var (scenario, jobId, _) = await CreateAsync();
        await using var _ = scenario;

        using var removed = await scenario.Admin.SendAsync(HttpMethod.Delete, scenario.Root + $"/backup-jobs/{jobId}/schedule");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var job = AgentWorld.ReadJson(removed);
        Assert.True(!job.TryGetProperty("schedule", out var schedule) || schedule.ValueKind == JsonValueKind.Null);
        Assert.True(job.GetProperty("enabled").GetBoolean());
        Assert.NotEqual(Guid.Empty, await scenario.StartRunAsync(jobId, "still-manual"));
    }

    [Fact]
    public async Task DisablingAJobStopsEverythingAndEnablingResumesAtTheNextFutureOccurrence()
    {
        var clock = new MutableClock(Start);
        var (scenario, jobId, _) = await CreateAsync(Schedule("0 * * * *", "UTC", 600, "Queue"), clock: clock);
        await using var _ = scenario;

        using var disabled = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/backup-jobs/{jobId}/disable");
        var off = AgentWorld.ReadJson(disabled);
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, off.GetProperty("schedule").GetProperty("nextOccurrenceAt").ValueKind);
        using var manual = await scenario.StartRawAsync(jobId, "while-disabled");
        Assert.Equal(HttpStatusCode.NotFound, manual.StatusCode);

        clock.Advance(TimeSpan.FromHours(5)); // 13:30
        using var enabled = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/backup-jobs/{jobId}/enable");
        var on = AgentWorld.ReadJson(enabled);
        Assert.True(on.GetProperty("enabled").GetBoolean());
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.Zero), on.GetProperty("schedule").GetProperty("nextOccurrenceAt").GetDateTimeOffset());
        Assert.NotEqual(Guid.Empty, await scenario.StartRunAsync(jobId, "after-enable"));
    }

    [Fact]
    public async Task AScheduleSwitchedOffReportsNoNextOccurrenceButKeepsItsSettings()
    {
        var (scenario, jobId, _) = await CreateAsync();
        await using var _ = scenario;

        using var response = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{jobId}/schedule", Schedule(enabled: false));

        var schedule = AgentWorld.ReadJson(response).GetProperty("schedule");
        Assert.False(schedule.GetProperty("enabled").GetBoolean());
        Assert.Equal("0 3 * * *", schedule.GetProperty("cronExpression").GetString());
        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("nextOccurrenceAt").ValueKind);
    }

    // ---- authorization and isolation --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ViewersReadTheScheduleAndItsHistoryButCannotChangeIt()
    {
        var (scenario, jobId, _) = await CreateAsync();
        await using var _ = scenario;
        var viewer = await scenario.SessionAsync(TenantRole.Viewer);
        var root = scenario.Root + $"/backup-jobs/{jobId}";

        using var read = await viewer.GetAsync(root);
        using var history = await viewer.GetAsync(root + "/schedule/occurrences");
        using var put = await viewer.SendAsync(HttpMethod.Put, root + "/schedule", Schedule("5 * * * *"));
        using var delete = await viewer.SendAsync(HttpMethod.Delete, root + "/schedule");
        using var enable = await viewer.SendAsync(HttpMethod.Post, root + "/enable");
        using var disable = await viewer.SendAsync(HttpMethod.Post, root + "/disable");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (read.StatusCode, history.StatusCode));
        Assert.All(new[] { put, delete, enable, disable }, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        using var after = await scenario.Admin.GetAsync(root);
        Assert.Equal("0 3 * * *", AgentWorld.ReadJson(after).GetProperty("schedule").GetProperty("cronExpression").GetString());
    }

    [Fact]
    public async Task AnotherTenantCannotSeeOrChangeTheScheduleAndAnonymousCallersAreRefused()
    {
        var (scenario, jobId, _) = await CreateAsync();
        await using var _ = scenario;
        var other = await scenario.World.CreateTenantAsync();
        var foreign = $"/api/tenants/{other.Tenant.Id.Value}/backup-jobs/{jobId}";

        using var read = await other.Admin.GetAsync(foreign);
        using var history = await other.Admin.GetAsync(foreign + "/schedule/occurrences");
        using var put = await other.Admin.SendAsync(HttpMethod.Put, foreign + "/schedule", Schedule("5 * * * *"));
        using var disable = await other.Admin.SendAsync(HttpMethod.Post, foreign + "/disable");
        using var anonymous = await scenario.World.Anonymous.GetAsync(scenario.Root + $"/backup-jobs/{jobId}/schedule/occurrences");

        Assert.All(new[] { read, history, put, disable }, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var mine = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{jobId}");
        Assert.True(AgentWorld.ReadJson(mine).GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public async Task OccurrenceHistoryRejectsAnOutOfRangeLimit(int limit)
    {
        var (scenario, jobId, _) = await CreateAsync();
        await using var _ = scenario;

        using var response = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{jobId}/schedule/occurrences?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- why a backup did or did not start --------------------------------------------------------------------------------------

    private static async Task<BackupScheduleEvaluation> EvaluateAsync(BackupApiScenario scenario, Guid jobId)
    {
        await using var scope = scenario.World.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BackupScheduleService>().EvaluateAsync(scenario.Setup.Tenant.Id, jobId, default);
    }

    [Fact]
    public async Task AScheduledRunIsVisibleAsSuchWithItsOccurrenceDelayAndAttemptTriggersAndEveryMissIsExplained()
    {
        var clock = new MutableClock(Start);
        var (scenario, jobId, _) = await CreateAsync(Schedule("0 * * * *", "UTC", 7200, "Queue"), clock: clock);
        await using var _ = scenario;
        clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(5)); // 11:35: 09:00, 10:00 and 11:00 came due while nothing was running
        using (var poll = await scenario.World.PollAsync(scenario.Agent)) Assert.Equal(HttpStatusCode.OK, poll.StatusCode); // the agent is alive, now

        var evaluation = await EvaluateAsync(scenario, jobId);

        var runId = evaluation.CreatedRunId!.Value;
        var run = await scenario.RunAsync(runId);
        Assert.Equal("Scheduled", run.GetProperty("triggerType").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 11, 0, 0, TimeSpan.Zero), run.GetProperty("scheduledFor").GetDateTimeOffset());
        Assert.Equal(35 * 60, run.GetProperty("scheduleDelaySeconds").GetDouble());
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.Zero), run.GetProperty("claimDeadline").GetDateTimeOffset());
        Assert.Equal("Scheduled", Assert.Single(BackupApiScenario.AttemptsOf(run)).GetProperty("trigger").GetString());

        using var history = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{jobId}/schedule/occurrences");
        var occurrences = AgentWorld.ReadJson(history).EnumerateArray().ToList();
        // Newest first: the run, then why the earlier due times did not run.
        Assert.Equal(["RunCreated", "MissedSuperseded", "MissedGraceExpired"], occurrences.Select(o => o.GetProperty("outcome").GetString()));
        var created = occurrences[0];
        Assert.Equal(runId, created.GetProperty("runId").GetGuid());
        Assert.Equal("run_created", created.GetProperty("reason").GetString());
        Assert.Equal(35 * 60, created.GetProperty("delaySeconds").GetDouble());
        var superseded = occurrences.First(o => o.GetProperty("outcome").GetString() == "MissedSuperseded");
        Assert.Equal("superseded_by_later_occurrence", superseded.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, superseded.GetProperty("runId").ValueKind);

        // The agent claims it through the normal pipeline; a retryable failure keeps the SAME run and its durable automatic retry provenance.
        var claim = await scenario.FailNextAsync("DatabaseUnavailable");
        Assert.Equal(runId, claim.RunId);
        clock.Advance(TimeSpan.FromMinutes(10));
        await scenario.ScanRetriesAsync();
        var retried = await scenario.RunAsync(runId);
        var attempts = BackupApiScenario.AttemptsOf(retried);
        Assert.Equal(["Scheduled", "AutomaticRetry"], attempts.Select(a => a.GetProperty("trigger").GetString()));
        Assert.Equal("Scheduled", retried.GetProperty("triggerType").GetString());
        Assert.Equal(1, await CountRunsAsync(scenario, jobId));
    }

    [Fact]
    public async Task ASkippedOverlapIsVisibleAndNamesTheRunThatBlockedIt()
    {
        var clock = new MutableClock(Start);
        var (scenario, jobId, _) = await CreateAsync(Schedule("0 * * * *", "UTC", 1800, "Skip"), clock: clock);
        await using var _ = scenario;
        var blocking = await scenario.StartRunAsync(jobId, "long-running");
        clock.Advance(TimeSpan.FromMinutes(40)); // 09:10: the 09:00 occurrence is due while the manual run is still queued
        using (var poll = await scenario.World.PollAsync(scenario.Agent)) Assert.Equal(HttpStatusCode.OK, poll.StatusCode);

        var evaluation = await EvaluateAsync(scenario, jobId);

        Assert.Null(evaluation.CreatedRunId);
        using var history = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{jobId}/schedule/occurrences");
        var skipped = Assert.Single(AgentWorld.ReadJson(history).EnumerateArray());
        Assert.Equal("SkippedOverlap", skipped.GetProperty("outcome").GetString());
        Assert.Equal("active_run", skipped.GetProperty("reason").GetString());
        Assert.Equal(blocking, skipped.GetProperty("runId").GetGuid());
        Assert.Equal(1, await CountRunsAsync(scenario, jobId));
    }

    private async Task<int> CountRunsAsync(BackupApiScenario scenario, Guid jobId)
    {
        await using var context = fixture.CreatePlatformContext();
        return context.BackupRuns.Count(run => run.JobId == jobId);
    }
}
