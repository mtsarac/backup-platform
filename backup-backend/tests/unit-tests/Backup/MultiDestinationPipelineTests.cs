using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.UnitTests.Backup;

/// <summary>One engine backup, one artifact, N independently transferred and verified copies, driven through the real pipeline with fake storages.</summary>
public sealed class MultiDestinationPipelineTests : IDisposable
{
    private readonly PipelineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private InMemoryStorage NewStorage() => new(_h);

    private static BackupPipelineDestination Destination(string provider, InMemoryStorage storage, bool primary = false) =>
        new(new StorageProviderId(provider), storage, Guid.NewGuid(), primary);

    private DatabaseBackupPipelineRequest Request(IReadOnlyList<BackupPipelineDestination> destinations, int required = 1, TimeSpan? timeout = null)
    {
        var single = _h.Request();
        return new DatabaseBackupPipelineRequest(single.Engine, single.Connection, single.Credential, single.DatabaseName, single.Staging,
            destinations[0].Storage, single.StoragePrefix, single.Options, single.Overwrite, stagingOnFailure: single.StagingOnFailure)
        {
            StorageOverride = destinations[0].StorageOverride,
            Destinations = destinations,
            RequiredVerifiedReplicas = required,
            DestinationTimeout = timeout
        };
    }

    [Fact]
    public async Task One_engine_backup_becomes_three_verified_replicas_of_one_artifact()
    {
        var stores = new[] { NewStorage(), NewStorage(), NewStorage() };
        var destinations = new[] { Destination("central", stores[0], true), Destination("sftp", stores[1]), Destination("google-drive", stores[2]) };

        var result = await _h.RunAsync(Request(destinations, required: 2));

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        Assert.Single(_h.Provider.Requests);
        Assert.All(result.Destinations, destination => Assert.Equal(BackupDestinationState.Verified, destination.State));
        Assert.Equal(3, result.Destinations.Count);
        var artifact = result.Artifact!;
        Assert.All(result.Destinations, destination =>
        {
            Assert.Equal(artifact.Checksum, destination.Replica!.Checksum);
            Assert.Equal(artifact.SizeBytes, destination.Replica.SizeBytes);
        });
        // Every destination received the complete artifact, from a stream of its own.
        Assert.All(stores, store => Assert.Equal(PipelineHarness.BackupBytes, Assert.Single(store.Objects).Value));
        Assert.Equal(destinations.Select(d => d.ConnectionId), result.Destinations.Select(d => d.Destination.ConnectionId));
        Assert.Empty(_h.StagedFiles);
    }

    [Fact]
    public async Task The_staged_artifact_is_still_there_for_every_destination_and_removed_only_afterwards()
    {
        var stores = new[] { NewStorage(), NewStorage(), NewStorage() };
        var staged = new List<string[]>();
        foreach (var store in stores)
        {
            var inner = store;
            inner.OnPut = async (key, content, options, ct) =>
            {
                staged.Add(_h.StagedFiles);
                using var buffer = new MemoryStream();
                await content.CopyToAsync(buffer, ct);
                return new StorageObjectMetadata(new StorageLocation(inner.Id, key), buffer.Length, InMemoryStorage.Checksum(buffer.ToArray()), DateTimeOffset.UtcNow);
            };
            inner.OnVerify = (key, expected) => new StorageVerificationResult(new StorageLocation(inner.Id, key), StorageVerificationStatus.Verified,
                StorageVerificationMethod.ReadBack, expected.SizeBytes, expected.Checksum);
        }

        var result = await _h.RunAsync(Request([Destination("central", stores[0], true), Destination("sftp", stores[1]), Destination("google-drive", stores[2])]));

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        Assert.Equal(3, staged.Count);
        Assert.All(staged, files => Assert.Single(files));
        Assert.Empty(_h.StagedFiles);
    }

    [Fact]
    public async Task A_failing_primary_does_not_stop_the_secondary_and_the_run_keeps_its_artifact()
    {
        var primary = NewStorage();
        primary.OnPut = (_, _, _, _) => throw new StorageException(StorageFailureKind.Io, "primary down");
        var secondary = NewStorage();

        var result = await _h.RunAsync(Request([Destination("central", primary, true), Destination("sftp", secondary)]));

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        Assert.Equal(BackupDestinationState.Failed, result.Destinations[0].State);
        Assert.Equal(BackupPipelineReasons.StorageIoError, result.Destinations[0].Failure!.ReasonCode);
        Assert.False(result.Destinations[0].Uploaded);
        Assert.Equal(BackupDestinationState.Verified, result.Destinations[1].State);
        Assert.Same(result.Destinations[1].Replica, result.Replica);
        Assert.Empty(primary.Objects);
        Assert.Single(secondary.Objects);
    }

