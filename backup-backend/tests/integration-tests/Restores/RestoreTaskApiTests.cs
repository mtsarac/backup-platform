using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using static BackupPlatform.IntegrationTests.RestoreApiScenario;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// The target agent's endpoints for a claimed restore: every call is fenced by the attempt, the lease token and the agent's own identity, and a malformed report is
/// a bad request, never a lifecycle conflict.
/// </summary>
public sealed class RestoreTaskApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static async Task<(RestoreApiScenario Scenario, Guid RestoreId, RestoreTask Task)> ClaimedAsync(PostgreSqlFixture fixture, bool started)
    {
        var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        var restoreId = (await scenario.StartAsync(backup.ArtifactId)).GetProperty("id").GetGuid();
        var task = await scenario.MustPollRestoreAsync();
        if (started)
        {
            using var response = await scenario.StartAttemptRawAsync(task);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        return (scenario, restoreId, task);
    }

    private static string Error(HttpResponseMessage response) => AgentWorld.ReadJson(response).GetProperty("error").GetString()!;

    [Fact]
    public async Task EveryCallWithAWrongLeaseTokenIsALeaseConflictAndChangesNothing()
    {
        var (scenario, restoreId, task) = await ClaimedAsync(fixture, started: true);
        await using var _ = scenario;
        var wrong = Guid.NewGuid();

        using var start = await scenario.StartAttemptRawAsync(task, wrong);
        using var renew = await scenario.RenewRawAsync(task, wrong);
        using var progress = await scenario.ProgressRawAsync(task, token: wrong);
        using var complete = await scenario.CompleteRawAsync(task, Completion(task, token: wrong));

        foreach (var response in new[] { start, renew, progress, complete })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("lease_conflict", Error(response));
        }
        Assert.Equal("Running", StatusOf(await scenario.RestoreAsync(restoreId)));
    }

    [Fact]
    public async Task AnUnknownAttemptIsNotFoundForEveryCall()
    {
        var (scenario, _, task) = await ClaimedAsync(fixture, started: false);
        await using var _ = scenario;
        var unknown = task with { AttemptId = Guid.NewGuid() };

        using var start = await scenario.StartAttemptRawAsync(unknown);
        using var renew = await scenario.RenewRawAsync(unknown);
        using var progress = await scenario.ProgressRawAsync(unknown);
        using var complete = await scenario.CompleteRawAsync(unknown, Completion(unknown));

        Assert.All(new[] { start, renew, progress, complete }, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
    }

    [Fact]
    public async Task AnotherAgentOfTheTenantAndAnAgentOfAnotherTenantCannotTouchTheAttempt()
    {
        var (scenario, restoreId, task) = await ClaimedAsync(fixture, started: true);
        await using var _ = scenario;
        var sibling = await scenario.AddAgentAsync();
        var otherTenant = await scenario.World.CreateTenantAsync();
        var foreign = await scenario.World.EnrollAsync(otherTenant);

        foreach (var intruder in new[] { sibling, foreign })
        {
            using var start = await scenario.AgentCallAsync(task.AttemptId, "start", new { leaseToken = task.LeaseToken, softwareVersion = "1", protocolVersion = "1.0", platform = "linux" }, intruder);
            using var renew = await scenario.AgentCallAsync(task.AttemptId, "lease", new { leaseToken = task.LeaseToken }, intruder);
            using var progress = await scenario.AgentCallAsync(task.AttemptId, "progress", new { leaseToken = task.LeaseToken, stage = "Restoring", percent = 1 }, intruder);
            using var complete = await scenario.CompleteRawAsync(task, Completion(task), intruder);
            Assert.All(new[] { start, renew, progress, complete }, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        }
        Assert.Equal("Running", StatusOf(await scenario.RestoreAsync(restoreId)));
    }

    [Fact]
    public async Task StorageAccess_RejectsAnotherAgentEvenWithTheCorrectRestoreLease()
    {
        var (scenario, _, task) = await ClaimedAsync(fixture, started: true);
        await using var _ = scenario;
        var sibling = await scenario.AddAgentAsync();
        foreach (var caller in new[] { scenario.Agent, sibling })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/restore-tasks/{task.AttemptId}/storage/access");
            request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(caller.CredentialId, caller.Secret));
            request.Headers.Add("X-Backup-Agent-Protocol", "1.0");
            request.Headers.Add(TaskStorageProtocol.LeaseHeader, task.LeaseToken.ToString("D"));
            request.Headers.Add(TaskStorageProtocol.RestoreHeader, task.OperationId.ToString("D"));

            using var response = await scenario.World.Anonymous.SendAsync(request);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            // The owner passes the lease fence; this fixture's central replica is intentionally not a direct external source.
            Assert.Equal(caller == scenario.Agent ? "source_inaccessible" : "lease_conflict", Error(response));
        }
    }

    [Fact]
    public async Task ABrowserSessionCannotCallAnAgentEndpoint()
    {
        var (scenario, restoreId, task) = await ClaimedAsync(fixture, started: true);
        await using var _ = scenario;

        using var response = await scenario.Admin.SendAsync(HttpMethod.Post, $"/api/agent/restores/{task.AttemptId}/complete", Completion(task));

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        Assert.Equal("Running", StatusOf(await scenario.RestoreAsync(restoreId)));
    }

    [Fact]
    public async Task AnAgentCallWithoutTheProtocolHeaderIsRejected()
    {
        var (scenario, _, task) = await ClaimedAsync(fixture, started: false);
        await using var _ = scenario;

        using var response = await scenario.World.SendAgentAsync(HttpMethod.Post, $"/api/agent/restores/{task.AttemptId}/lease", scenario.Agent.CredentialId, scenario.Agent.Secret,
            protocol: null, body: new { leaseToken = task.LeaseToken });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProgressNeedsARunningAttemptAndARestoreStage()
    {
        var (scenario, _, task) = await ClaimedAsync(fixture, started: false);
        await using var _ = scenario;

        using var beforeStart = await scenario.ProgressRawAsync(task);
        using (var started = await scenario.StartAttemptRawAsync(task)) Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var running = await scenario.ProgressRawAsync(task, "DownloadingArtifact", 0);
        using var backupStage = await scenario.ProgressRawAsync(task, "BackingUp");
        using var terminalStage = await scenario.ProgressRawAsync(task, "Completed");
        using var failedStage = await scenario.ProgressRawAsync(task, "Failed");
        using var percent = await scenario.ProgressRawAsync(task, "Restoring", 100.5);
        using var emptyToken = await scenario.ProgressRawAsync(task, token: Guid.Empty);

        Assert.Equal(HttpStatusCode.Conflict, beforeStart.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, running.StatusCode);
        foreach (var response in new[] { backupStage, terminalStage, failedStage, percent, emptyToken })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_request", Error(response));
        }
    }

    [Fact]
    public async Task StartingWithMissingMetadataOrAnEmptyTokenIsABadRequest()
    {
        var (scenario, restoreId, task) = await ClaimedAsync(fixture, started: false);
        await using var _ = scenario;

        using var noSoftware = await scenario.AgentCallAsync(task.AttemptId, "start", new { leaseToken = task.LeaseToken, softwareVersion = "", protocolVersion = "1.0", platform = "linux" });
        using var longPlatform = await scenario.AgentCallAsync(task.AttemptId, "start", new { leaseToken = task.LeaseToken, softwareVersion = "1", protocolVersion = "1.0", platform = new string('x', 33) });
        using var emptyToken = await scenario.AgentCallAsync(task.AttemptId, "start", new { leaseToken = Guid.Empty, softwareVersion = "1", protocolVersion = "1.0", platform = "linux" });
        using var emptyRenewToken = await scenario.RenewRawAsync(task, Guid.Empty);

        Assert.All(new[] { noSoftware, longPlatform, emptyToken, emptyRenewToken }, response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        Assert.Equal("Leased", StatusOf(await scenario.RestoreAsync(restoreId)));
    }

    [Theory]
    [InlineData("Succeeded", "PartiallyRestored", null)]
    [InlineData("Succeeded", "Online", "SqlRestoreFailed")]
    [InlineData("Succeeded", "NotModified", null)]
    [InlineData("Failed", "Online", "SqlRestoreFailed")]
    [InlineData("Failed", "NotModified", null)]
    [InlineData("Failed", "NotModified", "Cancelled")]
    [InlineData("Failed", "NotModified", "AgentLost")]
    [InlineData("Failed", "NotModified", "NotClaimed")]
    [InlineData("Failed", "NotModified", "MadeUp")]
    [InlineData("Cancelled", "Online", null)]
    [InlineData("Cancelled", "NotModified", "SqlRestoreFailed")]
    [InlineData("AgentLost", "Unknown", "AgentLost")]
    [InlineData("Pending", "NotModified", null)]
    [InlineData("Succeeded", "2", null)]
    [InlineData("Succeeded", "online", null)]
    [InlineData("Failed", "", "SqlRestoreFailed")]
    public async Task AMalformedCompletionIsABadRequestAndLeavesTheRestoreRunning(string status, string targetState, string? failureCode)
    {
        var (scenario, restoreId, task) = await ClaimedAsync(fixture, started: true);
        await using var _ = scenario;

        using var response = await scenario.CompleteRawAsync(task, Completion(task, status, targetState, failureCode));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", Error(response));
        Assert.Equal("Running", StatusOf(await scenario.RestoreAsync(restoreId)));
    }

    [Fact]
    public async Task AMalformedResultIsABadRequestAndNothingOfItIsStored()
    {
        var (scenario, restoreId, task) = await ClaimedAsync(fixture, started: true);
        await using var _ = scenario;
        var preChecks = Enumerable.Range(0, 65).Select(index => new { checkId = $"check-{index}", status = "Passed", reasonCode = "ok", message = "fine" }).ToArray();
        var bad = new object[]
        {
            new { preCheck = preChecks },
            new { preCheck = new[] { new { checkId = "x", status = "Maybe", reasonCode = "ok", message = "fine" } } },
            new { preCheck = new[] { new { checkId = "x", status = "Passed", reasonCode = "not ok!", message = "fine" } } },
            new { warnings = new[] { new { code = "w", message = "bad \u0000 text" } } },
            new { databaseState = "ONLINE\nINJECTED" },
            new { targetStateNote = new string('x', 513) },
            new { fileMappings = new[] { new { logicalName = "", targetPhysicalName = "/x", sizeBytes = 1 } } },
            new { sourceBackup = new { databaseName = "master", position = -1 } }
        };

        foreach (var result in bad)
        {
            using var response = await scenario.CompleteRawAsync(task, Completion(task, result: result));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var view = await scenario.RestoreAsync(restoreId);
        Assert.Equal("Running", StatusOf(view));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task AReportOverTheSizeLimitIsRefusedAndNothingOfItIsStored()
    {
        var (scenario, restoreId, task) = await ClaimedAsync(fixture, started: true);
        await using var _ = scenario;
        var huge = new { warnings = Enumerable.Range(0, 64).Select(index => new { code = $"w{index}", message = new string('m', 512) }).ToArray(), preCheck = Enumerable.Range(0, 64)
            .Select(index => new { checkId = $"c{index}", status = "Passed", reasonCode = "ok", message = new string('m', 512) }).ToArray(), padding = new string('p', 8 * 1024) };

        using var response = await scenario.CompleteRawAsync(task, Completion(task, result: huge));

        // Over real sockets Kestrel answers 413 before the body is read; the in-memory test server surfaces the same refusal as a failed body read (400). Either way nothing is stored.
        Assert.True(response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, $"Unexpected status {response.StatusCode}.");
        Assert.Equal("Running", StatusOf(await scenario.RestoreAsync(restoreId)));
    }

    [Fact]
    public async Task RepeatingTheSameCompletionIsAcceptedAndADifferentOneIsAConflict()
    {
        var (scenario, restoreId, task) = await ClaimedAsync(fixture, started: true);
        await using var _ = scenario;

        using var first = await scenario.CompleteRawAsync(task, Completion(task, result: SampleResult()));
        var done = await scenario.RestoreAsync(restoreId);
        using var again = await scenario.CompleteRawAsync(task, Completion(task, result: SampleResult()));
        using var different = await scenario.CompleteRawAsync(task, Completion(task, "Failed", "NotModified", "SqlRestoreFailed"));
        using var otherResult = await scenario.CompleteRawAsync(task, Completion(task, result: new { databaseState = "OFFLINE" }));
        using var renew = await scenario.RenewRawAsync(task);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Equal("lease_conflict", Error(different));
        Assert.Equal(HttpStatusCode.Conflict, otherResult.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, renew.StatusCode);
        var after = await scenario.RestoreAsync(restoreId);
        Assert.Equal("Succeeded", StatusOf(after));
        Assert.Equal(done.GetProperty("completedAt").GetDateTimeOffset(), after.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(done.GetProperty("result").GetRawText(), after.GetProperty("result").GetRawText());
    }

    [Fact]
    public async Task ASuccessCannotBeReportedForAnAttemptThatNeverStarted()
    {
        var (scenario, restoreId, task) = await ClaimedAsync(fixture, started: false);
        await using var _ = scenario;

        using var success = await scenario.CompleteRawAsync(task, Completion(task));
        using var failure = await scenario.CompleteRawAsync(task, Completion(task, "Failed", "NotModified", "PreCheckFailed"));

        Assert.Equal(HttpStatusCode.Conflict, success.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, failure.StatusCode);
        Assert.Equal("Failed", StatusOf(await scenario.RestoreAsync(restoreId)));
    }

    [Fact]
    public async Task AnExpiredLeaseIsAConflictForEveryCallUntilTheSweepEndsTheRestore()
    {
        var (scenario, restoreId, task) = await ClaimedAsync(fixture, started: true);
        await using var _ = scenario;
        scenario.Clock.Advance(TimeSpan.FromSeconds(121));

        using var renew = await scenario.RenewRawAsync(task);
        using var progress = await scenario.ProgressRawAsync(task);
        using var complete = await scenario.CompleteRawAsync(task, Completion(task));

        Assert.All(new[] { renew, progress, complete }, response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
        await scenario.SweepAsync();
        var view = await scenario.RestoreAsync(restoreId, await scenario.SessionAsync(BackupPlatform.Domain.Tenancy.TenantRole.Admin));
        Assert.Equal("AgentLost", StatusOf(view));
    }

    [Fact]
    public async Task EachRenewalExtendsTheLeaseFromTheBackendClock()
    {
        var (scenario, _, task) = await ClaimedAsync(fixture, started: true);
        await using var _ = scenario;
        scenario.Clock.Advance(TimeSpan.FromSeconds(90));

        using var renewed = await scenario.RenewRawAsync(task);

        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.Equal(scenario.Clock.GetUtcNow() + TimeSpan.FromSeconds(120), AgentWorld.ReadJson(renewed).GetProperty("leaseExpiresAt").GetDateTimeOffset());
        scenario.Clock.Advance(TimeSpan.FromSeconds(100));
        using var stillValid = await scenario.RenewRawAsync(task);
        Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);
    }
}
