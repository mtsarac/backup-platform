using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Tenancy;
using static BackupPlatform.IntegrationTests.BackupApiScenario;
using static BackupPlatform.IntegrationTests.RetentionApiSupport;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// Retention policy, artifact views and the manual delete/cancel-delete endpoints as operators see them over HTTP. The retention worker is
/// disabled in this suite (the default of the test host): nothing here may depend on a background process, and a delete request must never be physical.
/// </summary>
public sealed class RetentionApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly TimeSpan DefaultKeep = TimeSpan.FromDays(30);
    private static readonly TimeSpan DefaultGrace = TimeSpan.FromHours(24);

    // ---- policy on jobs and runs ----

    [Fact]
    public async Task AJobWithoutAPolicyGetsThePlatformDefaultsAndItsRunSnapshotsThem()
    {
        await using var scenario = await CreateAsync(fixture);

        var jobId = await CreateJobAsync(scenario);
        var job = await JobAsync(scenario, jobId);
        var run = await scenario.RunAsync(await scenario.StartRunAsync(jobId));

        foreach (var retention in new[] { job.GetProperty("retention"), run.GetProperty("retention") })
        {
            Assert.True(retention.GetProperty("enabled").GetBoolean());
            Assert.Equal(DefaultKeep.TotalSeconds, retention.GetProperty("keepForSeconds").GetDouble());
            Assert.Equal(DefaultGrace.TotalSeconds, retention.GetProperty("deleteGracePeriodSeconds").GetDouble());
        }
    }

    [Fact]
    public async Task AnExplicitPolicyIsHonouredAndOmittedMembersTakeThePlatformDefault()
    {
        await using var scenario = await CreateAsync(fixture);

        var full = await JobAsync(scenario, await CreateJobAsync(scenario, Policy(true, 7200, 3600)));
        var partial = await JobAsync(scenario, await CreateJobAsync(scenario, Policy(keepForSeconds: 10800)));

        Assert.Equal((true, 7200d, 3600d), Read(full));
        Assert.Equal((true, 10800d, DefaultGrace.TotalSeconds), Read(partial));
    }

    [Theory]
    [InlineData(60d, null)]      // keep below the platform minimum (one hour)
    [InlineData(null, 60d)]      // a window to undo a deletion below the minimum
    [InlineData(0d, null)]
    [InlineData(-5d, null)]
    [InlineData(null, 0d)]
    public async Task APolicyBelowThePlatformMinimumOrOutOfRangeIsRejectedAtCreation(double? keep, double? grace)
    {
        await using var scenario = await CreateAsync(fixture);

        using var response = await scenario.CreateJobRawAsync(JobBody(scenario, Policy(keepForSeconds: keep, deleteGracePeriodSeconds: grace)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }

    [Fact]
    public async Task DisabledRetentionKeepsBackupsForeverAndSkipsTheKeepMinimum()
    {
        await using var scenario = await CreateAsync(fixture);
        var jobId = await CreateJobAsync(scenario, Policy(enabled: false, keepForSeconds: 60));

        var backup = await AcceptBackupAsync(scenario, jobId);

        var artifact = await ArtifactAsync(scenario, backup.ArtifactId);
        Assert.True(IsNull(artifact, "retentionExpiresAt"));
        Assert.Equal("Available", artifact.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ChangingTheRetentionKeepsOmittedMembersAndRejectsInvalidPoliciesWithoutChangingTheJob()
    {
        await using var scenario = await CreateAsync(fixture);
        var jobId = await CreateJobAsync(scenario, Policy(true, 7200, 3600));

        using var disable = await PutRetentionAsync(scenario, jobId, Policy(enabled: false));
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Equal((false, 7200d, 3600d), Read(AgentWorld.ReadJson(disable)));

        using var longer = await PutRetentionAsync(scenario, jobId, Policy(keepForSeconds: 10800));
        Assert.Equal((false, 10800d, 3600d), Read(AgentWorld.ReadJson(longer)));

        using var enable = await PutRetentionAsync(scenario, jobId, Policy(enabled: true, deleteGracePeriodSeconds: 7200));
        Assert.Equal((true, 10800d, 7200d), Read(AgentWorld.ReadJson(enable)));

        using var tooShort = await PutRetentionAsync(scenario, jobId, Policy(keepForSeconds: 60));
        using var tooShortGrace = await PutRetentionAsync(scenario, jobId, Policy(deleteGracePeriodSeconds: 60));
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooShortGrace.StatusCode);
        Assert.Equal((true, 10800d, 7200d), Read(await JobAsync(scenario, jobId)));

        using var unknown = await PutRetentionAsync(scenario, Guid.NewGuid(), Policy(enabled: true));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task ChangingTheJobPolicyNeverRewritesAnExistingRunOrBackupButANewRunSnapshotsTheNewPolicy()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 5, 8, 30, 0, TimeSpan.Zero));
        await using var scenario = await CreateAsync(fixture, clock: clock);
        var jobId = await CreateJobAsync(scenario, Policy(true, 7200, 3600));
        var first = await AcceptBackupAsync(scenario, jobId);
        var expiresAt = first.AcceptedAt + TimeSpan.FromHours(2);

        using var change = await PutRetentionAsync(scenario, jobId, Policy(keepForSeconds: 36000, deleteGracePeriodSeconds: 7200));
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await AcceptBackupAsync(scenario, jobId);

        var firstRun = await scenario.RunAsync(first.RunId);
        Assert.Equal((true, 7200d, 3600d), Read(firstRun));
        var firstArtifact = ArtifactOf(firstRun);
        Assert.Equal(expiresAt, firstArtifact.GetProperty("retentionExpiresAt").GetDateTimeOffset());
        var secondRun = await scenario.RunAsync(second.RunId);
        Assert.Equal((true, 36000d, 7200d), Read(secondRun));
        Assert.Equal(second.AcceptedAt + TimeSpan.FromHours(10), ArtifactOf(secondRun).GetProperty("retentionExpiresAt").GetDateTimeOffset());
    }

    // ---- what a run shows about its backup ----

    [Fact]
    public async Task ExpirationIsTheBackendAcceptanceTimePlusKeepForNotTheAgentsOwnTimestamp()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 5, 8, 30, 0, TimeSpan.Zero));
        await using var scenario = await CreateAsync(fixture, clock: clock);
        var jobId = await CreateJobAsync(scenario, Policy(true, 7200, 3600));

        // Inside the 2-minute lease: a completion that arrives after the lease ran out is rightly refused.
        var backup = await AcceptBackupAsync(scenario, jobId, agentWorkTakes: TimeSpan.FromSeconds(90));

        var artifact = ArtifactOf(await scenario.RunAsync(backup.RunId));
        Assert.Equal(backup.AcceptedAt, artifact.GetProperty("acceptedAt").GetDateTimeOffset());
        Assert.Equal(backup.AcceptedAt + TimeSpan.FromHours(2), artifact.GetProperty("retentionExpiresAt").GetDateTimeOffset());
        Assert.True(artifact.GetProperty("createdAt").GetDateTimeOffset() < backup.AcceptedAt); // the agent's own time is earlier and ignored
        Assert.Equal("Available", artifact.GetProperty("state").GetString());
        var replica = ReplicaOf(artifact);
        Assert.Equal("Available", replica.GetProperty("status").GetString());
        Assert.True(replica.GetProperty("usable").GetBoolean());
        Assert.True(IsNull(replica, "deleteAfter"));
        Assert.Equal(backup.Key, replica.GetProperty("key").GetString());
    }

    // ---- who may do what ----

    [Fact]
    public async Task ViewersReadArtifactsButOnlyAdministratorsMayDeleteCancelOrChangeRetention()
    {
        await using var scenario = await CreateAsync(fixture);
        var jobId = await CreateJobAsync(scenario);
        var backup = await AcceptBackupAsync(scenario, jobId);
        var viewer = await scenario.SessionAsync(TenantRole.Viewer);
        var @operator = await scenario.SessionAsync(TenantRole.Operator);

        using var list = await viewer.GetAsync(scenario.Root + "/backup-artifacts");
        using var get = await viewer.GetAsync(scenario.Root + $"/backup-artifacts/{backup.ArtifactId}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        foreach (var session in new[] { viewer, @operator })
        {
            using var delete = await DeleteArtifactAsync(scenario, backup.ArtifactId, session);
            using var cancel = await CancelDeleteAsync(scenario, backup.ArtifactId, session);
            using var retention = await PutRetentionAsync(scenario, jobId, Policy(keepForSeconds: 7200), session);
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, retention.StatusCode);
        }

        using var anonymous = await scenario.World.Anonymous.DeleteAsync(scenario.Root + $"/backup-artifacts/{backup.ArtifactId}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("Available", (await ArtifactAsync(scenario, backup.ArtifactId)).GetProperty("state").GetString());
        Assert.Equal((true, DefaultKeep.TotalSeconds, DefaultGrace.TotalSeconds), Read(await JobAsync(scenario, jobId)));
    }

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    public async Task OwnersAndAdminsMayRequestDeletion(TenantRole role)
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await AcceptBackupAsync(scenario, await CreateJobAsync(scenario));
        var session = await scenario.SessionAsync(role);

        using var response = await DeleteArtifactAsync(scenario, backup.ArtifactId, session);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task ArtifactsOfAnotherTenantDoNotExistForAnyEndpoint()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await AcceptBackupAsync(scenario, await CreateJobAsync(scenario));
        var other = await scenario.World.CreateTenantAsync();
        var otherRoot = RootOf(other);

        using var get = await other.Admin.GetAsync(otherRoot + $"/backup-artifacts/{backup.ArtifactId}");
        using var delete = await DeleteArtifactAsync(scenario, backup.ArtifactId, other.Admin, otherRoot);
        using var cancel = await CancelDeleteAsync(scenario, backup.ArtifactId, other.Admin, otherRoot);
        using var list = await other.Admin.GetAsync(otherRoot + "/backup-artifacts");
        using var viaVictimRoute = await DeleteArtifactAsync(scenario, backup.ArtifactId, other.Admin);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.Empty(AgentWorld.ReadJson(list).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, viaVictimRoute.StatusCode); // not a member of that tenant at all
        Assert.Equal("Available", (await ArtifactAsync(scenario, backup.ArtifactId)).GetProperty("state").GetString());
    }

    // ---- listing ----

    [Fact]
    public async Task ListingFiltersByJobValidatesTheLimitAndNeverDropsAnArtifactThatIsBeingDeleted()
    {
        await using var scenario = await CreateAsync(fixture);
        var jobA = await CreateJobAsync(scenario);
        var jobB = await CreateJobAsync(scenario);
        var a = await AcceptBackupAsync(scenario, jobA);
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));
        var b = await AcceptBackupAsync(scenario, jobB);
        using (await DeleteArtifactAsync(scenario, a.ArtifactId)) { }

        using var all = await scenario.Admin.GetAsync(scenario.Root + "/backup-artifacts");
        using var onlyA = await scenario.Admin.GetAsync(scenario.Root + $"/backup-artifacts?jobId={jobA}");
        using var tooMany = await scenario.Admin.GetAsync(scenario.Root + "/backup-artifacts?limit=101");
        using var none = await scenario.Admin.GetAsync(scenario.Root + "/backup-artifacts?limit=0");

        Assert.Equal(new[] { b.ArtifactId, a.ArtifactId }, AgentWorld.ReadJson(all).EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray()); // newest first
        var only = Assert.Single(AgentWorld.ReadJson(onlyA).EnumerateArray());
        Assert.Equal(a.ArtifactId, only.GetProperty("id").GetGuid());
        Assert.Equal("DeletePending", only.GetProperty("state").GetString());
        Assert.Equal(jobA, only.GetProperty("jobId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    // ---- manual delete and cancel ----

    [Fact]
    public async Task ADeleteRequestOnlyStartsTheGracePeriodAndRepeatingItChangesNothing()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 5, 8, 30, 0, TimeSpan.Zero));
        await using var scenario = await CreateAsync(fixture, clock: clock);
        var backup = await AcceptBackupAsync(scenario, await CreateJobAsync(scenario));
        var requestedAt = clock.GetUtcNow();

        using var first = await DeleteArtifactAsync(scenario, backup.ArtifactId);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var artifact = AgentWorld.ReadJson(first);
        Assert.Equal("DeletePending", artifact.GetProperty("state").GetString());
        Assert.Equal("Manual", artifact.GetProperty("deleteReason").GetString());
        var replica = ReplicaOf(artifact);
        Assert.Equal("DeletePending", replica.GetProperty("status").GetString());
        Assert.True(replica.GetProperty("usable").GetBoolean()); // still intact and restorable during the grace period
        Assert.Equal(backup.Key, replica.GetProperty("key").GetString());
        Assert.Equal(requestedAt, replica.GetProperty("deleteRequestedAt").GetDateTimeOffset());
        Assert.Equal(requestedAt + DefaultGrace, replica.GetProperty("deleteAfter").GetDateTimeOffset());
        Assert.True(IsNull(replica, "deletedAt"));

        clock.Advance(TimeSpan.FromMinutes(10));
        using var second = await DeleteArtifactAsync(scenario, backup.ArtifactId);

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(requestedAt + DefaultGrace, ReplicaOf(AgentWorld.ReadJson(second)).GetProperty("deleteAfter").GetDateTimeOffset()); // the grace is not extended
    }

    [Fact]
    public async Task CancellingADeleteRestoresTheBackupAndThereIsNothingToCancelOtherwise()
    {
        await using var scenario = await CreateAsync(fixture);
        var backup = await AcceptBackupAsync(scenario, await CreateJobAsync(scenario));

        using var nothingPending = await CancelDeleteAsync(scenario, backup.ArtifactId);
        Assert.Equal(HttpStatusCode.Conflict, nothingPending.StatusCode);
        Assert.Equal("not_cancellable", AgentWorld.ReadJson(nothingPending).GetProperty("error").GetString());

        using (await DeleteArtifactAsync(scenario, backup.ArtifactId)) { }
        using var restored = await CancelDeleteAsync(scenario, backup.ArtifactId);

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var artifact = AgentWorld.ReadJson(restored);
        Assert.Equal("Available", artifact.GetProperty("state").GetString());
        var replica = ReplicaOf(artifact);
        Assert.Equal("Available", replica.GetProperty("status").GetString());
        Assert.True(IsNull(replica, "deleteAfter"));
        Assert.True(IsNull(artifact, "retentionPinnedAt")); // it had not expired: nothing to pin

        using var again = await CancelDeleteAsync(scenario, backup.ArtifactId);
        using var unknown = await CancelDeleteAsync(scenario, Guid.NewGuid());
        using var unknownDelete = await DeleteArtifactAsync(scenario, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownDelete.StatusCode);
    }

    private static (bool Enabled, double Keep, double Grace) Read(JsonElement view)
    {
        var retention = view.GetProperty("retention");
        return (retention.GetProperty("enabled").GetBoolean(), retention.GetProperty("keepForSeconds").GetDouble(), retention.GetProperty("deleteGracePeriodSeconds").GetDouble());
    }
}