    [Fact]
    public async Task A_failing_secondary_does_not_touch_the_verified_primary()
    {
        var primary = NewStorage();
        var secondary = NewStorage();
        secondary.OnPut = (_, _, _, _) => throw new StorageException(StorageFailureKind.AccessDenied, "denied");

        var result = await _h.RunAsync(Request([Destination("central", primary, true), Destination("sftp", secondary)]));

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        Assert.Equal(BackupDestinationState.Verified, result.Destinations[0].State);
        Assert.Equal(BackupDestinationState.Failed, result.Destinations[1].State);
        Assert.Equal(BackupFailureCause.PermissionDenied, result.Destinations[1].Failure!.Cause);
        Assert.Single(primary.Objects);
    }

    [Fact]
    public async Task When_every_destination_fails_the_run_fails_with_the_primary_failure_and_still_reports_each_destination()
    {
        var stores = new[] { NewStorage(), NewStorage(), NewStorage() };
        stores[0].OnPut = (_, _, _, _) => throw new StorageException(StorageFailureKind.AccessDenied, "denied");
        stores[1].OnPut = (_, _, _, _) => throw new StorageException(StorageFailureKind.Io, "io");
        stores[2].OnPut = (_, _, _, _) => throw new StorageException(StorageFailureKind.Io, "io");

        var result = await _h.RunAsync(Request([Destination("central", stores[0], true), Destination("sftp", stores[1]), Destination("google-drive", stores[2])]));

        Assert.Equal(DatabaseBackupStatus.Failed, result.Status);
        Assert.Equal(BackupFailureCause.PermissionDenied, result.Failure!.Cause);
        Assert.Null(result.Replica);
        Assert.All(result.Destinations, destination => Assert.Equal(BackupDestinationState.Failed, destination.State));
        Assert.Single(_h.Provider.Requests);
    }

