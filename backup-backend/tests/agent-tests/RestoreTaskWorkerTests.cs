using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using static BackupPlatform.AgentTests.Support.Failures;

namespace BackupPlatform.AgentTests;

public sealed class RestoreTaskWorkerTests
{
    [Fact]
    public async Task Duplicate_deliveries_before_during_and_after_execution_do_not_repeat_the_pipeline()
    {
        await using var harness = new RestoreTaskHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Pipeline.Execute = async (request, _, token) =>
        {
            await harness.Time.ObserveAsync("restore worker", release.Task, token);
            return harness.Success(request);
        };
        var envelope = harness.Envelope();
        Assert.True(harness.Worker.TryPublish(envelope));
        Assert.False(harness.Worker.TryPublish(envelope));
        harness.Start();
        await Wait.UntilAsync(() => harness.Pipeline.Calls.Count == 1, "pipeline execution");
        Assert.False(harness.Worker.TryPublish(envelope));
        release.SetResult();
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "terminal result");
        Assert.False(harness.Worker.TryPublish(envelope));
        Assert.Single(harness.Pipeline.Calls);
        Assert.Single(harness.Client.Starts);
    }

    [Fact]
    public async Task A_queued_claim_reaches_completion_without_advancing_the_clock()
    {
        await using var harness = new RestoreTaskHarness();
        harness.Start();
        await harness.Time.DrainAsync();
        var queuedAt = harness.Time.GetUtcNow();

        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await harness.Time.DrainAsync();

        Assert.Single(harness.Pipeline.Calls);
        Assert.Equal("Succeeded", Assert.Single(harness.Client.Completions).Status);
        Assert.Equal(queuedAt, harness.Time.GetUtcNow());
    }

    [Fact]
    public async Task Success_completes_online_with_the_result_and_hands_the_pipeline_exactly_the_claimed_input()
    {
        await using var harness = new RestoreTaskHarness();
        var envelope = harness.With(harness.Envelope(integrated: false, mode: "ReplaceExistingDatabase"), task => task with
        {
            Options = new RestoreTaskOptions { TerminateActiveConnections = true, VerifyChecksum = false, VerifyBackup = false },
            Target = task.Target with { Encrypt = false, TrustServerCertificate = true, Port = 14330 }
        });
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "successful result");

        var task = envelope.Restore!;
        var request = Assert.Single(harness.Pipeline.Calls);
        Assert.Equal(DatabaseEngineId.MsSql, request.Engine);
        Assert.Equal("SalesRestored", request.DatabaseName);
        Assert.Equal(DatabaseRestoreMode.ReplaceExistingDatabase, request.Mode);
        Assert.True(request.TerminateActiveConnections);
        Assert.False(request.VerifyChecksum);
        Assert.False(request.VerifyBackup);
        Assert.Equal(task.TenantId, request.TenantId);
        Assert.Equal("local", request.Storage.Value);
        Assert.Equal(task.Source.StorageKey, request.StorageKey.Value);
        Assert.Equal(4096, request.ExpectedSizeBytes);
        Assert.Equal(new string('a', 64), request.ExpectedChecksum.Hex);
        Assert.Equal("sql.example.test", request.Connection.Endpoint.Host);
        Assert.Equal(14330, request.Connection.Endpoint.Port);
        var password = Assert.IsType<PasswordDatabaseCredential>(request.Credential);
        Assert.Equal("backup-user", password.UserName);
        Assert.Equal(1, harness.Credentials.Calls);

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal(task.LeaseToken, completion.LeaseToken);
        Assert.Equal("Succeeded", completion.Status);
        Assert.Null(completion.FailureCode);
        Assert.Equal("Online", completion.TargetState);
        var result = Assert.IsType<RestoreTaskResult>(completion.Result);
        Assert.Equal("ONLINE", result.DatabaseState);
        Assert.Null(result.TargetStateNote);
        Assert.False(result.ChecksumVerified);
        Assert.False(result.BackupVerified);
        Assert.Equal("Sales", result.SourceBackup!.DatabaseName);
        Assert.Equal("Database", result.SourceBackup.BackupType);
        Assert.Equal("16.0.4135", result.SourceBackup.SourceServerVersion);
        Assert.Equal("SQL-TARGET", result.TargetServer!.ServerName);
        var mapping = Assert.Single(result.FileMappings);
        Assert.Equal("Sales", mapping.LogicalName);
        Assert.Equal("Data", mapping.FileType);
        Assert.Equal("/var/opt/mssql/data/SalesRestored.mdf", mapping.TargetPhysicalName);
        Assert.Equal("Passed", Assert.Single(result.PreCheck).Status);
        Assert.Equal("w1", Assert.Single(result.Warnings).Code);
        Assert.NotNull(result.RestoreStartedAt);
        Assert.NotNull(result.RestoreFinishedAt);
    }

    [Fact]
    public async Task Start_refusal_does_not_execute_or_complete_and_the_next_attempt_can_run()
    {
        await using var harness = new RestoreTaskHarness();
        harness.Client.OnStart = () => Fail<BackupTaskLeaseResponse>(AgentBackendFailureKind.Rejected);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Starts.Count == 1, "start refusal");
        harness.Client.OnStart = null;
        var next = harness.Envelope();
        Assert.True(harness.Worker.TryPublish(next));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "next terminal result");
        Assert.Single(harness.Pipeline.Calls);
        Assert.Equal(next.Restore!.LeaseToken, Assert.Single(harness.Client.Completions).LeaseToken);
    }

    [Fact]
    public async Task Backend_cancellation_on_renewal_reaches_the_pipeline_and_the_cancelled_restore_still_completes_with_its_target_state()
    {
        await using var harness = new RestoreTaskHarness();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Pipeline.Execute = async (_, _, token) =>
        {
            try
            {
                await harness.Time.ObserveAsync("restore worker", Task.Delay(Timeout.InfiniteTimeSpan, token));
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
            }
            return harness.Failed(RestoreAgentFailureCodes.Cancelled, DatabaseRestoreTargetState.PartiallyRestored, DatabaseRestoreStatus.Cancelled);
        };
        harness.Client.OnRenew = () => AgentBackendResult<BackupTaskLeaseResponse>.Success(new BackupTaskLeaseResponse
        {
            LeaseExpiresAt = harness.Time.GetUtcNow().AddSeconds(120), CancellationRequested = true
        });
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Pipeline.Calls.Count == 1, "pipeline start");
        await harness.Time.AdvanceAsync(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "cancelled terminal result");

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Cancelled", completion.Status);
        Assert.Equal("Cancelled", completion.FailureCode);
        Assert.Equal("PartiallyRestored", completion.TargetState);
        // The wording is engine-neutral and may change; what must never be lost is that a partially restored target is explained to the user.
        Assert.False(string.IsNullOrWhiteSpace(completion.Result!.TargetStateNote));
    }

    [Fact]
    public async Task Cancellation_flagged_in_the_start_answer_cancels_the_pipeline_token_before_it_runs()
    {
        await using var harness = new RestoreTaskHarness();
        harness.Client.OnStart = () => AgentBackendResult<BackupTaskLeaseResponse>.Success(new BackupTaskLeaseResponse
        {
            LeaseExpiresAt = harness.Time.GetUtcNow().AddSeconds(120), CancellationRequested = true
        });
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Pipeline.Execute = (_, _, token) =>
        {
            observed.SetResult(token.IsCancellationRequested);
            return Task.FromResult(harness.Failed(RestoreAgentFailureCodes.Cancelled, status: DatabaseRestoreStatus.Cancelled));
        };
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "cancelled result");
        // The claim itself was cancelled: the request is not even built, so the target is untouched and the pipeline is never asked.
        Assert.Empty(harness.Pipeline.Calls);
        Assert.False(observed.Task.IsCompleted);
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Cancelled", completion.Status);
        Assert.Equal("NotModified", completion.TargetState);
    }

    [Theory]
    [InlineData(AgentBackendFailureKind.Rejected, 10)]
    [InlineData(AgentBackendFailureKind.Transient, 120)]
    public async Task Lost_or_expired_lease_cancels_the_pipeline_without_sending_a_terminal_mutation(AgentBackendFailureKind failure, int seconds)
    {
        await using var harness = new RestoreTaskHarness();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Pipeline.Execute = async (_, _, token) =>
        {
            try
            {
                await harness.Time.ObserveAsync("restore worker", Task.Delay(Timeout.InfiniteTimeSpan, token));
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }
            throw new InvalidOperationException();
        };
        harness.Client.OnRenew = () => Fail<BackupTaskLeaseResponse>(failure);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Pipeline.Calls.Count == 1, "pipeline start");
        await harness.Time.AdvanceAsync(TimeSpan.FromSeconds(seconds));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Pipeline.Execute = (request, _, _) => Task.FromResult(harness.Success(request));
        var next = harness.Envelope();
        Assert.True(harness.Worker.TryPublish(next));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "next attempt terminal result");
        Assert.Equal(next.Restore!.LeaseToken, Assert.Single(harness.Client.Completions).LeaseToken);
    }

    [Fact]
    public async Task Renewal_extends_the_lease_so_a_restore_longer_than_one_lease_term_is_not_lost()
    {
        await using var harness = new RestoreTaskHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewals = 0;
        harness.Client.OnRenew = () =>
        {
            Interlocked.Increment(ref renewals);
            return AgentBackendResult<BackupTaskLeaseResponse>.Success(new BackupTaskLeaseResponse { LeaseExpiresAt = harness.Time.GetUtcNow().AddSeconds(120) });
        };
        harness.Pipeline.Execute = async (request, _, token) =>
        {
            await harness.Time.ObserveAsync("restore worker", release.Task, token);
            return harness.Success(request);
        };
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Pipeline.Calls.Count == 1, "pipeline start");
        await harness.Time.AdvanceAsync(TimeSpan.FromSeconds(300));
        Assert.InRange(Volatile.Read(ref renewals), 29, 30);
        Assert.Empty(harness.Client.Completions);
        release.SetResult();
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "completion after several lease terms");
        Assert.Equal("Succeeded", harness.Client.Completions.Single().Status);
    }

    [Fact]
    public async Task Progress_is_coalesced_to_the_latest_stage_and_transport_losses_do_not_change_the_result()
    {
        await using var harness = new RestoreTaskHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Pipeline.Execute = async (request, progress, token) =>
        {
            progress!.Report(new RestoreProgress(RestoreExecutionStage.DownloadingArtifact, 100, 10, 10, harness.Time.GetUtcNow()));
            for (var percent = 0; percent <= 100; percent++)
            {
                progress.Report(new RestoreProgress(RestoreExecutionStage.Restoring, percent, null, null, harness.Time.GetUtcNow()));
            }
            await harness.Time.ObserveAsync("restore worker", release.Task, token);
            return harness.Success(request);
        };
        harness.Client.OnProgress = () => Fail<bool>(AgentBackendFailureKind.Transient);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Pipeline.Calls.Count == 1, "pipeline start");
        Assert.Empty(harness.Client.Progress);
        await harness.Time.AdvanceAsync(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => harness.Client.Progress.Count == 1, "coalesced progress");
        var progress = Assert.Single(harness.Client.Progress);
        Assert.Equal("Restoring", progress.Stage);
        Assert.Equal(100, progress.Percent);
        release.SetResult();
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "successful result");
        Assert.Equal("Succeeded", Assert.Single(harness.Client.Completions).Status);
    }

    [Fact]
    public async Task Completion_transport_retry_reuses_the_terminal_result_and_current_identity_without_repeating_the_restore()
    {
        await using var harness = new RestoreTaskHarness();
        harness.Client.OnComplete = count => count == 1 ? Fail<bool>(AgentBackendFailureKind.Transient) : AgentBackendResult<bool>.Success(true);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "first completion");
        var rotated = Guid.NewGuid();
        harness.Store.Identity = TestIdentity.Create(TestIdentity.RotatedSecret, rotated);
        await harness.Time.AdvanceAsync(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 2, "completion retry");
        var results = harness.Client.Completions.ToArray();
        Assert.Same(results[0], results[1]);
        Assert.Single(harness.Pipeline.Calls);
        Assert.Equal(rotated, harness.Client.CompletionCredentials.Last());
    }

    [Fact]
    public async Task A_rate_limited_completion_waits_for_the_server_requested_delay()
    {
        await using var harness = new RestoreTaskHarness();
        harness.Client.OnComplete = count => count == 1
            ? Fail<bool>(AgentBackendFailureKind.RateLimited, TimeSpan.FromSeconds(7))
            : AgentBackendResult<bool>.Success(true);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "first completion");
        await harness.Time.AdvanceAsync(TimeSpan.FromSeconds(6));
        Assert.Single(harness.Client.Completions);
        await harness.Time.AdvanceAsync(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 2, "completion retry after the requested delay");
    }

    [Fact]
    public async Task A_conflicting_completion_is_not_retried()
    {
        await using var harness = new RestoreTaskHarness();
        harness.Client.OnComplete = _ => Fail<bool>(AgentBackendFailureKind.Rejected);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "completion");
        await harness.Time.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Single(harness.Client.Completions);
    }

    [Fact]
    public async Task Terminal_retry_stops_when_the_lease_expires_and_never_reruns_the_finished_restore()
    {
        await using var harness = new RestoreTaskHarness();
        harness.Client.OnComplete = _ => Fail<bool>(AgentBackendFailureKind.Transient);
        harness.Client.OnRenew = () => Fail<BackupTaskLeaseResponse>(AgentBackendFailureKind.Transient);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "first terminal request");
        await harness.Time.AdvanceAsync(TimeSpan.FromSeconds(120));
        harness.Client.OnComplete = null;
        harness.Client.OnRenew = null;
        var next = harness.Envelope();
        Assert.True(harness.Worker.TryPublish(next));
        await Wait.UntilAsync(() => harness.Client.Completions.Any(result => result.LeaseToken == next.Restore!.LeaseToken), "next attempt result");
        Assert.Single(harness.Client.Completions, result => result.LeaseToken == next.Restore!.LeaseToken);
        Assert.Equal(2, harness.Pipeline.Calls.Count);
    }

    [Fact]
    public async Task Unsupported_type_protocol_identity_and_expired_claims_are_not_executed()
    {
        await using var harness = new RestoreTaskHarness();
        var envelope = harness.Envelope();
        var restore = envelope.Restore!;
        Assert.False(harness.Worker.TryPublish(envelope with { Type = "unknown" }));
        Assert.False(harness.Worker.TryPublish(envelope with { Type = "database-backup" }));
        Assert.False(harness.Worker.TryPublish(envelope with { Restore = null }));
        Assert.False(harness.Worker.TryPublish(envelope with { Id = Guid.NewGuid().ToString() }));
        Assert.False(harness.Worker.TryPublish(envelope with { Id = "not-a-guid" }));
        Assert.False(harness.Worker.TryPublish(envelope with { Restore = restore with { Version = 2 } }));
        Assert.False(harness.Worker.TryPublish(envelope with { Restore = restore with { LeaseExpiresAt = harness.Time.GetUtcNow() } }));
        Assert.False(harness.Worker.TryPublish(envelope with { Restore = restore with { LeaseToken = Guid.Empty } }));
        Assert.False(harness.Worker.TryPublish(envelope with { Restore = restore with { OperationId = Guid.Empty } }));
        Assert.False(harness.Worker.TryPublish(envelope with { Restore = restore with { TenantId = Guid.Empty } }));
        Assert.False(harness.Worker.TryPublish(envelope with { Restore = restore with { AttemptNumber = 0 } }));
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "valid claim completion");
        Assert.Single(harness.Pipeline.Calls);
        Assert.Single(harness.Client.Starts);
    }

    [Fact]
    public async Task Queue_capacity_is_bounded_and_a_second_attempt_waits_for_the_active_restore()
    {
        await using var harness = new RestoreTaskHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Pipeline.Execute = async (request, _, token) =>
        {
            await harness.Time.ObserveAsync("restore worker", release.Task, token);
            return harness.Success(request);
        };
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Pipeline.Calls.Count == 1, "first pipeline start");
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        Assert.False(harness.Worker.TryPublish(harness.Envelope()));
        Assert.Single(harness.Pipeline.Calls);
        release.SetResult();
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 2, "both accepted attempts complete");
        Assert.Equal(2, harness.Pipeline.Calls.Count);
    }

    [Fact]
    public async Task Cancellation_in_the_claim_completes_cancelled_untouched_without_resolving_credentials_or_running_the_pipeline()
    {
        await using var harness = new RestoreTaskHarness();
        var envelope = harness.Envelope(integrated: false);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.With(envelope, task => task with { CancellationRequested = true })));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "claim cancellation");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Cancelled", completion.Status);
        Assert.Equal("Cancelled", completion.FailureCode);
        Assert.Equal("NotModified", completion.TargetState);
        Assert.Empty(harness.Pipeline.Calls);
        Assert.Equal(0, harness.Credentials.Calls);
    }

    public static TheoryData<string, Func<RestoreTaskSnapshot, RestoreTaskSnapshot>> InvalidSnapshots => new()
    {
        { "unknown mode", t => t with { Mode = "Overwrite" } },
        { "lower-case mode", t => t with { Mode = "newdatabase" } },
        { "foreign staging reference", t => t with { StagingReference = "/arbitrary/staging" } },
        { "empty database name", t => t with { DatabaseName = " " } },
        { "control character in database name", t => t with { DatabaseName = "Sales\nDROP" } },
        { "oversized database name", t => t with { DatabaseName = new string('d', 129) } },
        { "credential reference with integrated security", t => t with { Target = t.Target with { IntegratedSecurity = true, DatabaseCredentialReference = "x" } } },
        { "no credential reference without integrated security", t => t with { Target = t.Target with { IntegratedSecurity = false, DatabaseCredentialReference = null } } },
        { "credential reference with path characters", t => t with { Target = t.Target with { IntegratedSecurity = false, DatabaseCredentialReference = "../secret" } } },
        { "oversized host", t => t with { Target = t.Target with { Host = new string('h', 254) } } },
        { "host with whitespace", t => t with { Target = t.Target with { Host = "sql server" } } },
        { "port out of range", t => t with { Target = t.Target with { Port = 70000 } } },
        { "invalid storage provider id", t => t with { Source = t.Source with { StorageProvider = "Local/../x" } } },
        { "rooted storage key", t => t with { Source = t.Source with { StorageKey = "/etc/passwd" } } },
        { "parent segment in storage key", t => t with { Source = t.Source with { StorageKey = "a/../b/c.bak" } } },
        { "checksum that is not sha-256", t => t with { Source = t.Source with { Checksum = "abc" } } },
        { "unknown checksum algorithm", t => t with { Source = t.Source with { ChecksumAlgorithm = "md5" } } },
        { "zero size", t => t with { Source = t.Source with { SizeBytes = 0 } } },
        { "agent-local access on central storage", t => t with { Source = t.Source with { StorageProvider = "central", StorageExecutionLocation = "Backend" } } },
        { "central access on local storage", t => t with { Source = t.Source with { Access = "CentralStorage" } } },
        { "central access with agent execution location", t => t with { Source = t.Source with { Access = "CentralStorage", StorageProvider = "central" } } },
        { "unknown access", t => t with { Source = t.Source with { Access = "Anywhere" } } },
        { "empty artifact id", t => t with { ArtifactId = Guid.Empty } },
        { "empty replica id", t => t with { ReplicaId = Guid.Empty } },
    };

    [Theory]
    [MemberData(nameof(InvalidSnapshots))]
    public async Task A_snapshot_the_agent_cannot_act_on_is_completed_failed_and_untouched_without_running_the_pipeline(
        string description, Func<RestoreTaskSnapshot, RestoreTaskSnapshot> change)
    {
        await using var harness = new RestoreTaskHarness();
        harness.Start();
        var envelope = harness.Envelope(integrated: false);
        Assert.True(harness.Worker.TryPublish(harness.With(envelope, change)), description);
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "invalid snapshot result");

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Failed", completion.Status);
        Assert.Equal("InvalidConfiguration", completion.FailureCode);
        Assert.Equal("NotModified", completion.TargetState);
        Assert.Empty(harness.Pipeline.Calls);
        Assert.Equal(0, harness.Credentials.Calls);
    }

    [Fact]
    public async Task Central_storage_snapshots_are_accepted_with_the_central_provider()
    {
        await using var harness = new RestoreTaskHarness();
        harness.Start();
        var envelope = harness.With(harness.Envelope(), t => t with
        {
            Source = t.Source with { StorageProvider = "central", StorageExecutionLocation = "Backend", Access = "CentralStorage" }
        });
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "central restore");
        Assert.Equal("central", Assert.Single(harness.Pipeline.Calls).Storage.Value);
        Assert.Equal("Succeeded", Assert.Single(harness.Client.Completions).Status);
    }

    [Fact]
    public async Task Missing_default_staging_fails_the_restore_as_staging_invalid_before_anything_is_touched()
    {
        await using var harness = new RestoreTaskHarness(staging: false);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "staging error result");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("StagingInvalid", completion.FailureCode);
        Assert.Equal("NotModified", completion.TargetState);
        Assert.Empty(harness.Pipeline.Calls);
    }

    [Fact]
    public async Task Credential_resolution_failure_is_invalid_configuration_untouched_without_secret_details()
    {
        await using var harness = new RestoreTaskHarness();
        harness.Credentials.Failure = new InvalidOperationException("Password=must-never-leave-host");
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope(integrated: false)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "credential error result");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Failed", completion.Status);
        Assert.Equal("InvalidConfiguration", completion.FailureCode);
        Assert.Equal("NotModified", completion.TargetState);
        Assert.Empty(harness.Pipeline.Calls);
        Assert.DoesNotContain("must-never-leave-host", JsonSerializer.Serialize(completion), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DatabaseRestoreTargetState.NotModified, null)]
    [InlineData(DatabaseRestoreTargetState.PartiallyRestored, "failed while running")]
    [InlineData(DatabaseRestoreTargetState.Unknown, "could not establish")]
    public async Task A_failed_restore_reports_its_code_and_exactly_the_target_state_the_pipeline_established(DatabaseRestoreTargetState state, string? note)
    {
        await using var harness = new RestoreTaskHarness();
        harness.Pipeline.Execute = (_, _, _) => Task.FromResult(harness.Failed(
            RestoreAgentFailureCodes.SqlRestoreFailed, state, reason: "internal-detail", message: "password=should-not-be-forwarded"));
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope(integrated: false)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "failure result");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Failed", completion.Status);
        Assert.Equal("SqlRestoreFailed", completion.FailureCode);
        Assert.Equal(state.ToString(), completion.TargetState);
        if (note is null)
        {
            Assert.Null(completion.Result!.TargetStateNote);
        }
        else
        {
            Assert.Contains(note, completion.Result!.TargetStateNote, StringComparison.Ordinal);
        }

        var json = JsonSerializer.Serialize(completion);
        Assert.DoesNotContain("should-not-be-forwarded", json, StringComparison.Ordinal);
        Assert.DoesNotContain("internal-detail", json, StringComparison.Ordinal);
        Assert.DoesNotContain("local-only-password", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pipeline_that_throws_leaves_the_target_unknown_and_the_exception_text_behind()
    {
        await using var harness = new RestoreTaskHarness();
        harness.Pipeline.Execute = (_, _, _) => throw new InvalidOperationException("Server=secret-host;Password=hunter2");
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "failure result");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Failed", completion.Status);
        Assert.Equal("InternalError", completion.FailureCode);
        Assert.Equal("Unknown", completion.TargetState);
        Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(completion), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pipeline_that_throws_cancellation_reports_cancelled_with_an_unknown_target()
    {
        await using var harness = new RestoreTaskHarness();
        harness.Client.OnRenew = () => AgentBackendResult<BackupTaskLeaseResponse>.Success(new BackupTaskLeaseResponse
        {
            LeaseExpiresAt = harness.Time.GetUtcNow().AddSeconds(120), CancellationRequested = true
        });
        harness.Pipeline.Execute = async (_, _, token) =>
        {
            await harness.Time.ObserveAsync("restore worker", Task.Delay(Timeout.InfiniteTimeSpan, token));
            throw new InvalidOperationException();
        };
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Pipeline.Calls.Count == 1, "pipeline start");
        await harness.Time.AdvanceAsync(TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "cancelled result");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Cancelled", completion.Status);
        Assert.Equal("Unknown", completion.TargetState);
    }

    [Fact]
    public async Task A_result_with_a_very_large_report_is_bounded_and_keeps_what_failed()
    {
        await using var harness = new RestoreTaskHarness();
        harness.Pipeline.Execute = (request, _, _) =>
        {
            var success = harness.Success(request);
            var items = Enumerable.Range(0, 400)
                .Select(i => new BackupPreCheckItem($"check.{i}", BackupPreCheckStatus.Passed, "ok", new string('p', 1000)))
                .Append(new BackupPreCheckItem("target.important", BackupPreCheckStatus.Warning, "important-warning", "<b>\"quoted\"</b>\u0007 text"))
                .ToArray();
            var mappings = Enumerable.Range(0, 1000)
                .Select(i => new RestoreFileMapping($"File{i}", RestoreFileKind.Data, new string('s', 600), new DatabaseServerPath("/data/" + new string('x', 600) + i), 1))
                .ToArray();
            return Task.FromResult(success with
            {
                PreCheck = items,
                FileMappings = mappings,
                Warnings = Enumerable.Range(0, 200).Select(i => new BackupWarning($"w{i}", new string('w', 1000))).ToArray()
            });
        };
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "large result");

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Succeeded", completion.Status);
        var json = JsonSerializer.Serialize(completion.Result, JsonSerializerOptions.Web);
        Assert.True(json.Length < 64 * 1024, $"serialized result is {json.Length} characters");
        Assert.Contains(completion.Result!.PreCheck, item => item.CheckId == "target.important" && item.Status == "Warning");
        Assert.Contains(completion.Result.Warnings, warning => warning.Code == "result-truncated");
        Assert.All(completion.Result.PreCheck, item => Assert.True(item.Message.Length <= 300));
        Assert.DoesNotContain(completion.Result.PreCheck, item => item.Message.Any(char.IsControl));
    }

    [Fact]
    public async Task Restore_and_backup_workers_do_not_share_state_or_accept_each_others_tasks()
    {
        await using var restores = new RestoreTaskHarness();
        await using var backups = new BackupTaskHarness();
        Assert.False(restores.Worker.TryPublish(backups.Envelope()));
        Assert.False(backups.Worker.TryPublish(restores.Envelope()));
    }
}
