using BackupPlatform.AgentTests.Support;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Agent.Core.Connectivity;
using static BackupPlatform.AgentTests.Support.Failures;

namespace BackupPlatform.AgentTests;

/// <summary>
/// The agent's side of the cleanup the backend owes: it drops only a database whose name proves the platform made it, reports exactly what happened under the lease it was
/// handed, and never lets a refused task, a missing provider or a provider failure turn into a silent success.
/// </summary>
public sealed class RestoreCleanupWorkerTests
{
    [Fact]
    public async Task A_leased_verification_database_is_dropped_and_reported_under_its_lease_token()
    {
        await using var harness = new RestoreTaskHarness();
        var envelope = harness.CleanupEnvelope();
        harness.StartCleanup();
        Assert.True(harness.CleanupWorker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.CleanupCompletions.Count == 1, "cleanup report");

        var drop = Assert.Single(harness.CleanupProvider.Drops);
        Assert.Equal(envelope.RestoreCleanup!.DatabaseName, drop.DatabaseName);
        Assert.Equal(DatabaseDropKind.VerificationDatabase, drop.Kind);
        var (operationId, report) = Assert.Single(harness.Client.CleanupCompletions);
        Assert.Equal(envelope.RestoreCleanup.OperationId, operationId);
        Assert.Equal(envelope.RestoreCleanup.LeaseToken, report.LeaseToken);
        Assert.Equal("Dropped", report.Outcome);
        Assert.Null(report.FailureCode);
    }

    [Fact]
    public async Task An_already_absent_database_is_reported_as_not_found_which_is_still_a_removal()
    {
        await using var harness = new RestoreTaskHarness();
        harness.CleanupProvider.Drop = (_, _) => Task.FromResult(new DatabaseDropResult(DatabaseDropOutcome.NotFound));
        harness.StartCleanup();
        Assert.True(harness.CleanupWorker.TryPublish(harness.CleanupEnvelope()));
        await Wait.UntilAsync(() => harness.Client.CleanupCompletions.Count == 1, "cleanup report");

        Assert.Equal("NotFound", harness.Client.CleanupCompletions.Single().Request.Outcome);
    }

    [Fact]
    public async Task A_failed_drop_is_reported_with_a_stable_code_and_never_with_provider_text()
    {
        await using var harness = new RestoreTaskHarness();
        harness.CleanupProvider.Drop = (_, _) => Task.FromResult(new DatabaseDropResult(DatabaseDropOutcome.Failed, "database-in-use"));
        harness.StartCleanup();
        Assert.True(harness.CleanupWorker.TryPublish(harness.CleanupEnvelope()));
        await Wait.UntilAsync(() => harness.Client.CleanupCompletions.Count == 1, "cleanup report");
        var report = harness.Client.CleanupCompletions.Single().Request;
        Assert.Equal(("Failed", "database-in-use"), (report.Outcome, report.FailureCode));

        harness.CleanupProvider.Drop = (_, _) => throw new InvalidOperationException("Login failed for user 'sa' password=SECRET");
        Assert.True(harness.CleanupWorker.TryPublish(harness.CleanupEnvelope()));
        await Wait.UntilAsync(() => harness.Client.CleanupCompletions.Count == 2, "second cleanup report");
        var thrown = harness.Client.CleanupCompletions.Last().Request;
        Assert.Equal(("Failed", "cleanup-error"), (thrown.Outcome, thrown.FailureCode));
    }

    [Theory]
    [InlineData("Sales", "VerificationDatabase")]
    [InlineData("master", "VerificationDatabase")]
    [InlineData("bp_verify_UPPERCASE0123456789abcdef0123456", "VerificationDatabase")]
    [InlineData("bp_old_0123456789abcdef0123456789abcdef", "VerificationDatabase")]
    [InlineData("bp_verify_0123456789abcdef0123456789abcdef", "RollbackCopy")]
    [InlineData("bp_verify_0123456789abcdef0123456789abcdef", "Anything")]
    public async Task A_task_whose_name_or_kind_is_not_one_the_platform_made_is_refused_before_any_provider_is_called(string name, string kind)
    {
        await using var harness = new RestoreTaskHarness();
        harness.StartCleanup();
        Assert.True(harness.CleanupWorker.TryPublish(harness.CleanupEnvelope(name, kind)));
        await Wait.UntilAsync(() => harness.Client.CleanupCompletions.Count == 1, "cleanup report");

        Assert.Empty(harness.CleanupProvider.Drops);
        var report = harness.Client.CleanupCompletions.Single().Request;
        Assert.Equal(("Failed", "invalid-request"), (report.Outcome, report.FailureCode));
    }

