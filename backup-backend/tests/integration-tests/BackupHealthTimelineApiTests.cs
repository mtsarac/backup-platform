using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Backups;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.RetentionApiSupport;

namespace BackupPlatform.IntegrationTests;

/// <summary>The 14 day strip of the home page over HTTP: days are cut at the caller's local midnights and every cell comes from the runs ("Yedekleme denemesi") of that day.</summary>
public sealed class BackupHealthTimelineApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 20, 0, 0, TimeSpan.Zero);

    private static string Url(BackupApiScenario scenario, string? timeZone) =>
        scenario.Root + "/backup-jobs/health-timeline" + (timeZone is null ? "" : "?timeZone=" + Uri.EscapeDataString(timeZone));

    private static async Task<JsonElement> ReadAsync(BackupApiScenario scenario, string? timeZone)
    {
        using var response = await scenario.Admin.GetAsync(Url(scenario, timeZone));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private static JsonElement JobOf(JsonElement timeline, Guid jobId) =>
        Assert.Single(timeline.GetProperty("jobs").EnumerateArray(), job => job.GetProperty("jobId").GetGuid() == jobId);

    /// <summary>(date, status, attempts) of every day of a job, oldest first.</summary>
    private static List<(string Date, string Status, int Attempts)> DaysOf(JsonElement timeline, Guid jobId) =>
        [.. JobOf(timeline, jobId).GetProperty("days").EnumerateArray().Select(day => (day.GetProperty("date").GetString()!, day.GetProperty("status").GetString()!, day.GetProperty("attemptCount").GetInt32()))];

    private static string StatusOn(JsonElement timeline, Guid jobId, string date) => DaysOf(timeline, jobId).Single(day => day.Date == date).Status;

    /// <summary>Creates a run through the API, then places it at <paramref name="at"/> in the state a finished or running run would have.</summary>
    private async Task SeedRunAsync(BackupApiScenario scenario, Guid jobId, DateTimeOffset at, BackupStatus status)
    {
        var runId = await scenario.StartRunAsync(jobId);
        await using var context = fixture.CreatePlatformContext();
        var completed = status is BackupStatus.Succeeded or BackupStatus.Failed ? (DateTimeOffset?)at : null;
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE backup_runs SET \"CreatedAt\" = {at}, \"Status\" = {(int)status}, \"CompletedAt\" = {completed} WHERE \"Id\" = {runId}");
    }

    private async Task SetJobCreatedAsync(Guid jobId, DateTimeOffset at)
    {
        await using var context = fixture.CreatePlatformContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE backup_jobs SET \"CreatedAt\" = {at} WHERE \"Id\" = {jobId}");
    }

    private static void AssertFourteenConsecutiveDays(IReadOnlyList<(string Date, string Status, int Attempts)> days, string lastDate)
    {
        var last = DateOnly.ParseExact(lastDate, "yyyy-MM-dd");
        Assert.Equal(Enumerable.Range(-13, 14).Select(offset => last.AddDays(offset).ToString("yyyy-MM-dd")), days.Select(day => day.Date));
    }

    [Fact]
    public async Task EveryJobGetsFourteenDaysAndEachDayIsSucceededFailedRunningNoBackupOrNoJob()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Now));
        var jobId = await scenario.CreateJobAsync();
        await SetJobCreatedAsync(jobId, new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        await SeedRunAsync(scenario, jobId, new(2026, 10, 19, 3, 0, 0, TimeSpan.Zero), BackupStatus.Failed);
        await SeedRunAsync(scenario, jobId, new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero), BackupStatus.Succeeded);
        await SeedRunAsync(scenario, jobId, new(2026, 10, 18, 3, 0, 0, TimeSpan.Zero), BackupStatus.Failed);
        await SeedRunAsync(scenario, jobId, new(2026, 10, 20, 18, 0, 0, TimeSpan.Zero), BackupStatus.Running);

        var days = DaysOf(await ReadAsync(scenario, "UTC"), jobId);

        AssertFourteenConsecutiveDays(days, "2026-10-20");
        Assert.Equal(
        [
            ("2026-10-07", "NoJob", 0), ("2026-10-08", "NoJob", 0), ("2026-10-09", "NoJob", 0),
            ("2026-10-10", "NoBackup", 0), ("2026-10-11", "NoBackup", 0), ("2026-10-12", "NoBackup", 0), ("2026-10-13", "NoBackup", 0),
            ("2026-10-14", "NoBackup", 0), ("2026-10-15", "NoBackup", 0), ("2026-10-16", "NoBackup", 0), ("2026-10-17", "NoBackup", 0),
            ("2026-10-18", "Failed", 1), ("2026-10-19", "Succeeded", 2), ("2026-10-20", "Running", 1)
        ], days);
    }

    [Fact]
    public async Task OneSucceededRunMakesTheDayEvenIfAnotherFailedOrIsStillRunningAndRunningNeverAppliesToPastDays()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Now));
        var today = await scenario.CreateJobAsync();
        var pastActive = await scenario.CreateJobAsync();
        await SetJobCreatedAsync(today, Now.AddDays(-30));
        await SetJobCreatedAsync(pastActive, Now.AddDays(-30));
        await SeedRunAsync(scenario, today, new(2026, 10, 20, 2, 0, 0, TimeSpan.Zero), BackupStatus.Succeeded);
        await SeedRunAsync(scenario, today, new(2026, 10, 19, 2, 0, 0, TimeSpan.Zero), BackupStatus.Failed);
        // An active run blocks the next run of its job (overlap policy Skip), so it is always seeded last.
        await SeedRunAsync(scenario, today, new(2026, 10, 20, 18, 0, 0, TimeSpan.Zero), BackupStatus.Running);
        await SeedRunAsync(scenario, pastActive, new(2026, 10, 20, 2, 0, 0, TimeSpan.Zero), BackupStatus.Failed);
        await SeedRunAsync(scenario, pastActive, new(2026, 10, 19, 2, 0, 0, TimeSpan.Zero), BackupStatus.Running);

        var timeline = await ReadAsync(scenario, "UTC");

        Assert.Equal("Succeeded", StatusOn(timeline, today, "2026-10-20"));
        Assert.Equal(2, DaysOf(timeline, today).Single(day => day.Date == "2026-10-20").Attempts);
        Assert.Equal("Failed", StatusOn(timeline, today, "2026-10-19"));
        Assert.Equal("Failed", StatusOn(timeline, pastActive, "2026-10-19"));
        Assert.Equal("Failed", StatusOn(timeline, pastActive, "2026-10-20"));
    }

    [Fact]
    public async Task TheSameRunLandsOnDifferentDaysInDifferentTimeZonesAndTheWindowEndsOnTheCallersToday()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Now));
        var jobId = await scenario.CreateJobAsync();
        await SetJobCreatedAsync(jobId, Now.AddDays(-30));
        // 23:30 UTC on the 19th is already 08:30 on the 20th in Tokyo (UTC+9) and still the evening of the 19th in Los Angeles (UTC-7).
        await SeedRunAsync(scenario, jobId, new(2026, 10, 19, 23, 30, 0, TimeSpan.Zero), BackupStatus.Succeeded);

        var utc = await ReadAsync(scenario, "UTC");
        var tokyo = await ReadAsync(scenario, "Asia/Tokyo");
        var losAngeles = await ReadAsync(scenario, "America/Los_Angeles");

        Assert.Equal("Asia/Tokyo", tokyo.GetProperty("timeZone").GetString());
        Assert.Equal("Succeeded", StatusOn(utc, jobId, "2026-10-19"));
        Assert.Equal("NoBackup", StatusOn(utc, jobId, "2026-10-20"));
        Assert.Equal("NoBackup", StatusOn(tokyo, jobId, "2026-10-19"));
        Assert.Equal("Succeeded", StatusOn(tokyo, jobId, "2026-10-20"));
        Assert.Equal("Succeeded", StatusOn(losAngeles, jobId, "2026-10-19"));
        // 20:00 UTC is 05:00 on the 21st in Tokyo and 13:00 on the 20th in Los Angeles: each window ends on the caller's own today.
        AssertFourteenConsecutiveDays(DaysOf(utc, jobId), "2026-10-20");
        AssertFourteenConsecutiveDays(DaysOf(tokyo, jobId), "2026-10-21");
        AssertFourteenConsecutiveDays(DaysOf(losAngeles, jobId), "2026-10-20");
    }

    // New York springs forward on 2026-03-08 (a 23 hour day), Berlin falls back on 2026-10-25 (a 25 hour day).
    // First and last instant of the transition day sit in one cell, the instant after the last one starts the next day.
    [Theory]
    [InlineData("America/New_York", "2026-03-12T15:00:00Z", "2026-03-12", "2026-03-08", "2026-03-08T05:00:00Z", "2026-03-09T03:59:00Z", "2026-03-09T04:00:00Z", "2026-03-08T04:59:00Z")]
    [InlineData("Europe/Berlin", "2026-10-28T12:00:00Z", "2026-10-28", "2026-10-25", "2026-10-24T22:00:00Z", "2026-10-25T22:59:00Z", "2026-10-25T23:00:00Z", "2026-10-24T21:59:00Z")]
    public async Task ADayWithADaylightSavingShiftIsOneDayAndTheWindowIsStillFourteenLocalDays(
        string zone, string now, string lastDate, string shiftDate, string firstInstant, string lastInstant, string nextDayInstant, string previousDayInstant)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(DateTimeOffset.Parse(now)));
        var jobId = await scenario.CreateJobAsync();
        await SetJobCreatedAsync(jobId, DateTimeOffset.Parse(now).AddDays(-60));
        await SeedRunAsync(scenario, jobId, DateTimeOffset.Parse(firstInstant), BackupStatus.Succeeded);
        await SeedRunAsync(scenario, jobId, DateTimeOffset.Parse(lastInstant), BackupStatus.Failed);
        await SeedRunAsync(scenario, jobId, DateTimeOffset.Parse(nextDayInstant), BackupStatus.Failed);
        await SeedRunAsync(scenario, jobId, DateTimeOffset.Parse(previousDayInstant), BackupStatus.Failed);

        var days = DaysOf(await ReadAsync(scenario, zone), jobId);

        AssertFourteenConsecutiveDays(days, lastDate);
        var shift = DateOnly.ParseExact(shiftDate, "yyyy-MM-dd");
        Assert.Equal(("Succeeded", 2), days.Where(day => day.Date == shiftDate).Select(day => (day.Status, day.Attempts)).Single());
        Assert.Equal(("Failed", 1), days.Where(day => day.Date == shift.AddDays(1).ToString("yyyy-MM-dd")).Select(day => (day.Status, day.Attempts)).Single());
        Assert.Equal(("Failed", 1), days.Where(day => day.Date == shift.AddDays(-1).ToString("yyyy-MM-dd")).Select(day => (day.Status, day.Attempts)).Single());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("UTC+3")]
    [InlineData("Eastern Standard Time")]
    [InlineData("../../etc/passwd")]
    public async Task AMissingOrUnknownTimeZoneIsRefusedInsteadOfFallingBackToUtc(string? timeZone)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Now));
        await scenario.CreateJobAsync();

        using var response = await scenario.Admin.GetAsync(Url(scenario, timeZone));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("timeZone", AgentWorld.ReadJson(response).GetProperty("field").GetString());
    }

    [Fact]
    public async Task AnArchivedJobAndAnotherTenantsJobsAreNotInTheTimeline()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Now));
        var kept = await scenario.CreateJobAsync();
        var archived = await scenario.CreateJobAsync();
        using (var archive = await scenario.Admin.SendAsync(HttpMethod.Delete, scenario.Root + $"/backup-jobs/{archived}")) Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        var other = await scenario.World.CreateTenantAsync();
        await scenario.World.EntitleAsync(other.Tenant);
        var otherAgent = await scenario.World.DeclareBackupCapabilityAsync(await scenario.World.EnrollAsync(other));
        using var created = await other.Admin.SendAsync(HttpMethod.Post, BackupApiScenario.RootOf(other) + "/backup-jobs", BackupApiScenario.JobBody(otherAgent.AgentId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var otherJob = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();

        var timeline = await ReadAsync(scenario, "UTC");
        using var otherResponse = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + "/backup-jobs/health-timeline?timeZone=UTC");

        Assert.Equal([kept], timeline.GetProperty("jobs").EnumerateArray().Select(job => job.GetProperty("jobId").GetGuid()));
        Assert.Equal(HttpStatusCode.OK, otherResponse.StatusCode);
        Assert.Equal([otherJob], AgentWorld.ReadJson(otherResponse).GetProperty("jobs").EnumerateArray().Select(job => job.GetProperty("jobId").GetGuid()));
    }

    [Fact]
    public async Task OnlyAMemberOfTheTenantCanReadTheTimeline()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, clock: new MutableClock(Now));
        var nonMember = await scenario.SessionAsync(member: false);

        using var outsider = await nonMember.GetAsync(Url(scenario, "UTC"));
        using var anonymous = await scenario.World.Anonymous.GetAsync(Url(scenario, "UTC"));

        Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