    [Fact]
    public async Task A_destination_that_times_out_fails_alone()
    {
        var slow = NewStorage();
        slow.OnPut = async (_, _, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
        var fine = NewStorage();

        var result = await _h.RunAsync(Request([Destination("sftp", slow, true), Destination("central", fine)], timeout: TimeSpan.FromMilliseconds(150)));

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        var timedOut = result.Destinations[0];
        Assert.Equal(BackupDestinationState.Failed, timedOut.State);
        Assert.Equal(BackupPipelineReasons.DestinationTimeout, timedOut.Failure!.ReasonCode);
        Assert.Equal(BackupFailureCause.Timeout, timedOut.Failure.Cause);
        Assert.Equal(BackupDestinationState.Verified, result.Destinations[1].State);
    }

    [Fact]
    public async Task Cancelling_after_a_verified_copy_publishes_it_and_marks_the_rest_cancelled_without_deleting_it()
    {
        using var cancel = new CancellationTokenSource();
        var first = NewStorage();
        var second = NewStorage();
        second.OnPut = async (_, _, _, ct) =>
        {
            await cancel.CancelAsync();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        };
        var third = NewStorage();

        var result = await _h.RunAsync(Request([Destination("central", first, true), Destination("sftp", second), Destination("google-drive", third)]), cancel.Token);

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        Assert.Equal(BackupDestinationState.Verified, result.Destinations[0].State);
        Assert.Equal(BackupDestinationState.Cancelled, result.Destinations[1].State);
        Assert.Equal(BackupDestinationState.Cancelled, result.Destinations[2].State);
        Assert.Empty(third.Objects);
        Assert.Single(first.Objects);
    }

    [Fact]
    public async Task Cancelling_before_any_copy_exists_cancels_the_run()
    {
        using var cancel = new CancellationTokenSource();
        var only = NewStorage();
        only.OnPut = async (_, _, _, ct) =>
        {
            await cancel.CancelAsync();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        };

        var result = await _h.RunAsync(Request([Destination("central", only, true), Destination("sftp", NewStorage())]), cancel.Token);

        Assert.Equal(DatabaseBackupStatus.Cancelled, result.Status);
        Assert.Null(result.Replica);
        Assert.Empty(_h.StagedFiles);
    }

    [Fact]
    public async Task A_checksum_mismatch_on_one_destination_fails_it_removes_its_object_and_leaves_the_others()
    {
        var good = NewStorage();
        var bad = NewStorage();
        bad.OnVerify = (key, expected) => new StorageVerificationResult(new StorageLocation(bad.Id, key), StorageVerificationStatus.ChecksumMismatch,
            StorageVerificationMethod.ReadBack, expected.SizeBytes, ContentChecksum.Sha256(new byte[32]));

        var result = await _h.RunAsync(Request([Destination("central", good, true), Destination("sftp", bad)]));

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        var failed = result.Destinations[1];
        Assert.Equal(BackupDestinationState.Failed, failed.State);
        Assert.Equal(BackupPipelineReasons.ReplicaChecksumMismatch, failed.Failure!.ReasonCode);
        Assert.Equal(BackupFailureKind.ReplicaVerificationFailed, failed.Failure.Kind);
        Assert.True(failed.Uploaded);
        Assert.False(failed.ObjectMayRemain);
        Assert.Empty(bad.Objects);
        Assert.Single(good.Objects);
    }

    [Fact]
    public async Task An_unverified_object_that_cannot_be_removed_is_reported_as_possibly_remaining()
    {
        var bad = NewStorage();
        bad.OnVerify = (key, expected) => new StorageVerificationResult(new StorageLocation(bad.Id, key), StorageVerificationStatus.Missing,
            StorageVerificationMethod.ReadBack, null, null);
        bad.OnDelete = _ => throw new StorageException(StorageFailureKind.Io, "cannot delete");

        var result = await _h.RunAsync(Request([Destination("central", NewStorage(), true), Destination("sftp", bad)]));

        var failed = result.Destinations[1];
        Assert.Equal(BackupDestinationState.Failed, failed.State);
        Assert.True(failed.ObjectMayRemain);
        Assert.NotNull(failed.Key);
        Assert.Contains(result.CleanupFailures, failure => failure.ReasonCode == BackupPipelineReasons.ReplicaCleanupFailed);
    }

    [Fact]
    public async Task A_destination_this_agent_cannot_reach_is_reported_failed_not_skipped()
    {
        var missing = new BackupPipelineDestination(new StorageProviderId("central"), null, Guid.NewGuid(), false);

        var result = await _h.RunAsync(Request([Destination("sftp", NewStorage(), true), missing]));

        Assert.Equal(DatabaseBackupStatus.Succeeded, result.Status);
        Assert.Equal(BackupDestinationState.Verified, result.Destinations[0].State);
        Assert.Equal(BackupDestinationState.Failed, result.Destinations[1].State);
        Assert.Equal(BackupPipelineReasons.StorageNotAvailable, result.Destinations[1].Failure!.ReasonCode);
    }

    [Fact]
    public async Task A_backup_that_can_never_reach_the_required_copies_is_refused_before_the_engine_runs()
    {
        var missing = new BackupPipelineDestination(new StorageProviderId("central"), null, Guid.NewGuid(), false);

        var result = await _h.RunAsync(Request([Destination("sftp", NewStorage(), true), missing], required: 2));

        Assert.Equal(DatabaseBackupStatus.Failed, result.Status);
        Assert.Equal(BackupPipelineReasons.StorageNotAvailable, result.Failure!.ReasonCode);
        Assert.Empty(_h.Provider.Requests);
    }

    [Fact]
    public async Task A_staged_artifact_that_changed_is_not_sent_to_a_later_destination()
    {
        var first = NewStorage();
        var second = NewStorage();
        first.OnPut = async (key, content, options, ct) =>
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            // Tamper with the shared staged file between destinations.
            var staged = Directory.GetFiles(_h.StagingDirectory).Single();
            await File.AppendAllTextAsync(staged, "tampered", ct);
            return new StorageObjectMetadata(new StorageLocation(first.Id, key), buffer.Length, InMemoryStorage.Checksum(buffer.ToArray()), DateTimeOffset.UtcNow);
        };
        first.OnVerify = (key, expected) => new StorageVerificationResult(new StorageLocation(first.Id, key), StorageVerificationStatus.Verified,
            StorageVerificationMethod.ReadBack, expected.SizeBytes, expected.Checksum);

        var result = await _h.RunAsync(Request([Destination("central", first, true), Destination("sftp", second)]));

        Assert.Equal(BackupDestinationState.Verified, result.Destinations[0].State);
        Assert.Equal(BackupDestinationState.Failed, result.Destinations[1].State);
        Assert.Equal(BackupPipelineReasons.StagedFileChanged, result.Destinations[1].Failure!.ReasonCode);
        Assert.Empty(second.Objects);
    }
}