    [Fact]
    public async Task A_task_this_agent_cannot_act_on_is_reported_failed_not_dropped()
    {
        await using var harness = new RestoreTaskHarness();
        harness.StartCleanup();
        Assert.True(harness.CleanupWorker.TryPublish(harness.CleanupEnvelope(engine: "postgres", integrated: true)));
        await Wait.UntilAsync(() => harness.Client.CleanupCompletions.Count == 1, "first cleanup report");
        Assert.True(harness.CleanupWorker.TryPublish(harness.CleanupEnvelope(engine: "oracle")));
        await Wait.UntilAsync(() => harness.Client.CleanupCompletions.Count == 2, "second cleanup report");

        Assert.Empty(harness.CleanupProvider.Drops);
        Assert.All(harness.Client.CleanupCompletions, entry => Assert.Equal("Failed", entry.Request.Outcome));
        Assert.Contains(harness.Client.CleanupCompletions, entry => entry.Request.FailureCode == "unsupported-engine");
    }

    [Fact]
    public async Task A_lease_is_never_executed_twice_and_an_expired_or_malformed_delivery_is_ignored()
    {
        await using var harness = new RestoreTaskHarness();
        var envelope = harness.CleanupEnvelope();
        Assert.True(harness.CleanupWorker.TryPublish(envelope));
        Assert.False(harness.CleanupWorker.TryPublish(envelope));
        Assert.False(harness.CleanupWorker.TryPublish(envelope with { Id = Guid.NewGuid().ToString() }));
        Assert.False(harness.CleanupWorker.TryPublish(envelope with { RestoreCleanup = null }));
        Assert.False(harness.CleanupWorker.TryPublish(harness.CleanupEnvelope() with { RestoreCleanup = harness.CleanupEnvelope().RestoreCleanup! with { LeaseExpiresAt = harness.Time.GetUtcNow() } }));
    }

    [Fact]
    public async Task A_transient_backend_failure_repeats_the_report_until_the_backend_answers()
    {
        await using var harness = new RestoreTaskHarness();
        harness.Client.OnCleanupComplete = attempt => attempt == 1
            ? AgentBackendResult<bool>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Transient, "down"))
            : AgentBackendResult<bool>.Success(true);
        harness.StartCleanup();
        Assert.True(harness.CleanupWorker.TryPublish(harness.CleanupEnvelope()));
        await Wait.UntilAsync(() => harness.Client.CleanupCompletions.Count == 1, "first report");
        await harness.Time.AdvanceAsync(TimeSpan.FromSeconds(3));
        await Wait.UntilAsync(() => harness.Client.CleanupCompletions.Count == 2, "repeated report");

        Assert.Single(harness.CleanupProvider.Drops);
        Assert.Equal(harness.Client.CleanupCompletions.First().Request.LeaseToken, harness.Client.CleanupCompletions.Last().Request.LeaseToken);
    }

    [Fact]
    public async Task The_dispatcher_routes_a_cleanup_to_the_cleanup_worker_only()
    {
        await using var backups = new BackupTaskHarness();
        await using var deletions = new DeleteReplicaHarness();
        await using var restores = new RestoreTaskHarness();
        await using var downloads = new DownloadTaskHarness();
        var dispatcher = new BackupPlatform.Agent.Core.Tasks.AgentTaskDispatcher(backups.Worker, restores.Worker, deletions.Worker, downloads.Worker, restores.CleanupWorker, null!, null!);
        var envelope = restores.CleanupEnvelope();

        Assert.True(dispatcher.TryPublish(envelope));
        Assert.False(restores.Worker.TryPublish(envelope));
        Assert.False(dispatcher.TryPublish(envelope));
    }
}

/// <summary>A verification the agent refused before it touched anything owes no removal; one the pipeline broke on may.</summary>
public sealed class VerificationRefusalTests
{
    [Fact]
    public async Task A_refused_verification_snapshot_states_that_nothing_was_created_and_a_broken_pipeline_does_not()
    {
        await using var refused = new RestoreTaskHarness(staging: false);
        var envelope = refused.With(refused.Envelope(mode: "NewDatabase"), task => task with { Purpose = "Verification", DatabaseName = "bp_verify_" + Guid.NewGuid().ToString("N") });
        refused.Start();
        Assert.True(refused.Worker.TryPublish(envelope with { Type = RestoreTaskTypes.DatabaseRestoreVerification }));
        await Wait.UntilAsync(() => refused.Client.Completions.Count == 1, "refusal");
        var completion = Assert.Single(refused.Client.Completions);
        Assert.Equal("Failed", completion.Status);
        Assert.Equal(RestoreVerificationCleanupOutcomes.NotCreated, completion.Result!.Verification!.Cleanup);

        await using var broken = new RestoreTaskHarness();
        broken.Pipeline.Execute = (_, _, _) => throw new InvalidOperationException("pipeline contract broken");
        broken.Start();
        Assert.True(broken.Worker.TryPublish(broken.With(broken.Envelope(), task => task with { Purpose = "Verification", DatabaseName = "bp_verify_" + Guid.NewGuid().ToString("N") })
            with { Type = RestoreTaskTypes.DatabaseRestoreVerification }));
        await Wait.UntilAsync(() => broken.Client.Completions.Count == 1, "broken pipeline");
        Assert.Null(Assert.Single(broken.Client.Completions).Result!.Verification);
    }
}
