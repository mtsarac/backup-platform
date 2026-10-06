using System.Text.Json;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using static BackupPlatform.AgentTests.Support.Failures;

namespace BackupPlatform.AgentTests;

public sealed class BackupTaskWorkerTests
{
    [Fact]
    public async Task Duplicate_deliveries_before_during_and_after_execution_do_not_repeat_the_pipeline()
    {
        await using var harness = new BackupTaskHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Pipeline.Execute = async (request, _, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return harness.Success(request);
        };
        var envelope = harness.Envelope();
        Assert.True(harness.Worker.TryPublish(envelope));
        Assert.False(harness.Worker.TryPublish(envelope));
        harness.Start();
        await Wait.UntilAsync(() => harness.Pipeline.Calls.Count == 1, "provider execution");
        Assert.False(harness.Worker.TryPublish(envelope));
        release.SetResult();
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "terminal result");
        Assert.False(harness.Worker.TryPublish(envelope));
        Assert.Single(harness.Pipeline.Calls);
        Assert.Single(harness.Client.Starts);
    }

    [Fact]
    public async Task Start_refusal_does_not_execute_or_complete_and_the_next_attempt_can_run()
    {
        await using var harness = new BackupTaskHarness();
        harness.Client.OnStart = () => Fail<BackupTaskLeaseResponse>(AgentBackendFailureKind.Rejected);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Starts.Count == 1, "start refusal");
        harness.Client.OnStart = null;
        var next = harness.Envelope();
        Assert.True(harness.Worker.TryPublish(next));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "next terminal result");
        Assert.Single(harness.Pipeline.Calls);
        Assert.Equal(next.Backup!.LeaseToken, Assert.Single(harness.Client.Completions).LeaseToken);
    }

    [Fact]
    public async Task Backend_cancellation_reaches_provider_token_and_reports_cancelled()
    {
        await using var harness = new BackupTaskHarness();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Pipeline.Execute = async (_, _, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }
            throw new InvalidOperationException();
        };
        harness.Client.OnRenew = () => AgentBackendResult<BackupTaskLeaseResponse>.Success(new BackupTaskLeaseResponse
        {
            LeaseExpiresAt = harness.Time.GetUtcNow().AddSeconds(120), CancellationRequested = true
        });
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Pipeline.Calls.Count == 1, "provider start");
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "cancelled terminal result");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Cancelled", completion.Status);
        Assert.Equal("Cancelled", completion.FailureCode);
        Assert.Null(completion.Artifact);
    }

    [Theory]
    [InlineData(AgentBackendFailureKind.Rejected, 10)]
    [InlineData(AgentBackendFailureKind.Transient, 120)]
    public async Task Lost_or_expired_lease_cancels_provider_without_sending_a_terminal_mutation(AgentBackendFailureKind failure, int seconds)
    {
        await using var harness = new BackupTaskHarness();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Pipeline.Execute = async (_, _, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
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
        await Wait.UntilAsync(() => harness.Pipeline.Calls.Count == 1, "provider start");
        harness.Time.Advance(TimeSpan.FromSeconds(seconds));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Pipeline.Execute = (request, _, _) => Task.FromResult(harness.Success(request));
        var next = harness.Envelope();
        Assert.True(harness.Worker.TryPublish(next));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "next attempt terminal result");
        Assert.Equal(next.Backup!.LeaseToken, Assert.Single(harness.Client.Completions).LeaseToken);
    }

    [Fact]
    public async Task Progress_is_coalesced_and_transport_losses_do_not_change_the_verified_result()
    {
        await using var harness = new BackupTaskHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Pipeline.Execute = async (request, progress, token) =>
        {
            for (var percent = 0; percent <= 100; percent++)
            {
                progress!.Report(new BackupProgress(BackupExecutionStage.BackingUp, percent, null, null, harness.Time.GetUtcNow()));
            }
            await release.Task.WaitAsync(token);
            return harness.Success(request);
        };
        harness.Client.OnProgress = () => Fail<bool>(AgentBackendFailureKind.Transient);
        var envelope = harness.Envelope();
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Pipeline.Calls.Count == 1, "provider start");
        Assert.Empty(harness.Client.Progress);
        // The coalescing timer is armed by the worker after the provider started; advancing the fake clock before that would miss it.
        await ConnectivityHarness.SettleAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => harness.Client.Progress.Count == 1, "coalesced progress");
        Assert.Equal(100, Assert.Single(harness.Client.Progress).Percent);
        release.SetResult();
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "successful result");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Succeeded", completion.Status);
        Assert.Null(completion.FailureCode);
        var artifact = Assert.IsType<BackupTaskArtifact>(completion.Artifact);
        Assert.Equal(new string('a', 64), artifact.SourceChecksum);
        Assert.Equal(new string('b', 64), artifact.ArtifactChecksum);
        var replica = Assert.Single(artifact.Replicas);
        Assert.Equal(artifact.ArtifactChecksum, replica.Checksum);
        Assert.Equal(256, replica.SizeBytes);
        Assert.Equal("Verified", replica.VerificationState);
        Assert.Equal("ReadBack", replica.VerificationMethod);
        Assert.Equal($"backups/{envelope.Backup!.TenantId:N}/{envelope.Backup.JobId:N}/{envelope.Backup.RunId:N}/{envelope.Backup.AttemptId:N}/source.bak.gz", replica.Key);
        using var metadata = JsonDocument.Parse(artifact.SourceMetadataJson);
        Assert.Equal("Sales", metadata.RootElement.GetProperty("databaseName").GetString());
        Assert.Equal("Full", metadata.RootElement.GetProperty("backupType").GetString());
        Assert.Equal("16.0", metadata.RootElement.GetProperty("instance").GetProperty("version").GetString());
        Assert.Equal("simple", metadata.RootElement.GetProperty("details").GetProperty("recovery-model").GetString());
        Assert.Equal("RESTORE VERIFYONLY", metadata.RootElement.GetProperty("backupVerification").GetProperty("method").GetString());
        using var options = JsonDocument.Parse(artifact.AppliedOptionsJson);
        Assert.False(options.RootElement.GetProperty("compression").GetBoolean());
        Assert.Equal(0, harness.Credentials.Calls);
        Assert.IsType<IntegratedDatabaseCredential>(Assert.Single(harness.Pipeline.Calls).Credential);
    }

    [Theory]
    [InlineData(BackupFailureKind.PreCheckFailed, "PreCheckFailed")]
    [InlineData(BackupFailureKind.SqlBackupFailed, "SqlBackupFailed")]
    [InlineData(BackupFailureKind.VerificationFailed, "VerificationFailed")]
    [InlineData(BackupFailureKind.SourceHashFailed, "HashFailed")]
    [InlineData(BackupFailureKind.ArtifactPreparationFailed, "InternalError")]
    [InlineData(BackupFailureKind.StorageWriteFailed, "StorageWriteFailed")]
    [InlineData(BackupFailureKind.ReplicaVerificationFailed, "ReplicaVerificationFailed")]
    public async Task Provider_failure_categories_are_mapped_without_forwarding_diagnostics(BackupFailureKind kind, string expected)
    {
        await using var harness = new BackupTaskHarness();
        harness.Pipeline.Execute = (_, _, _) => Task.FromResult(new DatabaseBackupPipelineResult
        {
            Status = DatabaseBackupStatus.Failed, FinalStage = BackupExecutionStage.Failed,
            StartedAt = harness.Time.GetUtcNow(), FinishedAt = harness.Time.GetUtcNow(),
            Failure = new BackupFailure(kind, BackupExecutionStage.BackingUp, "internal-detail", "password=should-not-be-forwarded")
        });
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope(false)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "failure result");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Failed", completion.Status);
        Assert.Equal(expected, completion.FailureCode);
        Assert.Null(completion.Artifact);
        Assert.DoesNotContain("should-not-be-forwarded", JsonSerializer.Serialize(completion), StringComparison.Ordinal);
        Assert.Equal(1, harness.Credentials.Calls);
    }

    [Fact]
    public async Task Completion_transport_retry_reuses_the_terminal_result_and_current_identity_without_repeating_provider()
    {
        await using var harness = new BackupTaskHarness();
        harness.Client.OnComplete = count => count == 1 ? Fail<bool>(AgentBackendFailureKind.Transient) : AgentBackendResult<bool>.Success(true);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "first completion");
        var rotated = Guid.NewGuid();
        harness.Store.Identity = TestIdentity.Create(TestIdentity.RotatedSecret, rotated);
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 2, "completion retry");
        var results = harness.Client.Completions.ToArray();
        Assert.Same(results[0], results[1]);
        Assert.Single(harness.Pipeline.Calls);
        Assert.Equal(rotated, harness.Client.CompletionCredentials.Last());
    }

    [Fact]
    public async Task Unsupported_protocol_type_identity_and_expired_claims_are_not_executed()
    {
        await using var harness = new BackupTaskHarness();
        var envelope = harness.Envelope();
        Assert.False(harness.Worker.TryPublish(envelope with { Type = "unknown" }));
        Assert.False(harness.Worker.TryPublish(envelope with { Id = Guid.NewGuid().ToString() }));
        Assert.False(harness.Worker.TryPublish(envelope with { Backup = envelope.Backup! with { Version = 2 } }));
        Assert.False(harness.Worker.TryPublish(envelope with { Backup = envelope.Backup! with { LeaseExpiresAt = harness.Time.GetUtcNow() } }));
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "valid claim completion");
        Assert.Single(harness.Pipeline.Calls);
        Assert.Single(harness.Client.Starts);
    }

    [Fact]
    public async Task Queue_capacity_is_bounded_and_a_second_attempt_waits_for_the_active_provider()
    {
        await using var harness = new BackupTaskHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Pipeline.Execute = async (request, _, token) =>
        {
            await release.Task.WaitAsync(token);
            return harness.Success(request);
        };
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Pipeline.Calls.Count == 1, "first provider start");
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        Assert.False(harness.Worker.TryPublish(harness.Envelope()));
        Assert.Single(harness.Pipeline.Calls);
        release.SetResult();
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 2, "both accepted attempts complete");
        Assert.Equal(2, harness.Pipeline.Calls.Count);
    }

    [Fact]
    public async Task Cancellation_in_claim_does_not_resolve_credentials_or_execute_provider()
    {
        await using var harness = new BackupTaskHarness();
        var envelope = harness.Envelope(false);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope with { Backup = envelope.Backup! with { CancellationRequested = true } }));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "claim cancellation");
        Assert.Equal("Cancelled", Assert.Single(harness.Client.Completions).Status);
        Assert.Empty(harness.Pipeline.Calls);
        Assert.Equal(0, harness.Credentials.Calls);
    }

    [Theory]
    [InlineData("../escape", "default")]
    [InlineData("backups", "/arbitrary/staging")]
    public async Task Invalid_configuration_is_completed_safely_without_provider_execution(string prefix, string staging)
    {
        await using var harness = new BackupTaskHarness();
        var envelope = harness.Envelope();
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope with
        {
            Backup = envelope.Backup! with { Configuration = envelope.Backup.Configuration with { StoragePrefix = prefix, StagingReference = staging } }
        }));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "invalid configuration result");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Failed", completion.Status);
        Assert.Equal("InternalError", completion.FailureCode);
        Assert.Empty(harness.Pipeline.Calls);
    }

    [Fact]
    public async Task Empty_storage_prefix_still_scopes_replica_to_tenant_job_run_and_attempt()
    {
        await using var harness = new BackupTaskHarness();
        var envelope = harness.Envelope();
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope with
        {
            Backup = envelope.Backup! with { Configuration = envelope.Backup.Configuration with { StoragePrefix = "" } }
        }));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "scoped artifact");
        var replica = Assert.Single(Assert.Single(harness.Client.Completions).Artifact!.Replicas);
        Assert.Equal($"{envelope.Backup!.TenantId:N}/{envelope.Backup.JobId:N}/{envelope.Backup.RunId:N}/{envelope.Backup.AttemptId:N}/source.bak.gz", replica.Key);
    }

    [Theory]
    [InlineData(BackupFailureKind.PreCheckFailed, BackupFailureCause.DatabaseUnavailable, "DatabaseUnavailable")]
    [InlineData(BackupFailureKind.SqlBackupFailed, BackupFailureCause.Timeout, "OperationTimedOut")]
    [InlineData(BackupFailureKind.PreCheckFailed, BackupFailureCause.DatabaseNotFound, "DatabaseNotFound")]
    [InlineData(BackupFailureKind.PreCheckFailed, BackupFailureCause.InvalidConfiguration, "InvalidConfiguration")]
    [InlineData(BackupFailureKind.PreCheckFailed, BackupFailureCause.Unsupported, "UnsupportedCapability")]
    [InlineData(BackupFailureKind.PreCheckFailed, BackupFailureCause.AuthenticationFailed, "AuthenticationFailed")]
    [InlineData(BackupFailureKind.SqlBackupFailed, BackupFailureCause.PermissionDenied, "PermissionDenied")]
    [InlineData(BackupFailureKind.PreCheckFailed, BackupFailureCause.StagingInvalid, "StagingInvalid")]
    [InlineData(BackupFailureKind.StorageWriteFailed, BackupFailureCause.StorageUnavailable, "StorageUnavailable")]
    [InlineData(BackupFailureKind.PreCheckFailed, BackupFailureCause.Unclassified, "PreCheckFailed")]
    [InlineData(BackupFailureKind.StorageWriteFailed, BackupFailureCause.Unclassified, "StorageWriteFailed")]
    public async Task Failure_cause_decides_the_wire_code_before_the_kind_and_no_diagnostics_are_forwarded(
        BackupFailureKind kind, BackupFailureCause cause, string expected)
    {
        await using var harness = new BackupTaskHarness();
        harness.Pipeline.Execute = (_, _, _) => Task.FromResult(new DatabaseBackupPipelineResult
        {
            Status = DatabaseBackupStatus.Failed, FinalStage = BackupExecutionStage.Failed,
            StartedAt = harness.Time.GetUtcNow(), FinishedAt = harness.Time.GetUtcNow(),
            Failure = new BackupFailure(kind, BackupExecutionStage.BackingUp, "internal-detail", "password=should-not-be-forwarded", cause)
        });
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope(false)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "failure result");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Failed", completion.Status);
        Assert.Equal(expected, completion.FailureCode);
        Assert.DoesNotContain("should-not-be-forwarded", JsonSerializer.Serialize(completion), StringComparison.Ordinal);
        Assert.DoesNotContain("internal-detail", JsonSerializer.Serialize(completion), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_stays_cancelled_whatever_cause_is_attached()
    {
        await using var harness = new BackupTaskHarness();
        harness.Pipeline.Execute = (_, _, _) => Task.FromResult(new DatabaseBackupPipelineResult
        {
            Status = DatabaseBackupStatus.Cancelled, FinalStage = BackupExecutionStage.Cancelled,
            StartedAt = harness.Time.GetUtcNow(), FinishedAt = harness.Time.GetUtcNow(),
            Failure = new BackupFailure(BackupFailureKind.Cancelled, BackupExecutionStage.BackingUp, "cancelled", "The backup was cancelled.", BackupFailureCause.Timeout)
        });
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope(false)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "cancelled result");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Cancelled", completion.Status);
        Assert.Equal("Cancelled", completion.FailureCode);
    }

    [Theory]
    [InlineData(DatabaseBackupStatus.Failed, BackupFailureKind.SqlBackupFailed, "SqlBackupFailed")]
    [InlineData(DatabaseBackupStatus.Cancelled, BackupFailureKind.Cancelled, "Cancelled")]
    public async Task Non_success_does_not_publish_artifact_even_when_provider_returns_partial_metadata(
        DatabaseBackupStatus status, BackupFailureKind failure, string expectedCode)
    {
        await using var harness = new BackupTaskHarness();
        harness.Pipeline.Execute = (request, _, _) => Task.FromResult(harness.Success(request) with
        {
            Status = status,
            FinalStage = status == DatabaseBackupStatus.Cancelled ? BackupExecutionStage.Cancelled : BackupExecutionStage.Failed,
            Failure = new BackupFailure(failure, BackupExecutionStage.BackingUp, "partial-result", "A safe provider failure.")
        });
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "partial terminal result");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal(status.ToString(), completion.Status);
        Assert.Equal(expectedCode, completion.FailureCode);
        Assert.Null(completion.Artifact);
    }

    [Fact]
    public async Task Credential_resolution_failure_is_internal_error_without_secret_details_or_provider_execution()
    {
        await using var harness = new BackupTaskHarness();
        harness.Credentials.Failure = new InvalidOperationException("Password=must-never-leave-host");
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope(false)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "credential error result");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Failed", completion.Status);
        Assert.Equal("InternalError", completion.FailureCode);
        Assert.Empty(harness.Pipeline.Calls);
        Assert.DoesNotContain("must-never-leave-host", JsonSerializer.Serialize(completion), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminal_retry_stops_when_lease_expires_and_never_reexecutes_the_finished_provider()
    {
        await using var harness = new BackupTaskHarness();
        harness.Client.OnComplete = _ => Fail<bool>(AgentBackendFailureKind.Transient);
        harness.Client.OnRenew = () => Fail<BackupTaskLeaseResponse>(AgentBackendFailureKind.Transient);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "first terminal request");
        harness.Time.Advance(TimeSpan.FromSeconds(120));
        await ConnectivityHarness.SettleAsync();
        harness.Client.OnComplete = null;
        harness.Client.OnRenew = null;
        var next = harness.Envelope();
        Assert.True(harness.Worker.TryPublish(next));
        await Wait.UntilAsync(() => harness.Client.Completions.Any(result => result.LeaseToken == next.Backup!.LeaseToken), "next attempt result");
        Assert.Single(harness.Client.Completions, result => result.LeaseToken == next.Backup!.LeaseToken);
        Assert.Equal(2, harness.Pipeline.Calls.Count);
    }
}
