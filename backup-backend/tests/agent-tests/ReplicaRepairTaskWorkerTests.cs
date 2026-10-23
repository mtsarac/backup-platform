using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.AgentTests;

public sealed class ReplicaRepairTaskWorkerTests
{
    [Theory]
    [InlineData("sftp")]
    [InlineData("google-drive")]
    public async Task Verified_copy_is_pinned_before_the_source_is_read_and_completes_with_the_target_verification(string targetProvider)
    {
        await using var harness = new ReplicaRepairHarness(targetProvider);
        var record = new TaskStorageCatalogRecord("file-1", "folder-1", "repair.bak", ReplicaRepairHarness.Content.Length, ReplicaRepairHarness.Checksum, "md5");
        harness.Target.DriveRecord = record;
        var task = harness.Snapshot(targetProvider);
        var targetKey = ReplicaRepairHarness.ExpectedTargetKey(task, task.Sources[0]);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(ReplicaRepairHarness.Wrap(task)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");

        Assert.Equal(["renew", "pin", "read:source1", "put:target", "complete"], harness.Events.ToArray());
        var pin = Assert.Single(harness.Client.Pins);
        Assert.Equal(task.Sources[0].ReplicaId, pin.SourceReplicaId);
        Assert.Equal(targetKey, pin.TargetKey);
        Assert.Equal(task.LeaseToken, pin.LeaseToken);
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal(ReplicaRepairOutcomes.Succeeded, completion.Outcome);
        Assert.Equal(task.LeaseToken, completion.LeaseToken);
        Assert.Equal(nameof(StorageVerificationMethod.ReadBack), completion.VerificationMethod);
        Assert.Equal(targetProvider == "google-drive" ? record : null, completion.DriveRecord);
        Assert.Null(completion.OrphanKey);
        Assert.Equal(ReplicaRepairHarness.Content, harness.Target.Get(targetKey));
        Assert.Empty(harness.Client.CleanupChecks);
    }

    [Fact]
    public async Task Storage_access_is_requested_only_through_the_lease_scoped_repair_routes()
    {
        await using var harness = new ReplicaRepairHarness();
        var task = harness.Snapshot();
        harness.Start();
        Assert.True(harness.Worker.TryPublish(ReplicaRepairHarness.Wrap(task)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");

        var scopes = harness.Storage.Scopes.ToArray();
        Assert.Equal(2, scopes.Length);
        var source = Assert.Single(scopes, scope => scope.ConnectionId == harness.Source1Connection);
        var target = Assert.Single(scopes, scope => scope.ConnectionId == harness.TargetConnection);
        Assert.Equal($"api/agent/replica-repairs/{task.OperationId:D}/storage/source/access", source.AccessPath);
        Assert.Equal($"api/agent/replica-repairs/{task.OperationId:D}/storage/target/access", target.AccessPath);
        Assert.All(scopes, scope =>
        {
            Assert.Equal(task.LeaseToken, scope.LeaseToken);
            Assert.Equal(task.TenantId, scope.TenantId);
            Assert.Equal("sftp", scope.ProviderType);
        });
    }

    [Fact]
    public async Task Progress_is_reported_only_for_every_ten_mebibytes()
    {
        await using var harness = new ReplicaRepairHarness();
        const long MiB = 1024 * 1024;
        harness.Target.ReportedProgress = [3 * MiB, 12 * MiB, 14 * MiB, 30 * MiB];
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");
        Assert.Equal([12 * MiB, 30 * MiB], harness.Client.Progress.Select(request => request.TransferredBytes).ToArray());
    }

    [Fact]
    public async Task Refused_pin_falls_through_to_the_next_candidate_without_reading_the_refused_one()
    {
        await using var harness = new ReplicaRepairHarness();
        var task = harness.Snapshot();
        harness.Client.AcceptPin = request => request.SourceReplicaId == task.Sources[1].ReplicaId;
        harness.Start();
        Assert.True(harness.Worker.TryPublish(ReplicaRepairHarness.Wrap(task)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");

        Assert.Equal(["renew", "pin", "pin", "read:source2", "put:target", "complete"], harness.Events.ToArray());
        Assert.Equal(ReplicaRepairOutcomes.Succeeded, Assert.Single(harness.Client.Completions).Outcome);
        Assert.Equal(ReplicaRepairHarness.Content, harness.Target.Get(ReplicaRepairHarness.ExpectedTargetKey(task, task.Sources[1])));
    }

    [Fact]
    public async Task Unreadable_sources_fail_the_repair_without_touching_the_target()
    {
        await using var harness = new ReplicaRepairHarness();
        harness.Source1.OpenFailure = () => new StorageException(StorageFailureKind.Io, "unreadable");
        harness.Source2.OpenFailure = () => new StorageException(StorageFailureKind.NotFound, "missing");
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal(ReplicaRepairOutcomes.Failed, completion.Outcome);
        Assert.Equal("SourceInaccessible", completion.FailureCode);
        Assert.Null(completion.OrphanKey);
        Assert.Equal(2, harness.Client.Pins.Count);
        Assert.DoesNotContain(harness.Events, entry => entry.StartsWith("put:", StringComparison.Ordinal) || entry.StartsWith("delete:", StringComparison.Ordinal));
        Assert.Empty(harness.Client.CleanupChecks);
    }

    [Fact]
    public async Task Pins_refused_for_every_candidate_read_nothing()
    {
        await using var harness = new ReplicaRepairHarness();
        harness.Client.AcceptPin = _ => false;
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");

        Assert.Equal("SourceInaccessible", Assert.Single(harness.Client.Completions).FailureCode);
        Assert.DoesNotContain(harness.Events, entry => entry.StartsWith("read:", StringComparison.Ordinal) || entry.StartsWith("put:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Source_checksum_mismatch_deletes_the_partial_target_only_when_the_backend_allows_it(bool cleanupAllowed)
    {
        await using var harness = new ReplicaRepairHarness();
        var corrupt = ReplicaRepairHarness.Content.ToArray();
        corrupt[0] ^= 0xFF;
        harness.Source1.Seed(ReplicaRepairHarness.Source1Key, corrupt);
        harness.Client.CleanupAllowed = cleanupAllowed;
        var task = harness.Snapshot();
        var targetKey = ReplicaRepairHarness.ExpectedTargetKey(task, task.Sources[0]);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(ReplicaRepairHarness.Wrap(task)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal(ReplicaRepairOutcomes.Failed, completion.Outcome);
        Assert.Equal("SourceChecksumMismatch", completion.FailureCode);
        Assert.Equal(targetKey, Assert.Single(harness.Client.CleanupChecks).TargetKey);
        Assert.False(completion.OrphanQuarantine);
        Assert.Equal(cleanupAllowed, !harness.Target.Has(targetKey));
        Assert.Equal(cleanupAllowed, harness.Events.Contains("delete:target"));
        Assert.Equal(cleanupAllowed ? null : targetKey, completion.OrphanKey);
    }

    [Fact]
    public async Task AReportedDeleteWithoutVerifiedAbsenceLeavesADurableOrphan()
    {
        await using var harness = new ReplicaRepairHarness();
        harness.Target.CorruptOnPut = true;
        harness.Target.DeleteLeavesObject = true;
        var task = harness.Snapshot();
        var targetKey = ReplicaRepairHarness.ExpectedTargetKey(task, task.Sources[0]);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(ReplicaRepairHarness.Wrap(task)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "unconfirmed cleanup completion");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("DestinationReadbackFailed", completion.FailureCode);
        Assert.Equal(targetKey, completion.OrphanKey);
        Assert.True(harness.Target.Has(targetKey));
    }

    [Fact]
    public async Task Failed_destination_readback_is_cleaned_up_and_reported()
    {
        await using var harness = new ReplicaRepairHarness();
        harness.Target.CorruptOnPut = true;
        var task = harness.Snapshot();
        harness.Start();
        Assert.True(harness.Worker.TryPublish(ReplicaRepairHarness.Wrap(task)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("DestinationReadbackFailed", completion.FailureCode);
        Assert.Null(completion.OrphanKey);
        Assert.False(harness.Target.Has(ReplicaRepairHarness.ExpectedTargetKey(task, task.Sources[0])));
    }

    [Fact]
    public async Task Google_drive_target_without_an_object_record_is_not_reported_as_success()
    {
        await using var harness = new ReplicaRepairHarness("google-drive");
        var task = harness.Snapshot("google-drive");
        harness.Start();
        Assert.True(harness.Worker.TryPublish(ReplicaRepairHarness.Wrap(task)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal(ReplicaRepairOutcomes.Failed, completion.Outcome);
        Assert.Equal("InternalError", completion.FailureCode);
        Assert.Null(completion.DriveRecord);
        Assert.False(harness.Target.Has(ReplicaRepairHarness.ExpectedTargetKey(task, task.Sources[0])));
    }

    [Fact]
    public async Task Existing_target_content_is_a_conflict_and_is_never_deleted()
    {
        await using var harness = new ReplicaRepairHarness();
        var task = harness.Snapshot();
        var targetKey = ReplicaRepairHarness.ExpectedTargetKey(task, task.Sources[0]);
        harness.Target.Seed(targetKey, [1, 2, 3]);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(ReplicaRepairHarness.Wrap(task)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal(ReplicaRepairOutcomes.TargetConflict, completion.Outcome);
        Assert.Null(completion.OrphanKey);
        Assert.Equal<byte>([1, 2, 3], harness.Target.Get(targetKey)!);
        Assert.Empty(harness.Client.CleanupChecks);
        Assert.DoesNotContain("delete:target", harness.Events);
    }

    [Theory]
    [InlineData(StorageFailureKind.InsufficientSpace, "QuotaExceeded")]
    [InlineData(StorageFailureKind.Timeout, "OperationTimedOut")]
    [InlineData(StorageFailureKind.Io, "StorageWriteFailed")]
    [InlineData(StorageFailureKind.AccessDenied, "StorageWriteFailed")]
    [InlineData(StorageFailureKind.NetworkUnavailable, "StorageUnavailable")]
    public async Task Put_failure_maps_to_the_repair_failure_code_and_quarantines_the_key_when_no_object_may_remain(StorageFailureKind kind, string code)
    {
        await using var harness = new ReplicaRepairHarness();
        harness.Target.PutFailure = () => new StorageException(kind, "failed");
        var task = harness.Snapshot();
        harness.Start();
        Assert.True(harness.Worker.TryPublish(ReplicaRepairHarness.Wrap(task)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal(ReplicaRepairOutcomes.Failed, completion.Outcome);
        Assert.Equal(code, completion.FailureCode);
        Assert.Equal(ReplicaRepairHarness.ExpectedTargetKey(task, task.Sources[0]), completion.OrphanKey);
        Assert.True(completion.OrphanQuarantine);
        Assert.DoesNotContain("delete:target", harness.Events);
    }

    [Fact]
    public async Task Put_failure_that_may_leave_an_object_goes_through_the_fenced_cleanup()
    {
        await using var harness = new ReplicaRepairHarness();
        harness.Target.PutFailure = () => new StorageException(StorageFailureKind.Io, "failed", objectMayRemain: true);
        var task = harness.Snapshot();
        harness.Target.Seed(ReplicaRepairHarness.ExpectedTargetKey(task, task.Sources[0]), [1, 2, 3]);
        harness.Target.DeleteLeavesObject = true;
        harness.Start();
        Assert.True(harness.Worker.TryPublish(ReplicaRepairHarness.Wrap(task)));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("StorageWriteFailed", completion.FailureCode);
        Assert.False(completion.OrphanQuarantine);
        Assert.Single(harness.Client.CleanupChecks);
        // Nothing was stored, so the delete reports that no object was removed and the key stays an orphan to reconcile.
        Assert.Equal(ReplicaRepairHarness.ExpectedTargetKey(task, task.Sources[0]), completion.OrphanKey);
    }

    [Fact]
    public async Task Unexpected_exception_is_reported_by_type_name_only()
    {
        await using var harness = new ReplicaRepairHarness();
        harness.Target.DuringPut = _ => throw new InvalidOperationException("secret-detail");
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("InternalError", completion.FailureCode);
        Assert.Contains(nameof(InvalidOperationException), completion.Reason);
        Assert.DoesNotContain("secret-detail", completion.Reason);
        Assert.Null(completion.OrphanKey);
    }

    [Fact]
    public async Task Refused_lease_at_start_touches_no_storage_and_reports_nothing()
    {
        await using var harness = new ReplicaRepairHarness();
        harness.Client.OnRenew = (_, _) => Task.FromResult(Fail<ReplicaRepairLeaseResponse>(AgentBackendFailureKind.Rejected));
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope()));
        await Wait.UntilAsync(() => harness.Client.Renewals.Count == 1, "refused authority request");

        // The single execution slot is free again only after the refused repair is over.
        harness.Client.OnRenew = null;
        var next = harness.Envelope();
        await Wait.UntilAsync(() => harness.Worker.TryPublish(next), "next repair accepted");
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "next repair completion");
        Assert.Equal(next.ReplicaRepair!.LeaseToken, Assert.Single(harness.Client.Completions).LeaseToken);
        Assert.Equal(next.ReplicaRepair.LeaseToken, Assert.Single(harness.Client.Pins).LeaseToken);
        Assert.Equal(2, harness.Client.Renewals.Count);
    }

    [Fact]
    public async Task Cancellation_requested_during_the_transfer_cleans_the_target_and_reports_cancelled()
    {
        await using var harness = new ReplicaRepairHarness();
        harness.Target.DuringPut = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var task = harness.Snapshot();
        harness.Start();
        Assert.True(harness.Worker.TryPublish(ReplicaRepairHarness.Wrap(task)));
        await Wait.UntilAsync(() => harness.Events.Contains("put:target"), "transfer in flight");
        harness.Client.OnRenew = (_, _) => Task.FromResult(AgentBackendResult<ReplicaRepairLeaseResponse>.Success(
            new ReplicaRepairLeaseResponse { LeaseExpiresAt = harness.Time.GetUtcNow().AddSeconds(120), CancellationRequested = true }));
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "cancelled completion");

        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal(ReplicaRepairOutcomes.Cancelled, completion.Outcome);
        Assert.Null(completion.OrphanKey);
        Assert.Equal(ReplicaRepairHarness.ExpectedTargetKey(task, task.Sources[0]), Assert.Single(harness.Client.CleanupChecks).TargetKey);
        Assert.False(harness.Target.Has(ReplicaRepairHarness.ExpectedTargetKey(task, task.Sources[0])));
    }

    [Fact]
    public async Task Lost_lease_stops_the_transfer_and_reports_nothing_about_the_old_claim()
    {
        await using var harness = new ReplicaRepairHarness();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Target.DuringPut = async token =>
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
        var lost = harness.Envelope();
        harness.Start();
        Assert.True(harness.Worker.TryPublish(lost));
        await Wait.UntilAsync(() => harness.Events.Contains("put:target"), "transfer in flight");
        harness.Client.OnRenew = (_, _) => Task.FromResult(Fail<ReplicaRepairLeaseResponse>(AgentBackendFailureKind.Rejected));
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Client.OnRenew = null;
        harness.Target.DuringPut = null;
        var next = harness.Envelope();
        await Wait.UntilAsync(() => harness.Worker.TryPublish(next), "next repair accepted");
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "next repair completion");
        Assert.Equal(next.ReplicaRepair!.LeaseToken, Assert.Single(harness.Client.Completions).LeaseToken);
        Assert.DoesNotContain("cleanup-check", harness.Events);
        Assert.DoesNotContain("delete:target", harness.Events);
    }

    [Fact]
    public async Task Dispatcher_routes_repair_tasks_to_the_repair_worker()
    {
        await using var harness = new ReplicaRepairHarness();
        var dispatcher = new AgentTaskDispatcher(null!, null!, null!, null!, null!, null!, harness.Worker);
        Assert.True(dispatcher.TryPublish(harness.Envelope()));
        Assert.False(dispatcher.TryPublish(new AgentTaskEnvelope { Type = "replica-repair-unknown" }));
    }

    [Fact]
    public async Task Same_lease_is_never_executed_twice_but_a_fresh_lease_is()
    {
        await using var harness = new ReplicaRepairHarness();
        var envelope = harness.Envelope();
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => harness.Client.Completions.Count == 1, "repair completion");
        Assert.False(harness.Worker.TryPublish(envelope));
        var fresh = envelope with { ReplicaRepair = envelope.ReplicaRepair! with { LeaseToken = Guid.NewGuid() } };
        Assert.True(harness.Worker.TryPublish(fresh));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("version")]
    [InlineData("envelope-id")]
    [InlineData("operation")]
    [InlineData("tenant")]
    [InlineData("agent")]
    [InlineData("artifact")]
    [InlineData("lease")]
    [InlineData("expired")]
    [InlineData("size")]
    [InlineData("checksum-short")]
    [InlineData("checksum-not-hex")]
    [InlineData("no-sources")]
    [InlineData("source-provider")]
    [InlineData("source-central")]
    [InlineData("source-connection")]
    [InlineData("source-replica")]
    [InlineData("source-key")]
    [InlineData("target-provider")]
    [InlineData("target-connection")]
    public async Task Malformed_or_expired_snapshots_are_refused(string defect)
    {
        await using var harness = new ReplicaRepairHarness();
        var task = harness.Snapshot();
        var source = task.Sources[0];
        var envelope = ReplicaRepairHarness.Wrap(task);
        envelope = defect switch
        {
            "type" => envelope with { Type = "delete-replica" },
            "version" => envelope with { ReplicaRepair = task with { Version = 2 } },
            "envelope-id" => envelope with { Id = Guid.NewGuid().ToString() },
            "operation" => ReplicaRepairHarness.Wrap(task with { OperationId = Guid.Empty }),
            "tenant" => envelope with { ReplicaRepair = task with { TenantId = Guid.Empty } },
            "agent" => envelope with { ReplicaRepair = task with { AgentId = Guid.Empty } },
            "artifact" => envelope with { ReplicaRepair = task with { ArtifactId = Guid.Empty } },
            "lease" => envelope with { ReplicaRepair = task with { LeaseToken = Guid.Empty } },
            "expired" => envelope with { ReplicaRepair = task with { LeaseExpiresAt = harness.Time.GetUtcNow() } },
            "size" => envelope with { ReplicaRepair = task with { SizeBytes = 0 } },
            "checksum-short" => envelope with { ReplicaRepair = task with { Checksum = task.Checksum[..63] } },
            "checksum-not-hex" => envelope with { ReplicaRepair = task with { Checksum = new string('z', 64) } },
            "no-sources" => envelope with { ReplicaRepair = task with { Sources = [] } },
            "source-provider" => envelope with { ReplicaRepair = task with { Sources = [source with { StorageProvider = "ftp" }] } },
            "source-central" => envelope with { ReplicaRepair = task with { Sources = [source with { StorageProvider = "central" }] } },
            "source-connection" => envelope with { ReplicaRepair = task with { Sources = [source with { StorageConnectionId = Guid.Empty }] } },
            "source-replica" => envelope with { ReplicaRepair = task with { Sources = [source with { ReplicaId = Guid.Empty }] } },
            "source-key" => envelope with { ReplicaRepair = task with { Sources = [source with { StorageKey = "../escape" }] } },
            "target-provider" => envelope with { ReplicaRepair = task with { Target = task.Target with { StorageProvider = "s3" } } },
            "target-connection" => envelope with { ReplicaRepair = task with { Target = task.Target with { StorageConnectionId = Guid.Empty } } },
            _ => throw new InvalidOperationException()
        };
        Assert.False(harness.Worker.TryPublish(envelope));
        Assert.True(harness.Worker.TryPublish(ReplicaRepairHarness.Wrap(task)));
    }

    private static AgentBackendResult<T> Fail<T>(AgentBackendFailureKind kind) => AgentBackendResult<T>.Fail(new AgentBackendFailure(kind, "refused"));
}
