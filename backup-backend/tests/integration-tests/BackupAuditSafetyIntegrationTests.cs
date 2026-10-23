using System.Net;
using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.IntegrationTests.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class BackupAuditSafetyIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset Now = BackupStoreHarness.T0;

    [Fact]
    public async Task HttpMutationsAttributeTheUserAndDenyUnauthorizedOrForeignTenantWithoutAudit()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var jobId = await scenario.CreateJobAsync();
        var viewer = await scenario.SessionAsync(TenantRole.Viewer);
        using (var denied = await scenario.CreateJobRawAsync(BackupApiScenario.JobBody(scenario.Agent.AgentId), viewer))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await scenario.StartRawAsync(jobId, "viewer-denied", viewer))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var foreign = await scenario.World.CreateTenantAsync();
        using (var denied = await foreign.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/backup-jobs/{jobId}/disable"))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var update = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{jobId}", BackupApiScenario.JobBody(scenario.Agent.AgentId)))
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var runs = await Task.WhenAll(scenario.StartRunAsync(jobId, "same-key"), scenario.StartRunAsync(jobId, "same-key"));
        Assert.Equal(runs[0], runs[1]);
        using (var cancel = await scenario.CancelRawAsync(runs[0])) Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        using (var cancel = await scenario.CancelRawAsync(runs[0])) Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        using (var rejectedRetry = await scenario.RetryRawAsync(runs[0])) Assert.Equal(HttpStatusCode.Conflict, rejectedRetry.StatusCode);
        var events = await EventsAsync(scenario.Setup.Tenant.Id);
        var human = events.Where(e => e.TargetId == jobId.ToString("D") || e.TargetId == runs[0].ToString("D"))
            .Where(e => e.Action != AuditActions.BackupRunCompleted && e.Action != AuditActions.BackupRunAttemptCompleted).ToArray();
        Assert.Equal(3, human.Length);
        Assert.All(human, e => { Assert.Equal(scenario.Setup.Account.UserId, e.ActorUserId); Assert.Equal(AuditActorKind.User, e.ActorKind); });
        Assert.Single(human, e => e.Action == AuditActions.BackupJobCreated);
        Assert.Single(human, e => e.Action == AuditActions.BackupRunTriggered);
        Assert.Single(human, e => e.Action == AuditActions.BackupRunCancelRequested);
        Assert.Single(events, e => e.Action == AuditActions.BackupRunCompleted && e.TargetId == runs[0].ToString("D"));
        Assert.Single(events, e => e.Action == AuditActions.BackupRunAttemptCompleted && e.TargetId == runs[0].ToString("D"));
    }

    [Fact]
    public async Task OperatorManualRetryPreservesActualIdentityWithoutClaimingAdministratorAuthority()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var account = await scenario.World.SeedUserAsync();
        await scenario.World.GrantAsync(scenario.Setup.Tenant, account, TenantRole.Operator);
        var session = await scenario.World.SignInAsync(account);
        using var created = await scenario.CreateJobRawAsync(BackupApiScenario.JobBody(scenario.Agent.AgentId), session);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = AgentWorld.ReadJson(created).GetProperty("id").GetGuid();
        using var started = await scenario.StartRawAsync(jobId, "operator", session);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var runId = AgentWorld.ReadJson(started).GetProperty("id").GetGuid();
        await scenario.FailNextAsync(BackupFailureCodes.PreCheckFailed);
        using var retry = await scenario.RetryRawAsync(runId, session);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using var duplicate = await scenario.RetryRawAsync(runId, session);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var events = await EventsAsync(scenario.Setup.Tenant.Id);
        foreach (var action in new[] { AuditActions.BackupJobCreated, AuditActions.BackupRunTriggered, AuditActions.BackupRunRetryRequested })
        {
            var audit = Assert.Single(events, e => e.Action == action);
            Assert.Equal(account.UserId, audit.ActorUserId);
            Assert.Equal(AuditActorKind.User, audit.ActorKind);
        }
    }

    [Fact]
    public async Task ForeignTenantTargetsCannotMutateOrEmitSuccessfulAudit()
    {
        var kit = new ScheduleKit(fixture);
        var agent = await kit.Runs.AddAgentAsync();
        var foreignTenant = await kit.Runs.AddTenantAsync();
        var actor = await AddActorAsync();
        var job = await kit.AddJobAsync(agent);
        var store = kit.Runs.Store();
        var run = await kit.Runs.StartRunAsync(store, job, "original", Now);
        var before = (await EventsAsync(agent.TenantId)).Select(e => e.Id).Order().ToArray();
        Assert.Null(await store.UpdateJobAsync(foreignTenant, job.Id, new BackupJobUpdate("Foreign", agent.AgentId, job.Configuration, null, null), default, actor, Now));
        Assert.Equal(BackupStartOutcome.NotFound, (await store.StartRunAsync(foreignTenant, job.Id, "foreign", Now, default, actor)).Outcome);
        Assert.Null(await store.RequestCancellationAsync(foreignTenant, run.Id, Now, default, actor));
        Assert.Equal(BackupRetryOutcome.NotFound, (await store.RetryRunAsync(foreignTenant, run.Id, Now, default, actor)).Outcome);
        Assert.Null(await kit.Store().ModifyJobAsync(foreignTenant, job.Id, j => j.Disable(), default, actor, Now));
        Assert.Equal(before, (await EventsAsync(agent.TenantId)).Select(e => e.Id).Order().ToArray());
        Assert.Empty(await EventsAsync(foreignTenant));
        Assert.True((await kit.JobAsync(job)).Enabled);
        Assert.Equal(BackupStatus.Pending, (await kit.RunAsync(job, run.Id)).Status);
    }

    [Fact]
    public async Task JobPolicyEditsCompareAllFieldsAndRepeatedEditsAreSilent()
    {
        var kit = new ScheduleKit(fixture);
        var agent = await kit.Runs.AddAgentAsync();
        var actor = await AddActorAsync();
        var job = await kit.AddJobAsync(agent);
        var store = kit.Store();
        async Task ApplyAsync(Action<BackupJob> edit, DateTimeOffset at)
        {
            await store.ModifyJobAsync(agent.TenantId, job.Id, edit, default, actor, at);
            var count = (await EventsAsync(agent.TenantId)).Count;
            await store.ModifyJobAsync(agent.TenantId, job.Id, edit, default, actor, at.AddSeconds(1));
            Assert.Equal(count, (await EventsAsync(agent.TenantId)).Count);
        }
        await ApplyAsync(j => j.ChangeSchedule(j.Schedule!.Change(true, j.Schedule.CronExpression, "Europe/Istanbul", j.Schedule.MissedRunGracePeriod, Now.AddMinutes(1))), Now.AddMinutes(1));
        await ApplyAsync(j => j.ChangeSchedule(j.Schedule!.Change(true, j.Schedule.CronExpression, j.Schedule.TimeZoneId, TimeSpan.FromMinutes(5), Now.AddMinutes(2))), Now.AddMinutes(2));
        await ApplyAsync(j => j.ChangeSchedule(j.Schedule!.Change(false, j.Schedule.CronExpression, j.Schedule.TimeZoneId, j.Schedule.MissedRunGracePeriod, Now.AddMinutes(3))), Now.AddMinutes(3));
        await ApplyAsync(j => j.ChangeRetention(j.RetentionPolicy with { Enabled = false }), Now.AddMinutes(4));
        await ApplyAsync(j => j.ChangeRetention(j.RetentionPolicy with { DeleteGracePeriod = TimeSpan.FromHours(2) }), Now.AddMinutes(5));
        await ApplyAsync(j => j.Disable(), Now.AddMinutes(6));
        await ApplyAsync(j => j.Enable(Now.AddMinutes(7)), Now.AddMinutes(7));
        await ApplyAsync(j => j.ChangeSchedule(null), Now.AddMinutes(8));
        var events = (await EventsAsync(agent.TenantId)).Where(e => e.ActorUserId == actor).ToList();
        Assert.Equal(4, events.Count(e => e.Action == AuditActions.BackupJobScheduleChanged));
        Assert.Equal(2, events.Count(e => e.Action == AuditActions.BackupJobRetentionChanged));
        Assert.Single(events, e => e.Action == AuditActions.BackupJobDisabled);
        Assert.Single(events, e => e.Action == AuditActions.BackupJobEnabled);
        Assert.Single(events, e => e.Action == AuditActions.BackupJobScheduleRemoved);
        Assert.All(events, e => Assert.InRange(e.At, Now.AddMinutes(1), Now.AddMinutes(8)));
        var loaded = await kit.JobAsync(job);
        var update = new BackupJobUpdate(loaded.Name, loaded.AgentId, loaded.Configuration, loaded.RetryPolicy, loaded.OverlapPolicy);
        var before = (await EventsAsync(agent.TenantId)).Count;
        await kit.Runs.Store().UpdateJobAsync(agent.TenantId, job.Id, update, default, actor, Now.AddMinutes(9));
        Assert.Equal(before, (await EventsAsync(agent.TenantId)).Count);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("trigger")]
    [InlineData("cancel")]
    [InlineData("retry")]
    [InlineData("schedule")]
    [InlineData("complete")]
    [InlineData("scheduled")]
    public async Task AuditInsertFailureRollsBackTheEntireMutation(string operation)
    {
        var kit = new ScheduleKit(fixture);
        var agent = await kit.Runs.AddAgentAsync();
        var actor = await AddActorAsync();
        var job = await kit.AddJobAsync(agent, retry: new BackupRetryPolicy { MaxAttempts = 2 });
        var store = kit.Runs.Store();
        BackupRun? run = null;
        BackupClaim? claim = null;
        if (operation is "cancel" or "retry" or "complete")
        {
            run = await kit.Runs.StartRunAsync(store, job, "before", Now);
            if (operation is "retry" or "complete")
            {
                claim = (await BackupStoreHarness.ClaimAsync(store, agent, Now))!;
                await BackupStoreHarness.StartAttemptAsync(store, claim, Now);
                if (operation == "retry") await BackupStoreHarness.CompleteAsync(store, claim, BackupStoreHarness.Failed(BackupFailureCodes.PreCheckFailed), Now.AddSeconds(1));
            }
        }
        if (operation == "scheduled") await kit.SeenAsync(agent, Now.AddHours(1));
        var beforeEvents = (await EventsAsync(agent.TenantId)).Select(e => e.Id).Order().ToArray();
        var beforeRuns = await kit.Runs.CountRunsAsync(job.Id);
        var beforeStatus = run is null ? (BackupStatus?)null : (await store.GetRunAsync(agent.TenantId, run.Id, default))!.Status;
        var beforeAttempts = run is null ? 0 : await kit.Runs.CountAttemptsAsync(run.Id);
        var name = "reject_backup_audit_" + Guid.NewGuid().ToString("N");
        await using var context = fixture.CreatePlatformContext();
        await fixture.ExecuteAsync($$"""
            CREATE FUNCTION {{name}}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'audit refused'; END $$;
            CREATE TRIGGER {{name}} BEFORE INSERT ON audit_events FOR EACH ROW
            WHEN (NEW."TenantId" = '{{agent.TenantId.Value}}'::uuid) EXECUTE FUNCTION {{name}}();
            """);
        try
        {
            await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
            {
                switch (operation)
                {
                    case "create": await store.CreateJobAsync(new BackupJob(Guid.NewGuid(), agent.TenantId, "New", agent.AgentId, job.Configuration, Now), default, actor); break;
                    case "update": await store.UpdateJobAsync(agent.TenantId, job.Id, new BackupJobUpdate("Changed", agent.AgentId, job.Configuration, null, null), default, actor, Now); break;
                    case "trigger": await store.StartRunAsync(agent.TenantId, job.Id, "rejected", Now, default, actor); break;
                    case "cancel": await store.RequestCancellationAsync(agent.TenantId, run!.Id, Now, default, actor); break;
                    case "retry": await store.RetryRunAsync(agent.TenantId, run!.Id, Now.AddSeconds(2), default, actor); break;
                    case "schedule": await kit.Store().ModifyJobAsync(agent.TenantId, job.Id, j => j.Disable(), default, actor, Now); break;
                    case "complete": await store.CompleteAttemptAsync(BackupStoreHarness.Command(claim!, Now.AddSeconds(1)), BackupStoreHarness.Failed(BackupFailureCodes.PreCheckFailed), default); break;
                    case "scheduled": await kit.Store().EvaluateAsync(agent.TenantId, job.Id, Now.AddHours(1), default); break;
                }
            });
        }
        finally { await fixture.ExecuteAsync($"DROP TRIGGER {name} ON audit_events; DROP FUNCTION {name}();"); }
        Assert.Equal(beforeEvents, (await EventsAsync(agent.TenantId)).Select(e => e.Id).Order().ToArray());
        Assert.Equal(beforeRuns, await kit.Runs.CountRunsAsync(job.Id));
        var persisted = await kit.JobAsync(job);
        Assert.Equal(job.Name, persisted.Name);
        Assert.True(persisted.Enabled);
        if (run is not null)
        {
            var persistedRun = (await store.GetRunAsync(agent.TenantId, run.Id, default))!;
            Assert.Equal(beforeStatus, persistedRun.Status);
            Assert.Null(persistedRun.CancellationRequestedAt);
            Assert.Equal(beforeAttempts, persistedRun.Attempts.Count);
        }
        Assert.Empty(await kit.OccurrencesAsync(job));
    }

    [Fact]
    public async Task CompletionOrphanCleanupIsAuditedOnceWithoutStorageKeys()
    {
        var kit = new BackupStoreHarness(fixture);
        var agent = await kit.AddAgentAsync();
        var job = await kit.AddJobAsync(agent);
        var store = kit.Store(BackupStoreHarness.Limits());
        await kit.StartRunAsync(store, job, "orphan", Now);
        var claim = (await BackupStoreHarness.ClaimAsync(store, agent, Now))!;
        await BackupStoreHarness.StartAttemptAsync(store, claim, Now);
        var artifact = BackupStoreHarness.Success(Now).Artifact!;
        const string key = "tenant/job/run/attempt/secret-object.bak";
        var completion = new BackupPlatform.Application.Backups.BackupCompletion("Failed", BackupFailureCodes.PreCheckFailed,
            artifact with { Replicas = artifact.Replicas.Select(r => r with { Key = key, VerificationState = "Failed", VerificationMethod = "None", VerifiedAt = null }).ToArray() });
        await BackupStoreHarness.CompleteAsync(store, claim, completion, Now.AddSeconds(1));
        await BackupStoreHarness.CompleteAsync(store, claim, completion, Now.AddSeconds(1));
        await using var context = fixture.CreatePlatformContext();
        var audit = Assert.Single(await context.AuditEvents.AsNoTracking().Where(e => e.TenantId == agent.TenantId && e.Action == AuditActions.BackupReplicaOrphanRecorded).ToListAsync());
        Assert.Equal(UserId.System, audit.ActorUserId);
        Assert.Equal(AuditActorKind.System, audit.ActorKind);
        Assert.DoesNotContain(key, audit.DetailsJson);
        Assert.Equal("cleanup_queued", Details(audit).GetProperty("result").GetString());
        var replica = await context.BackupReplicas.AsNoTracking().SingleAsync(r => r.TenantId == agent.TenantId);
        Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status);
        Assert.Equal(replica.Id.ToString("D"), audit.TargetId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOfRunningOrRetryPendingRunRecordsOneTerminalDecision(bool waitingForRetry)
    {
        var kit = new BackupStoreHarness(fixture);
        var agent = await kit.AddAgentAsync();
        var actor = await AddActorAsync();
        var job = await kit.AddJobAsync(agent);
        var store = kit.Store(BackupStoreHarness.Limits());
        var run = await kit.StartRunAsync(store, job, "cancel", Now);
        var claim = (await BackupStoreHarness.ClaimAsync(store, agent, Now))!;
        await BackupStoreHarness.StartAttemptAsync(store, claim, Now);
        if (waitingForRetry)
            await BackupStoreHarness.CompleteAsync(store, claim, BackupStoreHarness.Failed(BackupFailureCodes.DatabaseUnavailable), Now.AddSeconds(1));
        await store.RequestCancellationAsync(agent.TenantId, run.Id, Now.AddSeconds(2), default, actor);
        await store.RequestCancellationAsync(agent.TenantId, run.Id, Now.AddSeconds(3), default, actor);
        var completedAt = waitingForRetry ? Now.AddSeconds(2) : Now.AddSeconds(4);
        if (!waitingForRetry)
        {
            await BackupStoreHarness.CompleteAsync(store, claim, BackupStoreHarness.Failed(BackupFailureCodes.DatabaseUnavailable), completedAt);
            await BackupStoreHarness.CompleteAsync(store, claim, BackupStoreHarness.Failed(BackupFailureCodes.DatabaseUnavailable), completedAt);
        }
        await store.StartDueRetriesAsync(Now.AddDays(1), 500, default);
        var events = await EventsAsync(agent.TenantId);
        var cancellation = Assert.Single(events, e => e.Action == AuditActions.BackupRunCancelRequested);
        Assert.Equal(actor, cancellation.ActorUserId);
        Assert.Equal(AuditActorKind.User, cancellation.ActorKind);
        Assert.Single(events, e => e.Action == AuditActions.BackupRunAttemptCompleted);
        AssertCompletion(events, AuditActions.BackupRunCompleted, "Cancelled", completedAt);
        Assert.DoesNotContain(events, e => e.Action == AuditActions.BackupRunRetryRequested);
        Assert.Equal(1, await kit.CountAttemptsAsync(run.Id));
    }

    [Fact]
    public async Task PauseAndResumeRecordTheHumanActorAndRejectedRequestsRecordNothing()
    {
        var kit = new BackupStoreHarness(fixture);
        var agent = await kit.AddAgentAsync();
        var actor = await AddActorAsync();
        var job = await kit.AddJobAsync(agent);
        var store = kit.Store(BackupStoreHarness.Limits());
        var run = await kit.StartRunAsync(store, job, "pause-audit", Now);
        var claim = (await BackupStoreHarness.ClaimAsync(store, agent, Now))!;
        await BackupStoreHarness.StartAttemptAsync(store, claim, Now);

        Assert.Equal(BackupMutationResult.Conflict, await store.SetPauseAsync(agent.TenantId, run.Id, true, Now.AddSeconds(1), default, actor));
        Assert.DoesNotContain(await EventsAsync(agent.TenantId), e => e.Action is AuditActions.BackupRunPauseRequested or AuditActions.BackupRunResumed);

        Assert.Equal(BackupMutationResult.Accepted, await store.SetPauseAsync(agent.TenantId, run.Id, false, Now.AddSeconds(2), default, actor));
        await BackupStoreHarness.CompleteAsync(store, claim, BackupStoreHarness.Paused(), Now.AddSeconds(3));
        Assert.Equal(BackupMutationResult.Accepted, await store.SetPauseAsync(agent.TenantId, run.Id, true, Now.AddSeconds(4), default, actor));

        var events = await EventsAsync(agent.TenantId);
        var paused = Assert.Single(events, e => e.Action == AuditActions.BackupRunPauseRequested);
        var resumed = Assert.Single(events, e => e.Action == AuditActions.BackupRunResumed);
        foreach (var entry in new[] { paused, resumed })
        {
            Assert.Equal(actor, entry.ActorUserId);
            Assert.Equal(AuditActorKind.User, entry.ActorKind);
            Assert.Equal(run.Id.ToString("D"), entry.TargetId);
            Assert.Equal(run.Id.ToString("D"), Details(entry).GetProperty("runId").GetString());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LeaseExpirationThroughSweepOrClaimRecordsTerminalEventsExactlyOnce(bool expireThroughClaim, bool cancellationRequested)
    {
        var kit = new BackupStoreHarness(fixture);
        var agent = await kit.AddAgentAsync();
        var job = await kit.AddJobAsync(agent, retry: new BackupRetryPolicy { MaxAttempts = 1 });
        var store = kit.Store(BackupStoreHarness.Limits());
        var run = await kit.StartRunAsync(store, job, "expiry", Now);
        var claim = (await BackupStoreHarness.ClaimAsync(store, agent, Now))!;
        await BackupStoreHarness.StartAttemptAsync(store, claim, Now);
        if (cancellationRequested)
        {
            var actor = await AddActorAsync();
            await store.RequestCancellationAsync(agent.TenantId, run.Id, Now.AddSeconds(1), default, actor);
            await store.RequestCancellationAsync(agent.TenantId, run.Id, Now.AddSeconds(2), default, actor);
        }
        var at = Now.Add(BackupStoreHarness.Lease);
        for (var i = 0; i < 2; i++)
        {
            if (expireThroughClaim) Assert.Null(await BackupStoreHarness.ClaimAsync(store, agent, at));
            else await store.ExpireStaleAsync(at, default);
        }
        var events = (await EventsAsync(agent.TenantId)).Where(e => e.TargetId == run.Id.ToString("D")).ToList();
        AssertCompletion(events, AuditActions.BackupRunAttemptCompleted, "AgentLost", at);
        AssertCompletion(events, AuditActions.BackupRunCompleted, cancellationRequested ? "Cancelled" : "AgentLost", at);
    }

    [Fact]
    public async Task CompletionAndManualOrAutomaticRetriesHaveDistinctActorsAndSafeImmutableResults()
    {
        var kit = new BackupStoreHarness(fixture);
        var agent = await kit.AddAgentAsync();
        var actor = await AddActorAsync();
        var job = await kit.AddJobAsync(agent, retry: new BackupRetryPolicy { MaxAttempts = 3, Jitter = 0 });
        var store = kit.Store(BackupStoreHarness.Limits());
        var run = await kit.StartRunAsync(store, job, "retries", Now);
        var claim = (await BackupStoreHarness.ClaimAsync(store, agent, Now))!;
        await BackupStoreHarness.StartAttemptAsync(store, claim, Now);
        await BackupStoreHarness.CompleteAsync(store, claim, BackupStoreHarness.Failed(BackupFailureCodes.DatabaseUnavailable), Now.AddSeconds(1));
        Assert.DoesNotContain(await EventsAsync(agent.TenantId), e => e.Action == AuditActions.BackupRunCompleted);
        var due = Now.AddSeconds(31);
        await Task.WhenAll(store.StartDueRetriesAsync(due, 500, default), store.StartDueRetriesAsync(due, 500, default));
        claim = (await BackupStoreHarness.ClaimAsync(store, agent, due))!;
        await BackupStoreHarness.StartAttemptAsync(store, claim, due);
        const string unsafeReason = "Password=secret; /private/backup.bak";
        Assert.Equal(BackupMutationResult.Conflict, await store.CompleteAttemptAsync(
            BackupStoreHarness.Command(claim, due.AddSeconds(1)), BackupStoreHarness.Failed(unsafeReason), default));
        await BackupStoreHarness.CompleteAsync(store, claim, BackupStoreHarness.Failed(BackupFailureCodes.PreCheckFailed), due.AddSeconds(1));
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(
            BackupStoreHarness.Command(claim, due.AddSeconds(1)), BackupStoreHarness.Failed(BackupFailureCodes.PreCheckFailed), default));
        Assert.Equal(BackupRetryOutcome.Started, (await store.RetryRunAsync(agent.TenantId, run.Id, due.AddSeconds(2), default, actor)).Outcome);
        Assert.Equal(BackupRetryOutcome.InProgress, (await store.RetryRunAsync(agent.TenantId, run.Id, due.AddSeconds(2), default, actor)).Outcome);
        var events = await EventsAsync(agent.TenantId);
        Assert.Equal(2, events.Count(e => e.Action == AuditActions.BackupRunAttemptCompleted));
        var terminal = Assert.Single(events, e => e.Action == AuditActions.BackupRunCompleted);
        Assert.Equal(BackupFailureCodes.PreCheckFailed, Details(terminal).GetProperty("terminalReason").GetString());
        Assert.All(events, e => Assert.DoesNotContain(unsafeReason, e.DetailsJson));
        var retries = events.Where(e => e.Action == AuditActions.BackupRunRetryRequested).ToArray();
        Assert.Equal(2, retries.Length);
        var manual = Assert.Single(retries, e => Details(e).GetProperty("retry").GetString() == "Manual");
        Assert.Equal(actor, manual.ActorUserId);
        Assert.Equal(AuditActorKind.User, manual.ActorKind);
        var automatic = Assert.Single(retries, e => Details(e).GetProperty("retry").GetString() == "Automatic");
        Assert.Equal(UserId.System, automatic.ActorUserId);
        Assert.Equal(AuditActorKind.System, automatic.ActorKind);
    }

    [Fact]
    public async Task ScheduledOccurrenceDecisionsAndQueuedExpiryAreAuditedOnce()
    {
        var kit = new ScheduleKit(fixture);
        var agent = await kit.Runs.AddAgentAsync();
        var job = await kit.AddJobAsync(agent);
        var at = Now.AddHours(1);
        await kit.SeenAsync(agent, at);
        var evaluations = await Task.WhenAll(kit.Store().EvaluateAsync(agent.TenantId, job.Id, at, default), kit.Store().EvaluateAsync(agent.TenantId, job.Id, at, default));
        var runId = Assert.Single(evaluations, e => e.CreatedRunId.HasValue).CreatedRunId!.Value;
        await kit.SeenAsync(agent, Now.AddHours(2));
        await kit.Store().EvaluateAsync(agent.TenantId, job.Id, Now.AddHours(2), default);
        await kit.Store().EvaluateAsync(agent.TenantId, job.Id, Now.AddHours(2), default);
        await kit.Store().EvaluateAsync(agent.TenantId, job.Id, Now.AddHours(3).AddMinutes(31), default);
        await kit.Store().EvaluateAsync(agent.TenantId, job.Id, Now.AddHours(3).AddMinutes(31), default);
        await kit.Store().ExpireUnclaimedRunsAsync(Now.AddHours(4), 1000, default);
        await kit.Store().ExpireUnclaimedRunsAsync(Now.AddHours(4), 1000, default);
        var events = await EventsAsync(agent.TenantId);
        var decisions = events.Where(e => e.Action == AuditActions.BackupScheduleOccurrenceDecided).ToArray();
        Assert.Equal(3, decisions.Length);
        Assert.Equal(3, decisions.Select(e => e.TargetId).Distinct().Count());
        Assert.Contains(decisions, e => Details(e).GetProperty("result").GetString() == "SkippedOverlap");
        Assert.Contains(decisions, e => Details(e).GetProperty("result").GetString() == "MissedGraceExpired");
        Assert.All(decisions, e => { Assert.Equal(UserId.System, e.ActorUserId); Assert.Equal(AuditActorKind.System, e.ActorKind); });
        var triggered = Assert.Single(events, e => e.Action == AuditActions.BackupRunTriggered);
        Assert.Equal(UserId.System, triggered.ActorUserId);
        Assert.Equal(runId.ToString("D"), triggered.TargetId);
        AssertCompletion(events, AuditActions.BackupRunCompleted, "Cancelled", Now.AddHours(4));
        AssertCompletion(events, AuditActions.BackupRunAttemptCompleted, "Cancelled", Now.AddHours(4));
    }

    [Fact]
    public async Task ScheduledAdmissionDenialIsAuditedOnceWithoutLeakingPolicyText()
    {
        var kit = new ScheduleKit(fixture);
        var agent = await kit.Runs.AddAgentAsync();
        var job = await kit.AddJobAsync(agent);
        await kit.SeenAsync(agent, Now.AddHours(1));
        var store = new PostgreSqlBackupScheduleStore(fixture.Options, new DeniedAdmission());
        await store.EvaluateAsync(agent.TenantId, job.Id, Now.AddHours(1), default);
        await store.EvaluateAsync(agent.TenantId, job.Id, Now.AddHours(1), default);
        var events = await EventsAsync(agent.TenantId);
        var decision = Assert.Single(events, e => e.Action == AuditActions.BackupScheduleOccurrenceDecided);
        Assert.Equal("AdmissionDenied", Details(decision).GetProperty("result").GetString());
        Assert.Equal("unknown_denial", Details(decision).GetProperty("reason").GetString());
        Assert.DoesNotContain(events, e => e.Action == AuditActions.BackupRunTriggered);
        Assert.All(events, e => Assert.DoesNotContain("secret", e.DetailsJson));
        Assert.Empty(await kit.RunsOfAsync(job));
    }

    private async Task<UserId> AddActorAsync()
    {
        var actor = UserId.New();
        await using var context = fixture.CreatePlatformContext();
        context.Users.Add(new User(actor, "Backup operator"));
        await context.SaveChangesAsync();
        return actor;
    }

    private async Task<List<AuditEvent>> EventsAsync(TenantId tenantId)
    {
        await using var context = fixture.CreatePlatformContext();
        return await context.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenantId && (e.TargetType == "backup_job" || e.TargetType == "backup_run" || e.TargetType == "backup_schedule_occurrence")).ToListAsync();
    }

    private static JsonElement Details(AuditEvent e) => JsonSerializer.Deserialize<JsonElement>(e.DetailsJson);

    private static void AssertCompletion(IReadOnlyList<AuditEvent> events, string action, string status, DateTimeOffset at)
    {
        var completion = Assert.Single(events, e => e.Action == action);
        Assert.Equal(UserId.System, completion.ActorUserId);
        Assert.Equal(AuditActorKind.System, completion.ActorKind);
        Assert.Equal(at, completion.At);
        Assert.Equal(status, Details(completion).GetProperty("result").GetString());
        Assert.NotEqual(Guid.Empty, Details(completion).GetProperty("attemptId").GetGuid());
        Assert.NotEqual(Guid.Empty, Details(completion).GetProperty("runId").GetGuid());
    }

    private sealed class DeniedAdmission : IBackupAdmissionPolicy
    {
        public Task<BackupAdmissionDecision> AdmitNewRunAsync(BackupAdmissionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(BackupAdmissionDecision.Denied("secret_policy_reference"));
        public Task ReleaseAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
