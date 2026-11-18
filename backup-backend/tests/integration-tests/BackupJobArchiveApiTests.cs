using System.Net;
using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BackupPlatform.IntegrationTests.RetentionApiSupport;

namespace BackupPlatform.IntegrationTests;

/// <summary>Archiving a backup job ("Korunan veriyi kaldır") over HTTP: the job leaves its lists and stops running, everything it produced stays readable.</summary>
public sealed class BackupJobArchiveApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 8, 30, 0, TimeSpan.Zero);

    private static Task<HttpResponseMessage> ArchiveAsync(BackupApiScenario scenario, Guid jobId, BrowserSession? session = null) =>
        (session ?? scenario.Admin).SendAsync(HttpMethod.Delete, scenario.Root + $"/backup-jobs/{jobId}");

    private static async Task<JsonElement> ReadAsync(BrowserSession session, string url)
    {
        using var response = await session.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private static async Task ArchiveOkAsync(BackupApiScenario scenario, Guid jobId)
    {
        using var archived = await ArchiveAsync(scenario, jobId);
        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
    }

    [Fact]
    public async Task AnArchivedJobLeavesTheJobListTheHomePageDataAndTheHealthSummaryButStaysAvailableAsAFilterOption()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Start));
        var jobId = await scenario.CreateJobAsync();
        var kept = await scenario.CreateJobAsync();

        await ArchiveOkAsync(scenario, jobId);

        var jobs = await ReadAsync(scenario.Admin, scenario.Root + "/backup-jobs");
        Assert.Equal([kept], jobs.EnumerateArray().Select(job => job.GetProperty("id").GetGuid()));
        var withArchived = await ReadAsync(scenario.Admin, scenario.Root + "/backup-jobs?includeArchived=true");
        var archived = Assert.Single(withArchived.EnumerateArray(), job => job.GetProperty("id").GetGuid() == jobId);
        Assert.Equal(Start, archived.GetProperty("archivedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, Assert.Single(withArchived.EnumerateArray(), job => job.GetProperty("id").GetGuid() == kept).GetProperty("archivedAt").ValueKind);

        var dashboard = await ReadAsync(scenario.Admin, scenario.Root + "/dashboard");
        Assert.Equal(1, dashboard.GetProperty("jobs").GetProperty("total").GetInt32());
        var health = await ReadAsync(scenario.Admin, scenario.Root + "/observability/health-summary");
        Assert.Equal([kept], health.GetProperty("jobs").EnumerateArray().Select(job => job.GetProperty("jobId").GetGuid()));
        using var jobHealth = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{jobId}/health");
        Assert.Equal(HttpStatusCode.NotFound, jobHealth.StatusCode);

        // Reading the job itself still works: its history is reached through it.
        Assert.Equal(Start, (await ReadAsync(scenario.Admin, scenario.Root + $"/backup-jobs/{jobId}")).GetProperty("archivedAt").GetDateTimeOffset());
        await using var context = fixture.CreatePlatformContext();
        var audit = await context.AuditEvents.AsNoTracking().Where(e => e.Action == AuditActions.BackupJobArchived && e.TargetId == jobId.ToString("D")).ToListAsync();
        Assert.Single(audit);
        Assert.DoesNotContain(await context.AuditEvents.AsNoTracking().Where(e => e.TargetId == jobId.ToString("D")).Select(e => e.Action).ToListAsync(), action => action == AuditActions.BackupJobDisabled);
    }

    [Fact]
    public async Task AnArchivedJobCannotBeArchivedOrChangedAgainAndThereIsNoWayBack()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Start));
        var jobId = await scenario.CreateJobAsync();
        await ArchiveOkAsync(scenario, jobId);
        var job = scenario.Root + $"/backup-jobs/{jobId}";
        var requests = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Delete, job, null),
            (HttpMethod.Put, job, BackupApiScenario.JobBody(scenario.Agent.AgentId)),
            (HttpMethod.Put, job + "/schedule", new { cronExpression = "0 3 * * *", timeZone = "UTC" }),
            (HttpMethod.Delete, job + "/schedule", null),
            (HttpMethod.Put, job + "/retention", new { keepForSeconds = 7200 }),
            (HttpMethod.Post, job + "/enable", null),
            (HttpMethod.Post, job + "/disable", null)
        };

        foreach (var (method, path, body) in requests)
        {
            using var response = await scenario.Admin.SendAsync(method, path, body);
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{method} {path} answered {(int)response.StatusCode}");
            Assert.Equal("job_archived", AgentWorld.ReadJson(response).GetProperty("error").GetString());
        }
        Assert.Equal("Nightly full backup", (await ReadAsync(scenario.Admin, job)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task OnlyAUserAdminOfTheTenantCanArchiveAndAnotherTenantsJobIsNeverTouched()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Start));
        var jobId = await scenario.CreateJobAsync();
        var nonMember = await scenario.SessionAsync(member: false);
        var platform = await scenario.World.SignInAsync(await scenario.World.SeedUserAsync(platformAdmin: true));
        var other = await scenario.World.CreateTenantAsync();

        using var outsider = await ArchiveAsync(scenario, jobId, nonMember);
        using var platformAdmin = await ArchiveAsync(scenario, jobId, platform);
        using var foreignRoot = await ArchiveAsync(scenario, jobId, other.Admin);
        using var foreignJob = await other.Admin.SendAsync(HttpMethod.Delete, BackupApiScenario.RootOf(other) + $"/backup-jobs/{jobId}");
        using var anonymous = await scenario.World.Anonymous.SendAsync(new(HttpMethod.Delete, scenario.Root + $"/backup-jobs/{jobId}"));

        Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, platformAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignRoot.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignJob.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(scenario.Admin, scenario.Root + $"/backup-jobs/{jobId}")).GetProperty("archivedAt").ValueKind);
    }

    [Fact]
    public async Task HistoryBackupFilesSharesAndRestoresOfAnArchivedJobStayReadableWithTheJobName()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Start));
        var jobId = await scenario.CreateJobAsync();
        var run = await scenario.StartRunAsync(jobId);
        var claim = await scenario.ClaimAsync();
        Assert.Equal(run, claim.RunId);
        await scenario.StartAttemptAsync(claim);
        using (var completed = await scenario.CompleteRawAsync(claim, scenario.SuccessRequest(claim))) Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        var artifactId = ArtifactOf(await scenario.RunAsync(run)).GetProperty("id").GetGuid();
        using (var share = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/backup-artifacts/{artifactId}/shares", new { reason = "support" }))
            Assert.Equal(HttpStatusCode.Created, share.StatusCode);

        await ArchiveOkAsync(scenario, jobId);

        var runs = await ReadAsync(scenario.Admin, scenario.Root + "/backup-runs");
        Assert.Equal("Nightly full backup", Assert.Single(runs.GetProperty("items").EnumerateArray()).GetProperty("jobName").GetString());
        Assert.Single((await ReadAsync(scenario.Admin, scenario.Root + $"/backup-runs?jobId={jobId}")).GetProperty("items").EnumerateArray());
        Assert.Single((await ReadAsync(scenario.Admin, scenario.Root + $"/backup-jobs/{jobId}/runs")).GetProperty("items").EnumerateArray());
        Assert.Equal("Nightly full backup", (await scenario.RunAsync(run)).GetProperty("jobName").GetString());
        var artifacts = await ReadAsync(scenario.Admin, scenario.Root + "/backup-artifacts");
        Assert.Equal(artifactId, Assert.Single(artifacts.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal("Available", (await ArtifactAsync(scenario, artifactId)).GetProperty("state").GetString());
        Assert.Single((await ReadAsync(scenario.Admin, scenario.Root + "/backup-shares")).EnumerateArray());
        Assert.Single((await ReadAsync(scenario.Admin, scenario.Root + $"/backup-artifacts/{artifactId}/shares")).EnumerateArray());
        // A backup's own server stays offered as a restore target after its job is archived.
        var targets = await ReadAsync(scenario.Admin, scenario.Root + $"/backup-artifacts/{artifactId}/restore-targets");
        Assert.Equal(jobId, Assert.Single(targets.GetProperty("targets").EnumerateArray()).GetProperty("jobId").GetGuid());
        Assert.Equal(JsonValueKind.Object, (await ReadAsync(scenario.Admin, scenario.Root + "/restores")).ValueKind);
    }

    [Fact]
    public async Task AnArchivedJobsScheduleNoLongerCreatesBackupAttempts()
    {
        var clock = new MutableClock(Start);
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: clock);
        var jobId = await scenario.CreateJobAsync();
        var control = await scenario.CreateJobAsync();
        foreach (var id in new[] { jobId, control })
        {
            using var put = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{id}/schedule", new { cronExpression = "0 * * * *", timeZone = "UTC", missedRunGraceSeconds = 7200 });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }
        await ArchiveOkAsync(scenario, jobId);

        clock.Advance(TimeSpan.FromHours(3));
        using (var poll = await scenario.World.PollAsync(scenario.Agent)) Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        await using (var scope = scenario.World.Factory.Services.CreateAsyncScope())
        {
            var report = await scope.ServiceProvider.GetRequiredService<BackupScheduleService>().SweepAsync(default);
            Assert.Empty(report.Errors);
            // The control job proves the sweep does start due schedules here; only the archived one is left out.
            Assert.Equal(1, report.RunsCreated);
            var evaluation = await scope.ServiceProvider.GetRequiredService<BackupScheduleService>().EvaluateAsync(scenario.Setup.Tenant.Id, jobId, default);
            Assert.Null(evaluation.CreatedRunId);
        }

        Assert.Equal(1, (await ReadAsync(scenario.Admin, scenario.Root + $"/backup-runs?jobId={control}")).GetProperty("totalCount").GetInt64());

        Assert.Equal(0, (await ReadAsync(scenario.Admin, scenario.Root + $"/backup-runs?jobId={jobId}")).GetProperty("totalCount").GetInt64());
        using var manual = await scenario.StartRawAsync(jobId, "after-archive");
        Assert.Equal(HttpStatusCode.NotFound, manual.StatusCode);
    }

    [Fact]
    public async Task ArchivedJobsNeverRetryFailedRunsOrDispatchAlreadyQueuedAttempts()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Start));
        var job = await scenario.CreateJobAsync();
        var failedRun = await scenario.StartRunAsync(job);
        await scenario.FailNextAsync("DatabaseUnavailable");
        var queuedJob = await scenario.CreateJobAsync();
        var queuedRun = await scenario.StartRunAsync(queuedJob);
        await ArchiveOkAsync(scenario, job);
        await ArchiveOkAsync(scenario, queuedJob);

        using var retry = await scenario.RetryRawAsync(failedRun);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        scenario.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await scenario.ScanRetriesAsync());
        Assert.Single(BackupApiScenario.AttemptsOf(await scenario.RunAsync(failedRun)));
        Assert.Null(await scenario.PollAsync());
        Assert.Single(BackupApiScenario.AttemptsOf(await scenario.RunAsync(queuedRun)));
    }

    [Fact]
    public async Task AFailureAfterArchivingDoesNotRetryButOtherJobsStillRetry()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Start));
        var job = await scenario.CreateJobAsync();
        var run = await scenario.StartRunAsync(job);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);
        await ArchiveOkAsync(scenario, job);
        using (var failure = await scenario.FailRawAsync(claim, "DatabaseUnavailable"))
            Assert.Equal(HttpStatusCode.NoContent, failure.StatusCode);

        var controlJob = await scenario.CreateJobAsync();
        var controlRun = await scenario.StartRunAsync(controlJob);
        await scenario.FailNextAsync("DatabaseUnavailable");
        scenario.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, await scenario.ScanRetriesAsync());
        Assert.Single(BackupApiScenario.AttemptsOf(await scenario.RunAsync(run)));
        Assert.Equal(2, BackupApiScenario.AttemptsOf(await scenario.RunAsync(controlRun)).Count);
        Assert.Equal(controlRun, (await scenario.ClaimAsync()).RunId);
    }

    [Fact]
    public async Task BackupFilesOfAnArchivedJobAreCleanedUpWhenTheirRetentionExpires()
    {
        var root = Directory.CreateTempSubdirectory("archive-retention-").FullName;
        try
        {
            await using var scenario = await BackupApiScenario.CreateAsync(fixture, WorkerConfiguration(root), new MutableClock(Start));
            var jobId = await CreateJobAsync(scenario, Policy(true, 2, 2));
            string path = string.Empty;
            var backup = await AcceptBackupAsync(scenario, jobId, claim => ManagedKey(scenario, jobId, claim), key =>
            {
                path = Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "backup bytes");
            });
            await ArchiveOkAsync(scenario, jobId);

            scenario.Clock.Advance(TimeSpan.FromSeconds(2)); // expiry
            await RunRetentionAsync(scenario);
            Assert.Equal("DeletePending", (await ArtifactAsync(scenario, backup.ArtifactId)).GetProperty("state").GetString());
            Assert.True(File.Exists(path));

            scenario.Clock.Advance(TimeSpan.FromSeconds(2)); // grace over
            await RunRetentionAsync(scenario);

            Assert.False(File.Exists(path));
            Assert.Equal("Deleted", (await ArtifactAsync(scenario, backup.ArtifactId)).GetProperty("state").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
