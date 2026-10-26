using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Agent.Host;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.AgentTests;

public sealed class DeleteReplicaTaskWorkerTests
{
    [Theory]
    [InlineData(true, "backups")]
    [InlineData(false, "")]
    public async Task Real_provider_deletes_then_verifies_absence_and_already_absent_is_success(bool exists, string prefix)
    {
        await using var harness = new DeleteReplicaHarness();
        var envelope = harness.Envelope(prefix);
        if (exists)
        {
            await harness.PutAsync(envelope.DeleteReplica!);
        }
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "absence completion");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.True(completion.AbsenceVerified);
        Assert.Null(completion.FailureCode);
        Assert.Equal(envelope.DeleteReplica!.OperationId, completion.OperationId);
        Assert.Equal(envelope.DeleteReplica.LeaseToken, completion.LeaseToken);
        Assert.Equal(["delete", "exists"], harness.Storage.Calls.ToArray());
        Assert.False(File.Exists(harness.ObjectPath(envelope.DeleteReplica)));
        Assert.Single(harness.Client.Renewals);
    }

    [Theory]
    [InlineData("sftp")]
    [InlineData("google-drive")]
    public async Task External_replica_is_removed_through_a_scope_bound_to_its_lease_and_absence_is_verified(string provider)
    {
        await using var harness = new DeleteReplicaHarness();
        var envelope = harness.ExternalEnvelope(provider);
        var task = envelope.DeleteReplica!;
        await harness.PutAsync(task);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "external absence completion");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.True(completion.AbsenceVerified);
        Assert.Null(completion.FailureCode);
        Assert.Equal(task.LeaseToken, completion.LeaseToken);
        Assert.Equal(["delete", "exists"], harness.Storage.Calls.ToArray());
        Assert.False(File.Exists(harness.ObjectPath(task)));
        var scope = Assert.Single(harness.External.Scopes);
        Assert.Equal(new DirectStorageScope($"api/agent/replica-deletions/{task.ReplicaId:D}/storage/access", task.LeaseToken, task.TenantId, Guid.Parse(task.StorageTarget), provider), scope);
    }

    [Fact]
    public async Task AnExternalOrphanLedgerEntryMayNameAnUnpublishedManagedPartial()
    {
        await using var harness = new DeleteReplicaHarness();
        var envelope = harness.ExternalEnvelope("sftp");
        var task = envelope.DeleteReplica!;
        var slash = task.StorageKey.LastIndexOf('/');
        task = task with { StorageKey = task.StorageKey[..(slash + 1)] + "." + task.StorageKey[(slash + 1)..] + ".partial" };
        envelope = envelope with { DeleteReplica = task };
        await harness.PutAsync(task);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "partial orphan absence completion");
        Assert.True(Assert.Single(harness.Client.Completions).AbsenceVerified);
        Assert.Equal(["delete", "exists"], harness.Storage.Calls.ToArray());
        Assert.False(File.Exists(harness.ObjectPath(task)));
    }

    [Fact]
    public async Task External_replica_is_never_deleted_when_the_lease_is_refused_before_any_provider_is_opened()
    {
        await using var harness = new DeleteReplicaHarness();
        var envelope = harness.ExternalEnvelope("sftp");
        await harness.PutAsync(envelope.DeleteReplica!);
        harness.Client.OnRenew = (_, _) => Task.FromResult(Fail<DeleteReplicaLeaseResponse>(AgentBackendFailureKind.Rejected));
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Renewals.Count == 1, "authority request");
        await Task.Delay(50);
        Assert.Empty(harness.Client.Completions);
        Assert.Empty(harness.External.Scopes);
        Assert.Empty(harness.Storage.Calls);
        Assert.True(File.Exists(harness.ObjectPath(envelope.DeleteReplica!)));
    }

    [Theory]
    [InlineData(true, "not-a-guid")]
    [InlineData(true, "00000000-0000-0000-0000-000000000000")]
    [InlineData(false, null)]
    public async Task External_replica_without_a_usable_connection_or_provider_factory_reports_invalid_configuration(bool factory, string? target)
    {
        await using var harness = new DeleteReplicaHarness(factory);
        var envelope = harness.ExternalEnvelope("google-drive", target);
        await harness.PutAsync(envelope.DeleteReplica!);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "failure completion");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.False(completion.AbsenceVerified);
        Assert.Equal(DeleteReplicaFailureCodes.InvalidConfiguration, completion.FailureCode);
        Assert.Empty(harness.External.Scopes);
        Assert.True(File.Exists(harness.ObjectPath(envelope.DeleteReplica!)));
    }

    [Fact]
    public async Task Authority_is_confirmed_before_any_filesystem_operation_and_refusal_preserves_object()
    {
        await using var harness = new DeleteReplicaHarness();
        var envelope = harness.Envelope();
        await harness.PutAsync(envelope.DeleteReplica!);
        var release = new TaskCompletionSource<AgentBackendResult<DeleteReplicaLeaseResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Client.OnRenew = (_, token) => release.Task.WaitAsync(token);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Renewals.Count == 1, "authority request");
        Assert.Empty(harness.Storage.Calls);
        release.SetResult(Fail<DeleteReplicaLeaseResponse>(AgentBackendFailureKind.Rejected));
        var next = harness.Envelope();
        harness.Client.OnRenew = null;
        Assert.True(harness.Worker.TryPublish(next));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "next deletion");
        Assert.True(File.Exists(harness.ObjectPath(envelope.DeleteReplica!)));
        Assert.Equal(next.DeleteReplica!.LeaseToken, Assert.Single(harness.Client.Completions).LeaseToken);
    }

    [Fact]
    public async Task Foreign_agent_identity_never_calls_backend_or_provider()
    {
        await using var harness = new DeleteReplicaHarness();
        var envelope = harness.Envelope();
        await harness.PutAsync(envelope.DeleteReplica!);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope with { DeleteReplica = envelope.DeleteReplica! with { AgentId = Guid.NewGuid() } }));
        await Wait.UntilAsync(() => harness.Store.LoadCount > 0, "identity check");
        Assert.Empty(harness.Client.Renewals);
        Assert.Empty(harness.Client.Completions);
        Assert.Empty(harness.Storage.Calls);
        Assert.True(File.Exists(harness.ObjectPath(envelope.DeleteReplica!)));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("provider")]
    [InlineData("prefix")]
    [InlineData("tenant")]
    [InlineData("job")]
    [InlineData("run")]
    [InlineData("attempt")]
    [InlineData("traversal")]
    [InlineData("absolute")]
    [InlineData("filename")]
    [InlineData("partial")]
    public async Task Untrusted_location_is_reported_without_touching_any_file(string mismatch)
    {
        await using var harness = new DeleteReplicaHarness();
        var envelope = harness.Envelope();
        var original = envelope.DeleteReplica!;
        await harness.PutAsync(original);
        var task = mismatch switch
        {
            "target" => original with { StorageTarget = harness.Root },
            "provider" => original with { StorageProvider = "central", StorageTarget = "central" },
            "prefix" => original with { StoragePrefix = "other" },
            "tenant" => original with { TenantId = Guid.NewGuid() },
            "job" => original with { JobId = Guid.NewGuid() },
            "run" => original with { RunId = Guid.NewGuid() },
            "attempt" => original with { AttemptId = Guid.NewGuid() },
            "traversal" => original with { StorageKey = "../" + original.StorageKey },
            "absolute" => original with { StorageKey = harness.ObjectPath(original) },
            "filename" => original with { StorageKey = original.StorageKey[..(original.StorageKey.LastIndexOf('/') + 1)] + "personal.txt" },
            "partial" => original with { StorageKey = original.StorageKey[..(original.StorageKey.LastIndexOf('/') + 1)] + "." + new StorageObjectKey(original.StorageKey).FileName + "." + Guid.NewGuid().ToString("N") + ".partial" },
            _ => throw new InvalidOperationException()
        };
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope with { DeleteReplica = task }));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "security rejection");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.False(completion.AbsenceVerified);
        Assert.Equal(mismatch is "target" or "provider" ? DeleteReplicaFailureCodes.StorageTargetUnknown : DeleteReplicaFailureCodes.InvalidLocation, completion.FailureCode);
        Assert.Empty(harness.Storage.Calls);
        Assert.True(File.Exists(harness.ObjectPath(original)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_provider_refuses_file_and_directory_symlinks_without_deleting_external_content(bool directoryLink)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating symlinks on Windows requires an OS privilege not guaranteed to test processes.
        }
        await using var harness = new DeleteReplicaHarness();
        var envelope = harness.Envelope();
        var task = envelope.DeleteReplica!;
        var external = Path.Combine(harness.Root, "external");
        Directory.CreateDirectory(external);
        var externalFile = Path.Combine(external, new StorageObjectKey(task.StorageKey).FileName);
        await File.WriteAllTextAsync(externalFile, "keep");
        var objectPath = harness.ObjectPath(task);
        var parent = Path.GetDirectoryName(objectPath)!;
        if (directoryLink)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(parent)!);
            Directory.CreateSymbolicLink(parent, external);
        }
        else
        {
            Directory.CreateDirectory(parent);
            File.CreateSymbolicLink(objectPath, externalFile);
        }
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "symlink refusal");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.False(completion.AbsenceVerified);
        Assert.Equal(DeleteReplicaFailureCodes.AccessDenied, completion.FailureCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(externalFile));
        Assert.Single(harness.Storage.Calls);
    }

    [Fact]
    public async Task Missing_configured_root_is_a_terminal_configuration_failure_not_an_absence_success()
    {
        await using var harness = new DeleteReplicaHarness();
        Directory.Delete(harness.Root);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "configuration refusal");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.False(completion.AbsenceVerified);
        Assert.Equal(DeleteReplicaFailureCodes.InvalidConfiguration, completion.FailureCode);
        Directory.CreateDirectory(harness.Root);
    }

    [Fact]
    public async Task Dedupe_is_by_token_and_fresh_token_for_same_replica_executes_again_serially()
    {
        await using var harness = new DeleteReplicaHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Storage.BeforeDelete = token => release.Task.WaitAsync(token);
        var envelope = harness.Envelope();
        await harness.PutAsync(envelope.DeleteReplica!);
        Assert.True(harness.Worker.TryPublish(envelope));
        Assert.False(harness.Worker.TryPublish(envelope));
        harness.Start();
        await Wait.UntilAsync(() => harness.Storage.Calls.Count == 1, "active deletion");
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => harness.Worker.TryPublish(envelope))));
        Assert.All(duplicates, accepted => Assert.False(accepted));
        var fresh = envelope with { DeleteReplica = envelope.DeleteReplica! with { LeaseToken = Guid.NewGuid() } };
        Assert.True(harness.Worker.TryPublish(fresh));
        Assert.Single(harness.Storage.Calls);
        release.SetResult();
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 2, "serialized fresh claim");
        Assert.False(harness.Worker.TryPublish(envelope));
        Assert.Equal(["delete", "exists", "delete", "exists"], harness.Storage.Calls.ToArray());
        Assert.All(harness.Client.Completions, result => Assert.True(result.AbsenceVerified));
    }

    [Theory]
    [InlineData(AgentBackendFailureKind.Rejected, 10)]
    [InlineData(AgentBackendFailureKind.Transient, 120)]
    public async Task Renewal_refusal_or_expiry_cancels_provider_and_never_reports_success(AgentBackendFailureKind failure, int seconds)
    {
        await using var harness = new DeleteReplicaHarness();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Storage.BeforeDelete = async token =>
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
        };
        var envelope = harness.Envelope();
        await harness.PutAsync(envelope.DeleteReplica!);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Storage.Calls.Count == 1, "waiting provider");
        harness.Client.OnRenew = (_, _) => Task.FromResult(Fail<DeleteReplicaLeaseResponse>(failure));
        harness.Time.Advance(TimeSpan.FromSeconds(seconds));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(harness.Client.Completions);
        Assert.True(File.Exists(harness.ObjectPath(envelope.DeleteReplica!)));
    }

    [Fact]
    public async Task Transient_authorization_and_completion_are_retried_without_repeating_physical_delete()
    {
        await using var harness = new DeleteReplicaHarness();
        var envelope = harness.Envelope();
        await harness.PutAsync(envelope.DeleteReplica!);
        harness.Client.OnRenew = (_, _) => Task.FromResult(Fail<DeleteReplicaLeaseResponse>(AgentBackendFailureKind.Transient));
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Renewals.Count == 1, "failed authority request");
        await Wait.UntilAsync(() => harness.Time.TimersCreated >= 2, "authorization retry timer");
        Assert.Empty(harness.Storage.Calls);
        harness.Client.OnRenew = null;
        harness.Client.OnComplete = (_, _) => Task.FromResult(Fail<bool>(AgentBackendFailureKind.Transient));
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "failed completion");
        await Wait.UntilAsync(() => harness.Time.TimersCreated >= 4, "completion retry timer");
        harness.Client.OnComplete = null;
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 2, "completion retry");
        Assert.Equal(harness.Client.Completions.First(), harness.Client.Completions.Last());
        Assert.Equal(["delete", "exists"], harness.Storage.Calls.ToArray());
    }

    [Fact]
    public async Task Restart_after_delete_before_report_completes_a_fresh_claim_of_the_absent_object()
    {
        await using var harness = new DeleteReplicaHarness();
        var envelope = harness.Envelope();
        await harness.PutAsync(envelope.DeleteReplica!);
        harness.Client.OnComplete = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return AgentBackendResult<bool>.Success(true);
        };
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "unreported physical delete");
        Assert.False(File.Exists(harness.ObjectPath(envelope.DeleteReplica!)));
        await harness.StopAsync();
        harness.Client.OnComplete = null;
        var restarted = new DeleteReplicaTaskWorker(harness.Client, harness.Store,
            new DeleteReplicaProviderResolver([harness.Local]), harness.Time, NullLogger<DeleteReplicaTaskWorker>.Instance);
        using var stop = new CancellationTokenSource();
        var running = restarted.RunAsync(stop.Token);
        try
        {
            var fresh = envelope with { DeleteReplica = envelope.DeleteReplica! with { LeaseToken = Guid.NewGuid() } };
            Assert.True(restarted.TryPublish(fresh));
            await Wait.UntilAsync(() => harness.Client.Completions.Count == 2, "recovered completion");
            var completion = harness.Client.Completions.Last();
            Assert.True(completion.AbsenceVerified);
            Assert.Equal(fresh.DeleteReplica!.LeaseToken, completion.LeaseToken);
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Five_second_lease_is_renewed_before_expiry_while_real_provider_execution_waits()
    {
        await using var harness = new DeleteReplicaHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Storage.BeforeDelete = token => release.Task.WaitAsync(token);
        harness.Client.OnRenew = (_, _) => Task.FromResult(AgentBackendResult<DeleteReplicaLeaseResponse>.Success(
            new DeleteReplicaLeaseResponse { LeaseExpiresAt = harness.Time.GetUtcNow().AddSeconds(5) }));
        var envelope = harness.Envelope();
        await harness.PutAsync(envelope.DeleteReplica!);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Storage.Calls.Count == 1, "waiting physical deletion");
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => harness.Client.Renewals.Count == 2, "short lease renewal");
        harness.Time.Advance(TimeSpan.FromSeconds(3));
        await Wait.UntilAsync(() => harness.Client.Renewals.Count == 3, "renewal beyond original expiry");
        release.SetResult();
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "completion with renewed authority");
        Assert.True(Assert.Single(harness.Client.Completions).AbsenceVerified);
        Assert.Equal(["delete", "exists"], harness.Storage.Calls.ToArray());
    }

    [Fact]
    public async Task Completion_rate_limit_waits_for_retry_after_without_repeating_delete()
    {
        await using var harness = new DeleteReplicaHarness();
        harness.Client.OnComplete = (_, _) => Task.FromResult(AgentBackendResult<bool>.Fail(
            new AgentBackendFailure(AgentBackendFailureKind.RateLimited, "wait") { RetryAfter = TimeSpan.FromSeconds(12) }));
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "rate limited report");
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(harness.Client.Completions);
        harness.Client.OnComplete = null;
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 2, "delayed completion retry");
        Assert.Equal(["delete", "exists"], harness.Storage.Calls.ToArray());
    }

    [Fact]
    public async Task Terminal_completion_refusal_is_not_retried_and_next_claim_can_execute()
    {
        await using var harness = new DeleteReplicaHarness();
        harness.Client.OnComplete = (_, _) => Task.FromResult(Fail<bool>(AgentBackendFailureKind.Rejected));
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "terminal refusal");
        harness.Client.OnComplete = null;
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 2, "subsequent claim");
        Assert.Equal(["delete", "exists", "delete", "exists"], harness.Storage.Calls.ToArray());
    }

    private static AgentBackendResult<T> Fail<T>(AgentBackendFailureKind failure) =>
        AgentBackendResult<T>.Fail(new AgentBackendFailure(failure, "transport refusal"));
}
