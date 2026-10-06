using System.Net;
using System.Text.Json;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Tenancy;
using static BackupPlatform.IntegrationTests.BackupApiScenario;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// Retry, overlap and lease fencing as an operator and an agent see them over HTTP. Time is the scenario's MutableClock; the retry
/// scan and the stale-lease sweep are driven directly, and every assertion about their effect reads the run back (the host's own
/// background workers share the clock and may get there first).
/// </summary>
public sealed class BackupRetryApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    // Deterministic delays: 30 s between attempts, no jitter.
    private static readonly object FixedThirtySeconds = new { maxAttempts = 3, initialDelaySeconds = 30.0, maxDelaySeconds = 600.0, backoff = "Fixed", jitter = 0.0 };

    // ---- the retry endpoint: who may call it, and what it refuses ----

    [Theory]
    [InlineData(TenantRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(TenantRole.Operator, HttpStatusCode.OK)]
    [InlineData(TenantRole.Admin, HttpStatusCode.OK)]
    public async Task RetryIsOpenToOperatorsAndAdminsButNotViewers(TenantRole role, HttpStatusCode expected)
    {
        await using var scenario = await CreateAsync(fixture);
        var run = await FailedRunAsync(scenario, "PreCheckFailed");
        var session = await scenario.SessionAsync(role);

        using var response = await scenario.RetryRawAsync(run, session);

        Assert.Equal(expected, response.StatusCode);
        var after = await scenario.RunAsync(run);
        var allowed = expected == HttpStatusCode.OK;
        Assert.Equal(allowed ? 2 : 1, AttemptsOf(after).Count);
        Assert.Equal(allowed ? "Pending" : "Failed", StatusOf(after));
    }

    [Fact]
    public async Task RetryIsRefusedToAnonymousCallersAndHiddenFromOtherTenants()
    {
        await using var scenario = await CreateAsync(fixture);
        var run = await FailedRunAsync(scenario, "PreCheckFailed");
        var other = await scenario.World.CreateTenantAsync();

        using var anonymous = await scenario.World.Anonymous.PostAsync(scenario.Root + $"/backup-runs/{run}/retry", content: null);
        using var ownRoute = await other.Admin.SendAsync(HttpMethod.Post, RootOf(other) + $"/backup-runs/{run}/retry");
        using var victimRoute = await other.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/backup-runs/{run}/retry");
        using var unknown = await scenario.RetryRawAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ownRoute.StatusCode); // the run id means nothing in another tenant
        Assert.Equal(HttpStatusCode.Forbidden, victimRoute.StatusCode); // not a member of that tenant at all
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var after = await scenario.RunAsync(run);
        Assert.Equal(("Failed", 1), (StatusOf(after), AttemptsOf(after).Count));
    }

    [Theory]
    [InlineData("succeeded", "succeeded")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("pending", "in_progress")]
    [InlineData("running", "in_progress")]
    [InlineData("exhausted", "attempts_exhausted")]
    [InlineData("overlap", "job_overlap")]
    public async Task RefusedRetryIsAConflictThatNamesTheReasonAndChangesNothing(string situation, string reason)
    {
        await using var scenario = await CreateAsync(fixture);
        var run = situation switch
        {
            "succeeded" => await StartedRunAsync(scenario, _ => scenario.SucceedNextAsync()),
            "cancelled" => await StartedRunAsync(scenario, async id =>
            {
                using var cancel = await scenario.CancelRawAsync(id);
                Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            }),
            "pending" => await StartedRunAsync(scenario, _ => Task.CompletedTask),
            "running" => await StartedRunAsync(scenario, async _ => await scenario.StartAttemptAsync(await scenario.ClaimAsync())),
            "exhausted" => await ExhaustedRunAsync(scenario),
            _ => await OverlappedRunAsync(scenario)
        };
        var before = await scenario.RunAsync(run);

        using var response = await scenario.RetryRawAsync(run);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = AgentWorld.ReadJson(response);
        Assert.Equal("retry_not_allowed", body.GetProperty("error").GetString());
        Assert.Equal(reason, body.GetProperty("reason").GetString());
        var after = await scenario.RunAsync(run);
        Assert.Equal((StatusOf(before), AttemptsOf(before).Count), (StatusOf(after), AttemptsOf(after).Count));
    }

    // ---- a manual retry ----

    [Theory]
    [InlineData("PreCheckFailed", "Failed")]           // not retryable by itself: an operator may still decide to try again
    [InlineData("DatabaseUnavailable", "RetryPending")] // "retry now" instead of waiting for the backoff
    public async Task ManualRetryAddsAnAttemptToTheSameRunAndASecondImmediateRetryIsRefused(string failureCode, string statusBefore)
    {
        await using var scenario = await CreateAsync(fixture);
        var run = await FailedRunAsync(scenario, failureCode);
        Assert.Equal(statusBefore, StatusOf(await scenario.RunAsync(run)));

        using var retry = await scenario.RetryRawAsync(run);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var view = AgentWorld.ReadJson(retry);
        Assert.Equal(run, view.GetProperty("id").GetGuid());
        Assert.Equal("Pending", StatusOf(view));
        Assert.Equal(2, view.GetProperty("currentAttemptNumber").GetInt32());
        Assert.Equal(new[] { 1, 2 }, AttemptsOf(view).Select(attempt => attempt.GetProperty("number").GetInt32()).ToArray());
        Assert.Equal("Failed", AttemptsOf(view)[0].GetProperty("status").GetString()); // history is kept
        Assert.Equal(2, view.GetProperty("retry").GetProperty("attemptsUsed").GetInt32());
        Assert.True(IsNull(view.GetProperty("retry"), "nextAttemptAt"));

        using var again = await scenario.RetryRawAsync(run);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("in_progress", AgentWorld.ReadJson(again).GetProperty("reason").GetString());
        var persisted = await scenario.RunAsync(run);
        Assert.Equal(2, AttemptsOf(persisted).Count);
        var task = await scenario.ClaimAsync();
        Assert.Equal((run, 2, AttemptsOf(persisted)[1].GetProperty("id").GetGuid()), (task.RunId, task.AttemptNumber, task.AttemptId));
    }

    [Fact]
    public async Task ConcurrentRetryRequestsAddExactlyOneAttempt()
    {
        await using var scenario = await CreateAsync(fixture);
        var run = await FailedRunAsync(scenario, "PreCheckFailed");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var response = await scenario.RetryRawAsync(run);
            return (response.StatusCode, Reason: response.StatusCode == HttpStatusCode.Conflict ? AgentWorld.ReadJson(response).GetProperty("reason").GetString() : null);
        })));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.OK), response =>
            Assert.Equal(("in_progress", HttpStatusCode.Conflict), (response.Reason, response.StatusCode)));
        Assert.Equal(2, AttemptsOf(await scenario.RunAsync(run)).Count);
    }

    // ---- job policies ----

    [Fact]
    public async Task JobRetryAndOverlapPoliciesRoundTripAndDefaultWhenOmitted()
    {
        await using var scenario = await CreateAsync(fixture);
        var explicitPolicy = new { maxAttempts = 5, initialDelaySeconds = 10.0, maxDelaySeconds = 120.0, backoff = "Fixed", jitter = 0.5 };

        using var created = await scenario.CreateJobRawAsync(JobBody(scenario.Agent.AgentId, explicitPolicy, "queue"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        using var read = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{jobId}");
        foreach (var job in new[] { AgentWorld.ReadJson(created), AgentWorld.ReadJson(read) })
        {
            var policy = job.GetProperty("retryPolicy");
            Assert.Equal((5, 10.0, 120.0, "Fixed", 0.5), (policy.GetProperty("maxAttempts").GetInt32(), policy.GetProperty("initialDelaySeconds").GetDouble(),
                policy.GetProperty("maxDelaySeconds").GetDouble(), policy.GetProperty("backoff").GetString(), policy.GetProperty("jitter").GetDouble()));
            Assert.Equal("Queue", job.GetProperty("overlapPolicy").GetString());
        }

        // A run snapshots the policy it will execute under.
        var runView = await scenario.RunAsync(await scenario.StartRunAsync(jobId));
        var retry = runView.GetProperty("retry");
        Assert.Equal(5, retry.GetProperty("maxAttempts").GetInt32());
        Assert.Equal("Queue", retry.GetProperty("overlapPolicy").GetString());
        Assert.Equal("Fixed", retry.GetProperty("policy").GetProperty("backoff").GetString());

        // Omitted: the platform defaults. A partial policy keeps the defaults for what it leaves out.
        var defaults = await ReadJobAsync(scenario, await scenario.CreateJobAsync());
        Assert.Equal(3, defaults.GetProperty("retryPolicy").GetProperty("maxAttempts").GetInt32());
        Assert.Equal("Exponential", defaults.GetProperty("retryPolicy").GetProperty("backoff").GetString());
        Assert.Equal("Skip", defaults.GetProperty("overlapPolicy").GetString());
        var partial = await ReadJobAsync(scenario, await scenario.CreateJobAsync(new { maxAttempts = 2 }));
        Assert.Equal(2, partial.GetProperty("retryPolicy").GetProperty("maxAttempts").GetInt32());
        foreach (var member in new[] { "initialDelaySeconds", "maxDelaySeconds", "backoff", "jitter" })
            Assert.Equal(defaults.GetProperty("retryPolicy").GetProperty(member).ToString(), partial.GetProperty("retryPolicy").GetProperty(member).ToString());
    }

    [Fact]
    public async Task InvalidPoliciesAreRejectedAndTheBoundariesAreAccepted()
    {
        await using var scenario = await CreateAsync(fixture);
        var rejected = new (string Policy, string? Overlap)[]
        {
            ("""{"maxAttempts":0}""", null), ("""{"maxAttempts":11}""", null), ("""{"maxAttempts":-1}""", null),
            ("""{"initialDelaySeconds":0.5}""", null), ("""{"initialDelaySeconds":0}""", null),
            ("""{"initialDelaySeconds":60,"maxDelaySeconds":30}""", null), ("""{"maxDelaySeconds":5}""", null), ("""{"maxDelaySeconds":90000}""", null),
            ("""{"jitter":1.5}""", null), ("""{"jitter":-0.1}""", null), ("""{"backoff":"Linear"}""", null),
            ("""{"backoff":"1"}""", null), ("""{"backoff":"2"}""", null), // names only: numeric text is not a policy
            ("null", "Replace"), ("null", ""), ("null", "1"), ("null", "2")
        };
        foreach (var (policy, overlap) in rejected)
        {
            using var response = await scenario.CreateJobRawAsync(JobBody(scenario.Agent.AgentId, JsonDocument.Parse(policy).RootElement, overlap));
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{policy} / {overlap} was answered {response.StatusCode}");
            Assert.Equal("invalid_request", AgentWorld.ReadJson(response).GetProperty("error").GetString());
        }

        var accepted = new[]
        {
            """{"maxAttempts":1}""", """{"maxAttempts":10}""", """{"initialDelaySeconds":1,"maxDelaySeconds":1}""",
            """{"initialDelaySeconds":1,"maxDelaySeconds":86400}""", """{"jitter":0}""", """{"jitter":1}""", """{"backoff":"exponential"}"""
        };
        foreach (var policy in accepted)
        {
            using var response = await scenario.CreateJobRawAsync(JobBody(scenario.Agent.AgentId, JsonDocument.Parse(policy).RootElement));
            Assert.True(response.StatusCode == HttpStatusCode.Created, $"{policy} was answered {response.StatusCode}");
        }
    }

    // ---- overlap ----

    [Fact]
    public async Task SkippedManualStartIsAConflictNamingTheActiveRunNeverASilentOk()
    {
        await using var scenario = await CreateAsync(fixture);
        var skipJob = await scenario.CreateJobAsync(); // Skip is the default
        var active = await scenario.StartRunAsync(skipJob, "first");

        using var skipped = await scenario.StartRawAsync(skipJob, "second");
        using var replay = await scenario.StartRawAsync(skipJob, "first");

        Assert.Equal(HttpStatusCode.Conflict, skipped.StatusCode);
        var body = AgentWorld.ReadJson(skipped);
        Assert.Equal("overlap_skipped", body.GetProperty("error").GetString());
        Assert.Equal(active, body.GetProperty("activeRunId").GetGuid());
        Assert.Equal((HttpStatusCode.OK, active), (replay.StatusCode, AgentWorld.ReadJson(replay).GetProperty("id").GetGuid())); // a replay is not a new start
        using var list = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{skipJob}/runs");
        Assert.Single(AgentWorld.ReadJson(list).GetProperty("items").EnumerateArray());

        using var cancel = await scenario.CancelRawAsync(active);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.NotEqual(active, await scenario.StartRunAsync(skipJob, "second")); // free again once the active run is over, same key as the skipped request

        var queueJob = await scenario.CreateJobAsync(overlapPolicy: "Queue");
        Assert.NotEqual(await scenario.StartRunAsync(queueJob, "one"), await scenario.StartRunAsync(queueJob, "two"));
    }

    // ---- what a run exposes ----

    [Fact]
    public async Task RunDetailExposesRetryMetadataAcrossTheLifecycleAndNeverLeaksTheLeaseToken()
    {
        await using var scenario = await CreateAsync(fixture);
        var job = await scenario.CreateJobAsync(FixedThirtySeconds);
        var run = await scenario.StartRunAsync(job);

        var fresh = await scenario.RunAsync(run);
        var retry = fresh.GetProperty("retry");
        Assert.Equal((3, 1, "None", "Skip"), (retry.GetProperty("maxAttempts").GetInt32(), retry.GetProperty("attemptsUsed").GetInt32(),
            retry.GetProperty("state").GetString(), retry.GetProperty("overlapPolicy").GetString()));
        Assert.True(IsNull(retry, "nextAttemptAt") && IsNull(retry, "lastFailureCode") && IsNull(retry, "lastFailureRetryable"));

        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);
        await AssertNoLeaseTokenAsync(scenario, run, claim); // the token exists now: this is where a leak would show

        var failedAt = scenario.Clock.GetUtcNow();
        using (var failure = await scenario.FailRawAsync(claim, "DatabaseUnavailable")) Assert.Equal(HttpStatusCode.NoContent, failure.StatusCode);
        var scheduled = await scenario.RunAsync(run);
        retry = scheduled.GetProperty("retry");
        Assert.Equal("RetryPending", StatusOf(scheduled));
        Assert.Equal("Scheduled", retry.GetProperty("state").GetString());
        Assert.Equal(failedAt.AddSeconds(30), retry.GetProperty("nextAttemptAt").GetDateTimeOffset());
        Assert.Equal(("DatabaseUnavailable", true), (retry.GetProperty("lastFailureCode").GetString(), retry.GetProperty("lastFailureRetryable").GetBoolean()));
        Assert.False(string.IsNullOrWhiteSpace(retry.GetProperty("lastFailureReason").GetString()));
        Assert.True(AttemptsOf(scheduled)[0].GetProperty("retryable").GetBoolean());

        scenario.Clock.Advance(TimeSpan.FromSeconds(30));
        await scenario.ScanRetriesAsync();
        var second = await scenario.FailNextAsync("PreCheckFailed");
        var terminal = await scenario.RunAsync(run);
        retry = terminal.GetProperty("retry");
        Assert.Equal(("Failed", "NotRetryable", 2), (StatusOf(terminal), retry.GetProperty("state").GetString(), retry.GetProperty("attemptsUsed").GetInt32()));
        Assert.Equal(("PreCheckFailed", false), (retry.GetProperty("lastFailureCode").GetString(), retry.GetProperty("lastFailureRetryable").GetBoolean()));
        Assert.True(IsNull(retry, "nextAttemptAt"));
        Assert.Equal(new[] { true, false }, AttemptsOf(terminal).Select(attempt => attempt.GetProperty("retryable").GetBoolean()).ToArray());
        await AssertNoLeaseTokenAsync(scenario, run, second);

        // Retryable, but with the budget spent: the run says so.
        var single = await scenario.CreateJobAsync(new { maxAttempts = 1 });
        var exhausted = await scenario.StartRunAsync(single);
        await scenario.FailNextAsync("OperationTimedOut");
        var exhaustedView = await scenario.RunAsync(exhausted);
        Assert.Equal(("Failed", "Exhausted", true), (StatusOf(exhaustedView), exhaustedView.GetProperty("retry").GetProperty("state").GetString(),
            exhaustedView.GetProperty("retry").GetProperty("lastFailureRetryable").GetBoolean()));
    }

    // ---- what an agent may report ----

    [Fact]
    public async Task AgentCompletionAcceptsTheWholeFailureVocabularyAndRejectsAnythingElse()
    {
        await using var scenario = await CreateAsync(fixture);
        var job = await scenario.CreateJobAsync();
        var run = await scenario.StartRunAsync(job);
        var claim = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(claim);

        var rejected = new (string Status, string? Code)[]
        {
            ("Failed", "Bogus"), ("Failed", ""), ("Failed", null), ("Failed", "Password=raw-diagnostic"),
            ("Failed", "AgentLost"),   // only the backend can say the agent was lost
            ("Failed", "Cancelled"),   // cancellation is reported as a cancellation
            ("Cancelled", "DatabaseUnavailable"), ("Succeeded", "DatabaseUnavailable"), ("Paused", null)
        };
        foreach (var (status, code) in rejected)
        {
            using var response = await scenario.CompleteRawAsync(claim, new BackupTaskCompletionRequest { LeaseToken = claim.LeaseToken, Status = status, FailureCode = code });
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{status}/{code} was answered {response.StatusCode}");
        }
        Assert.Equal(("Running", 1), (StatusOf(await scenario.RunAsync(run)), AttemptsOf(await scenario.RunAsync(run)).Count)); // none of it touched the attempt

        using var accepted = await scenario.FailRawAsync(claim, "DatabaseUnavailable"); // a code introduced with retries
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal("RetryPending", StatusOf(await scenario.RunAsync(run)));

        // Every code the agent can send is understood, and the backend, not the agent, decides what is worth retrying.
        var retryable = new HashSet<string> { "DatabaseUnavailable", "OperationTimedOut", "StorageUnavailable", "ReplicaVerificationFailed" };
        var vocabulary = new[]
        {
            "PreCheckFailed", "SqlBackupFailed", "VerificationFailed", "HashFailed", "StorageWriteFailed", "ReplicaVerificationFailed", "InternalError",
            "DatabaseUnavailable", "OperationTimedOut", "StorageUnavailable", "DatabaseNotFound", "InvalidConfiguration", "UnsupportedCapability",
            "AuthenticationFailed", "PermissionDenied", "StagingInvalid"
        };
        var singleAttempt = await scenario.CreateJobAsync(new { maxAttempts = 1 });
        foreach (var code in vocabulary)
        {
            var codeRun = await scenario.StartRunAsync(singleAttempt);
            await scenario.FailNextAsync(code);
            var view = await scenario.RunAsync(codeRun);
            var attempt = Assert.Single(AttemptsOf(view));
            Assert.Equal(code, attempt.GetProperty("reasonCode").GetString());
            Assert.Equal(retryable.Contains(code), attempt.GetProperty("retryable").GetBoolean());
            Assert.Equal(("Failed", retryable.Contains(code) ? "Exhausted" : "NotRetryable"), (StatusOf(view), view.GetProperty("retry").GetProperty("state").GetString()));
        }
    }

    // ---- stale and superseded attempts ----

    [Fact]
    public async Task LateCompletionOfALostAttemptIsALeaseConflictAndNeverTouchesTheRetry()
    {
        await using var scenario = await CreateAsync(fixture);
        var run = await scenario.StartRunAsync(await scenario.CreateJobAsync(FixedThirtySeconds));
        var lost = await scenario.ClaimAsync();
        await scenario.StartAttemptAsync(lost);

        scenario.Clock.Advance(TimeSpan.FromSeconds(121)); // the 120 s lease ran out without a renewal
        await scenario.ExpireStaleAsync();
        var afterLoss = await scenario.RunAsync(run);
        Assert.Equal(("RetryPending", "AgentLost"), (StatusOf(afterLoss), AttemptsOf(afterLoss)[0].GetProperty("status").GetString()));
        await AssertEveryCallbackRefusedAsync(scenario, lost);
        Assert.Empty(AttemptsOf(await scenario.RunAsync(run))[0].GetProperty("artifacts").EnumerateArray());

        scenario.Clock.Advance(TimeSpan.FromSeconds(30));
        await scenario.ScanRetriesAsync();
        var retried = await scenario.RunAsync(run);
        Assert.Equal(("Pending", 2), (StatusOf(retried), AttemptsOf(retried).Count));
        await AssertEveryCallbackRefusedAsync(scenario, lost); // now superseded as well as expired
        var untouched = await scenario.RunAsync(run);
        Assert.Equal(("Pending", "Pending"), (StatusOf(untouched), AttemptsOf(untouched)[1].GetProperty("status").GetString()));
        Assert.Empty(AttemptsOf(untouched)[1].GetProperty("artifacts").EnumerateArray());

        // The new attempt is fenced from the old one in both directions and then completes normally.
        var current = await scenario.ClaimAsync();
        Assert.Equal(2, current.AttemptNumber);
        using var oldTokenOnNew = await scenario.FailRawAsync(current, "InternalError", token: lost.LeaseToken);
        using var newTokenOnOld = await scenario.CompleteRawAsync(lost, scenario.SuccessRequest(lost, current.LeaseToken));
        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict), (oldTokenOnNew.StatusCode, newTokenOnOld.StatusCode));
        await scenario.StartAttemptAsync(current);
        using var done = await scenario.CompleteRawAsync(current, scenario.SuccessRequest(current));
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
        var final = await scenario.RunAsync(run);
        Assert.Equal("Succeeded", StatusOf(final));
        Assert.Equal(new[] { 0, 1 }, AttemptsOf(final).Select(attempt => attempt.GetProperty("artifacts").GetArrayLength()).ToArray()); // the artifact belongs to attempt 2 only
    }

    [Fact]
    public async Task SupersededAttemptCannotAffectItsSuccessorEvenWithItsOwnValidToken()
    {
        await using var scenario = await CreateAsync(fixture);
        var run = await scenario.StartRunAsync(await scenario.CreateJobAsync());
        var first = await scenario.FailNextAsync("PreCheckFailed");
        using (var retry = await scenario.RetryRawAsync(run)) Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var second = await scenario.ClaimAsync();

        await AssertEveryCallbackRefusedAsync(scenario, first);
        using var repeatedFailure = await scenario.FailRawAsync(first, "PreCheckFailed"); // even the exact repeat of what it already reported
        Assert.Equal(HttpStatusCode.Conflict, repeatedFailure.StatusCode);

        await scenario.StartAttemptAsync(second);
        using var progress = await scenario.ProgressRawAsync(second);
        Assert.Equal(HttpStatusCode.NoContent, progress.StatusCode);
        using var done = await scenario.CompleteRawAsync(second, scenario.SuccessRequest(second));
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
        var final = await scenario.RunAsync(run);
        Assert.Equal("Succeeded", StatusOf(final));
        Assert.Equal(("Failed", "PreCheckFailed"), (AttemptsOf(final)[0].GetProperty("status").GetString(), AttemptsOf(final)[0].GetProperty("reasonCode").GetString()));
    }

    // ---- the retry worker and cancellation ----

    [Fact]
    public async Task RetryWorkerTurnsADueRetryPendingRunIntoAPollableSecondAttempt()
    {
        await using var scenario = await CreateAsync(fixture);
        var run = await scenario.StartRunAsync(await scenario.CreateJobAsync(FixedThirtySeconds));
        await scenario.FailNextAsync("DatabaseUnavailable");
        Assert.Null(await scenario.PollAsync()); // nothing to run while the backoff lasts

        scenario.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, await scenario.ScanRetriesAsync()); // one second early: nothing is due, whoever scans
        Assert.Equal("RetryPending", StatusOf(await scenario.RunAsync(run)));
        Assert.Null(await scenario.PollAsync());

        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        await scenario.ScanRetriesAsync();
        var due = await scenario.RunAsync(run);
        Assert.Equal(("Pending", 2, "None"), (StatusOf(due), AttemptsOf(due).Count, due.GetProperty("retry").GetProperty("state").GetString()));
        var task = await scenario.ClaimAsync();
        Assert.Equal((run, 2, AttemptsOf(due)[1].GetProperty("id").GetGuid()), (task.RunId, task.AttemptNumber, task.AttemptId));
        Assert.Equal(0, await scenario.ScanRetriesAsync()); // nothing left to start: scanning again never adds another attempt
        Assert.Equal(2, AttemptsOf(await scenario.RunAsync(run)).Count);
    }

    [Fact]
    public async Task CancellingARetryPendingRunWithdrawsTheRetryForGood()
    {
        await using var scenario = await CreateAsync(fixture);
        var run = await scenario.StartRunAsync(await scenario.CreateJobAsync(FixedThirtySeconds));
        await scenario.FailNextAsync("OperationTimedOut");

        using var cancel = await scenario.CancelRawAsync(run);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelled = AgentWorld.ReadJson(cancel);
        Assert.Equal(("Cancelled", "None"), (StatusOf(cancelled), cancelled.GetProperty("retry").GetProperty("state").GetString()));
        Assert.True(IsNull(cancelled.GetProperty("retry"), "nextAttemptAt"));
        scenario.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await scenario.ScanRetriesAsync());
        var after = await scenario.RunAsync(run);
        Assert.Equal(("Cancelled", 1), (StatusOf(after), AttemptsOf(after).Count));
        Assert.Null(await scenario.PollAsync());
        using var retry = await scenario.RetryRawAsync(run);
        Assert.Equal("cancelled", AgentWorld.ReadJson(retry).GetProperty("reason").GetString());
    }

    // ---- helpers ----

    private static async Task<Guid> FailedRunAsync(BackupApiScenario scenario, string code, object? retryPolicy = null, string? overlap = null)
    {
        var run = await scenario.StartRunAsync(await scenario.CreateJobAsync(retryPolicy, overlap));
        await scenario.FailNextAsync(code);
        return run;
    }

    private static async Task<Guid> StartedRunAsync(BackupApiScenario scenario, Func<Guid, Task> advance)
    {
        var run = await scenario.StartRunAsync(await scenario.CreateJobAsync());
        await advance(run);
        return run;
    }

    private static async Task<Guid> ExhaustedRunAsync(BackupApiScenario scenario)
    {
        var run = await FailedRunAsync(scenario, "PreCheckFailed", new { maxAttempts = 2 });
        using var retry = await scenario.RetryRawAsync(run);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await scenario.FailNextAsync("PreCheckFailed");
        return run;
    }

    private static async Task<Guid> OverlappedRunAsync(BackupApiScenario scenario)
    {
        var job = await scenario.CreateJobAsync(); // Skip
        var failed = await scenario.StartRunAsync(job);
        await scenario.FailNextAsync("PreCheckFailed");
        await scenario.StartRunAsync(job); // allowed: the first run is over; it now occupies the job
        return failed;
    }

    private static async Task<JsonElement> ReadJobAsync(BackupApiScenario scenario, Guid jobId)
    {
        using var response = await scenario.Admin.GetAsync(scenario.Root + $"/backup-jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private static bool IsNull(JsonElement element, string name) => !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    private static async Task AssertNoLeaseTokenAsync(BackupApiScenario scenario, Guid run, TaskClaim claim)
    {
        var jobId = (await scenario.RunAsync(run)).GetProperty("jobId").GetGuid();
        foreach (var path in new[] { $"/backup-runs/{run}", "/backup-runs", $"/backup-jobs/{jobId}/runs" })
        {
            using var response = await scenario.Admin.GetAsync(scenario.Root + path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("leaseToken", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(claim.LeaseToken.ToString(), body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Start, renew, progress and complete (failure and success) with the attempt's own, once valid, token: all lease_conflict.</summary>
    private static async Task AssertEveryCallbackRefusedAsync(BackupApiScenario scenario, TaskClaim stale)
    {
        using var start = await scenario.StartAttemptRawAsync(stale);
        using var renew = await scenario.RenewRawAsync(stale);
        using var progress = await scenario.ProgressRawAsync(stale);
        using var failure = await scenario.FailRawAsync(stale, "InternalError");
        using var success = await scenario.CompleteRawAsync(stale, scenario.SuccessRequest(stale));
        foreach (var response in new[] { start, renew, progress, failure, success })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("lease_conflict", AgentWorld.ReadJson(response).GetProperty("error").GetString());
        }
    }
}
