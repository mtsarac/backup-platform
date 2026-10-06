using BackupPlatform.Application.Backups;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Domain.Backups;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// The retention lifecycle through the real store and database: expiry, the grace period, the leased physical delete with its retry and its
/// crash recovery, manual delete and cancel, and the history that outlives every deletion. Time is always an explicit value.
/// </summary>
public sealed class RetentionStoreLifecycleTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(6);
    private static readonly TimeSpan Step = RetentionWorld.Step;
    private static readonly TimeSpan Lease = RetentionWorld.Lease;
    private static DateTimeOffset Expiry => RetentionWorld.Accepted + Keep;
    private readonly RetentionWorld _world = new(fixture);

    private Task<SeededBackup> SeedAsync(BackupRetentionPolicy? policy = null) => _world.SeedAsync(policy ?? RetentionKit.Policy(keepFor: Keep, grace: Grace));

    // ---- expiry -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisabledRetentionNeverExpiresABackupHoweverLateTheScanRuns()
    {
        var backup = await SeedAsync(RetentionKit.Policy(enabled: false));
        var store = _world.Store();

        await store.ApplyExpiredRetentionAsync(RetentionWorld.Accepted.AddYears(200), 1000, default);

        var artifact = await _world.ArtifactAsync(backup.ArtifactId);
        Assert.Null(artifact.RetentionExpiresAt);
        Assert.Null(artifact.RetentionAppliedAt);
        Assert.Equal(BackupReplicaStatus.Available, Assert.Single(artifact.Replicas).Status);
    }

    [Fact]
    public async Task ExpiryCountsFromTheBackendAcceptanceAndTheBoundaryIsInclusive()
    {
        var backup = await SeedAsync();
        var store = _world.Store();
        var artifact = await _world.ArtifactAsync(backup.ArtifactId);

        // The agent reported a time 400 days earlier; only the backend's acceptance counts.
        Assert.Equal(RetentionWorld.Accepted, artifact.AcceptedAt);
        Assert.Equal(Expiry, artifact.RetentionExpiresAt);

        await store.ApplyExpiredRetentionAsync(Expiry - Step, 1000, default);
        Assert.Equal(BackupReplicaStatus.Available, (await _world.ReplicaAsync(backup.ReplicaId)).Status);
        Assert.Null((await _world.ArtifactAsync(backup.ArtifactId)).RetentionAppliedAt);

        var result = await store.ApplyExpiredRetentionAsync(Expiry, 1000, default);

        Assert.True(result.ArtifactsExamined >= 1);
        Assert.True(result.ReplicasMoved >= 1);
        Assert.Equal(BackupReplicaStatus.DeletePending, (await _world.ReplicaAsync(backup.ReplicaId)).Status);
    }

    [Fact]
    public async Task AnExpiredBackupIsSoftDeletedWithItsOwnGraceStaysIntactAndUsableAndASecondScanChangesNothing()
    {
        var backup = await SeedAsync();
        var store = _world.Store();
        var scanAt = Expiry + TimeSpan.FromHours(3);

        await store.ApplyExpiredRetentionAsync(scanAt, 1000, default);

        var replica = await _world.ReplicaAsync(backup.ReplicaId);
        Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status);
        Assert.Equal(BackupDeleteReason.RetentionExpired, replica.DeleteReason);
        Assert.Null(replica.DeleteRequestedByUserId);
        Assert.Equal(scanAt, replica.DeleteRequestedAt);
        Assert.Equal(scanAt + Grace, replica.DeleteAfter);
        Assert.True(replica.IsUsable);
        Assert.Equal(0, replica.DeleteAttempts);
        Assert.Equal(scanAt, (await _world.ArtifactAsync(backup.ArtifactId)).RetentionAppliedAt);
        // Nothing physical happened: the location and checksum are exactly what was stored.
        Assert.Equal(backup.Key, replica.Key);
        Assert.Equal(BackupStoreHarness.ArtifactHash, replica.Checksum);

        await store.ApplyExpiredRetentionAsync(scanAt + TimeSpan.FromDays(1), 1000, default);

        var again = await _world.ReplicaAsync(backup.ReplicaId);
        Assert.Equal(replica.DeleteAfter, again.DeleteAfter);
        Assert.Equal(replica.DeleteRequestedAt, again.DeleteRequestedAt);
    }

    [Fact]
    public async Task TheScanHandlesAtMostItsLimitEarliestExpiredFirstAndSuccessiveScansConverge()
    {
        await BackupStoreHarness.WithIsolatedDatabaseAsync(async harness =>
        {
            var world = new RetentionWorld(harness.Database);
            var store = world.Store();
            var policy = RetentionKit.Policy(keepFor: Keep, grace: Grace);
            var first = await world.SeedAsync(policy, RetentionWorld.Accepted);
            var second = await world.SeedAsync(policy, RetentionWorld.Accepted.AddHours(1));
            var third = await world.SeedAsync(policy, RetentionWorld.Accepted.AddHours(2));
            var late = Expiry.AddDays(1);

            var batch = await store.ApplyExpiredRetentionAsync(late, 2, default);

            Assert.Equal(new BackupRetentionApplication(2, 2), batch);
            Assert.Equal(BackupReplicaStatus.DeletePending, (await world.ReplicaAsync(first.ReplicaId)).Status);
            Assert.Equal(BackupReplicaStatus.DeletePending, (await world.ReplicaAsync(second.ReplicaId)).Status);
            Assert.Equal(BackupReplicaStatus.Available, (await world.ReplicaAsync(third.ReplicaId)).Status);
            Assert.Equal(new BackupRetentionApplication(1, 1), await store.ApplyExpiredRetentionAsync(late, 2, default));
            Assert.Equal(BackupReplicaStatus.DeletePending, (await world.ReplicaAsync(third.ReplicaId)).Status);
            Assert.Equal(new BackupRetentionApplication(0, 0), await store.ApplyExpiredRetentionAsync(late, 2, default));
        });
    }

    // ---- claiming and finishing the physical delete -----------------------------------------------------------------------------

    [Fact]
    public async Task ADeleteCannotBeClaimedBeforeItsGracePeriodEndsAndIsClaimedExactlyAtItWithThePersistedLocation()
    {
        var backup = await SeedAsync();
        var store = _world.Store();
        var requestedAt = RetentionWorld.Accepted.AddDays(1);
        var due = await _world.RequestDeleteAsync(store, backup, requestedAt);
        Assert.Equal(requestedAt + Grace, due);

        Assert.Null(await RetentionWorld.ClaimAsync(store, backup, due - Step));
        Assert.Equal(BackupReplicaStatus.DeletePending, (await _world.ReplicaAsync(backup.ReplicaId)).Status);

        var claim = await RetentionWorld.ClaimAsync(store, backup, due);

        Assert.NotNull(claim);
        Assert.Equal(backup.TenantId, claim.TenantId);
        Assert.Equal(backup.ArtifactId, claim.ArtifactId);
        Assert.Equal(backup.StorageProvider, claim.StorageProvider);
        Assert.Equal(backup.StorageTarget, claim.StorageTarget);
        Assert.Equal(backup.Key, claim.Key);
        Assert.NotEqual(Guid.Empty, claim.LeaseToken);
        Assert.Equal(1, claim.Attempt);
        var replica = await _world.ReplicaAsync(backup.ReplicaId);
        Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
        Assert.Equal(claim.LeaseToken, replica.DeleteLeaseToken);
        Assert.Equal("node-a", replica.DeleteLeaseOwner);
        Assert.Equal(due + Lease, replica.DeleteLeaseExpiresAt);
        Assert.False(replica.IsUsable);
        // Held under a live lease: nobody else is handed it.
        Assert.Null(await RetentionWorld.ClaimAsync(_world.Store(), backup, due + Step, "node-b"));
    }

    [Fact]
    public async Task ClaimsAreBoundedByTheLimitAndHandedOutEarliestDueFirst()
    {
        await BackupStoreHarness.WithIsolatedDatabaseAsync(async harness =>
        {
            var world = new RetentionWorld(harness.Database);
            var store = world.Store();
            var policy = RetentionKit.Policy(keepFor: Keep, grace: Grace);
            var requestedAt = RetentionWorld.Accepted.AddDays(1);
            var late = await world.SeedAsync(policy);
            var early = await world.SeedAsync(policy);
            var middle = await world.SeedAsync(policy);
            await world.RequestDeleteAsync(store, early, requestedAt);
            await world.RequestDeleteAsync(store, middle, requestedAt.AddHours(1));
            await world.RequestDeleteAsync(store, late, requestedAt.AddHours(2));
            var allDue = requestedAt.AddHours(2) + Grace;

            var firstBatch = await store.ClaimDueDeletionsAsync("node-a", allDue, Lease, 2, default);

            Assert.Equal(new[] { early.ReplicaId, middle.ReplicaId }, firstBatch.Select(claim => claim.ReplicaId).ToArray());
            var secondBatch = await store.ClaimDueDeletionsAsync("node-a", allDue, Lease, 2, default);
            Assert.Equal(new[] { late.ReplicaId }, secondBatch.Select(claim => claim.ReplicaId).ToArray());
            Assert.Empty(await store.ClaimDueDeletionsAsync("node-a", allDue, Lease, 2, default));
        });
    }

    [Fact]
    public async Task CompletingTheDeleteMarksTheReplicaDeletedAndNeverDropsTheHistory()
    {
        var backup = await SeedAsync();
        var store = _world.Store();
        var due = await _world.RequestDeleteAsync(store, backup, RetentionWorld.Accepted.AddDays(1));
        var claim = await RetentionWorld.ClaimAsync(store, backup, due);
        Assert.NotNull(claim);
        var doneAt = due + TimeSpan.FromMinutes(1);

        Assert.True(await store.CompleteDeletionAsync(claim.ReplicaId, claim.LeaseToken, doneAt, default));

        var replica = await _world.ReplicaAsync(backup.ReplicaId);
        Assert.Equal(BackupReplicaStatus.Deleted, replica.Status);
        Assert.Equal(doneAt, replica.DeletedAt);
        Assert.Null(replica.DeleteLeaseToken);
        Assert.False(replica.IsUsable);
        // The audit trail stays: run, attempt, artifact and the replica with its location and checksum.
        await using (var context = fixture.CreatePlatformContext())
        {
            Assert.True(await context.BackupRuns.AnyAsync(run => run.Id == backup.RunId));
            Assert.True(await context.BackupRunAttempts.AnyAsync(attempt => attempt.Id == backup.AttemptId));
        }
        var record = await store.GetArtifactAsync(backup.TenantId, backup.ArtifactId, default);
        Assert.NotNull(record);
        Assert.Equal(BackupArtifactState.Deleted, record.Artifact.State);
        Assert.Equal(backup.RunId, record.RunId);
        Assert.Equal(backup.JobId, record.JobId);
        var kept = Assert.Single(record.Artifact.Replicas);
        Assert.Equal(backup.Key, kept.Key);
        Assert.Equal(backup.StorageTarget, kept.StorageTarget);
        Assert.Equal(BackupStoreHarness.ArtifactHash, kept.Checksum);
        // A second completion (or a retried callback) is refused rather than rewriting the record.
        Assert.False(await store.CompleteDeletionAsync(claim.ReplicaId, claim.LeaseToken, doneAt.AddMinutes(1), default));
        Assert.Equal(doneAt, (await _world.ReplicaAsync(backup.ReplicaId)).DeletedAt);
        Assert.Null(await RetentionWorld.ClaimAsync(store, backup, doneAt.AddYears(1)));
    }

    [Fact]
    public async Task AFailedDeleteRecordsItsCodeAndRetryTimeAndIsClaimableAgainExactlyThenWithAnIncrementedAttempt()
    {
        var backup = await SeedAsync();
        var store = _world.Store();
        var due = await _world.RequestDeleteAsync(store, backup, RetentionWorld.Accepted.AddDays(1));
        var first = await RetentionWorld.ClaimAsync(store, backup, due);
        Assert.NotNull(first);
        var failedAt = due + TimeSpan.FromMinutes(1);
        var retryAt = failedAt + TimeSpan.FromMinutes(10);

        // Only the holder of the lease may fail it.
        Assert.False(await store.FailDeletionAsync(first.ReplicaId, Guid.NewGuid(), failedAt, BackupDeleteFailureCodes.IoError, retryAt, default));
        Assert.Equal(BackupReplicaStatus.Deleting, (await _world.ReplicaAsync(backup.ReplicaId)).Status);
        Assert.True(await store.FailDeletionAsync(first.ReplicaId, first.LeaseToken, failedAt, BackupDeleteFailureCodes.StorageUnavailable, retryAt, default));

        var failed = await _world.ReplicaAsync(backup.ReplicaId);
        Assert.Equal(BackupReplicaStatus.DeleteFailed, failed.Status);
        Assert.Equal(BackupDeleteFailureCodes.StorageUnavailable, failed.DeleteFailureCode);
        Assert.Equal(retryAt, failed.DeleteNextAttemptAt);
        Assert.Null(failed.DeleteLeaseToken);
        Assert.False(await store.CompleteDeletionAsync(first.ReplicaId, first.LeaseToken, failedAt, default));
        Assert.Null(await RetentionWorld.ClaimAsync(store, backup, retryAt - Step));

        var second = await RetentionWorld.ClaimAsync(store, backup, retryAt);

        Assert.NotNull(second);
        Assert.Equal(2, second.Attempt);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        var retried = await _world.ReplicaAsync(backup.ReplicaId);
        Assert.Equal(BackupReplicaStatus.Deleting, retried.Status);
        Assert.Equal(2, retried.DeleteAttempts);
        Assert.Null(retried.DeleteNextAttemptAt);
    }

    [Fact]
    public async Task ACrashedOwnersDeleteIsTakenOverWhenItsLeaseExpiresAndTheOldHolderCanNoLongerFinishIt()
    {
        var backup = await SeedAsync();
        var nodeA = _world.Store();
        var nodeB = _world.Store();
        var due = await _world.RequestDeleteAsync(nodeA, backup, RetentionWorld.Accepted.AddDays(1));
        var first = await RetentionWorld.ClaimAsync(nodeA, backup, due, "node-a");
        Assert.NotNull(first);
        var leaseEnds = due + Lease;

        Assert.Null(await RetentionWorld.ClaimAsync(nodeB, backup, leaseEnds - Step, "node-b"));
        var second = await RetentionWorld.ClaimAsync(nodeB, backup, leaseEnds, "node-b");

        Assert.NotNull(second);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.Equal(2, second.Attempt);
        Assert.False(await nodeA.CompleteDeletionAsync(first.ReplicaId, first.LeaseToken, leaseEnds + Step, default));
        Assert.False(await nodeA.FailDeletionAsync(first.ReplicaId, first.LeaseToken, leaseEnds + Step, BackupDeleteFailureCodes.IoError, leaseEnds + Lease, default));
        var replica = await _world.ReplicaAsync(backup.ReplicaId);
        Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
        Assert.Equal(second.LeaseToken, replica.DeleteLeaseToken);
        Assert.Equal("node-b", replica.DeleteLeaseOwner);
        Assert.True(await nodeB.CompleteDeletionAsync(second.ReplicaId, second.LeaseToken, leaseEnds + Step, default));
        Assert.Equal(BackupReplicaStatus.Deleted, (await _world.ReplicaAsync(backup.ReplicaId)).Status);
    }

    [Fact]
    public async Task TheHolderMayStillCompleteAfterItsLeaseExpiredWhenNobodyTookTheDeleteOver()
    {
        var backup = await SeedAsync();
        var store = _world.Store();
        var due = await _world.RequestDeleteAsync(store, backup, RetentionWorld.Accepted.AddDays(1));
        var claim = await RetentionWorld.ClaimAsync(store, backup, due);
        Assert.NotNull(claim);

        // A slow delete that finishes after its lease ran out, before anybody claimed it again.
        Assert.True(await store.CompleteDeletionAsync(claim.ReplicaId, claim.LeaseToken, due + Lease + TimeSpan.FromHours(1), default));

        Assert.Equal(BackupReplicaStatus.Deleted, (await _world.ReplicaAsync(backup.ReplicaId)).Status);
    }

    // ---- cancel -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CancellingTheDeleteOfAnExpiredBackupRestoresItPinsItAndALaterScanLeavesItAlone()
    {
        var backup = await SeedAsync();
        var store = _world.Store();
        await store.ApplyExpiredRetentionAsync(Expiry + TimeSpan.FromHours(1), 1000, default);
        var cancelledAt = Expiry + TimeSpan.FromHours(2);

        var result = await store.CancelDeleteAsync(backup.TenantId, backup.ArtifactId, cancelledAt, default);

        Assert.Equal(BackupArtifactCancelKind.Restored, result.Kind);
        Assert.Equal(BackupArtifactState.Available, result.Artifact!.Artifact.State);
        var replica = await _world.ReplicaAsync(backup.ReplicaId);
        Assert.Equal(BackupReplicaStatus.Available, replica.Status);
        Assert.Null(replica.DeleteAfter);
        Assert.Null(replica.DeleteReason);
        Assert.Null(replica.DeleteRequestedAt);
        Assert.Equal(cancelledAt, (await _world.ArtifactAsync(backup.ArtifactId)).RetentionPinnedAt);

        await store.ApplyExpiredRetentionAsync(Expiry + TimeSpan.FromDays(100), 1000, default);

        Assert.Equal(BackupReplicaStatus.Available, (await _world.ReplicaAsync(backup.ReplicaId)).Status);
    }

    [Fact]
    public async Task CancellingBeforeExpiryDoesNotPinSoRetentionStillAppliesOnceTheBackupExpires()
    {
        var backup = await SeedAsync();
        var store = _world.Store();
        await _world.RequestDeleteAsync(store, backup, RetentionWorld.Accepted.AddDays(1));

        var result = await store.CancelDeleteAsync(backup.TenantId, backup.ArtifactId, RetentionWorld.Accepted.AddDays(2), default);

        Assert.Equal(BackupArtifactCancelKind.Restored, result.Kind);
        Assert.Null((await _world.ArtifactAsync(backup.ArtifactId)).RetentionPinnedAt);
        await store.ApplyExpiredRetentionAsync(Expiry, 1000, default);
        var replica = await _world.ReplicaAsync(backup.ReplicaId);
        Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status);
        Assert.Equal(BackupDeleteReason.RetentionExpired, replica.DeleteReason);
    }

    [Fact]
    public async Task CancelIsRefusedOnceThePhysicalDeleteHasBegunFailedOrFinished()
    {
        var backup = await SeedAsync();
        var store = _world.Store();
        var due = await _world.RequestDeleteAsync(store, backup, RetentionWorld.Accepted.AddDays(1));
        var claim = await RetentionWorld.ClaimAsync(store, backup, due);
        Assert.NotNull(claim);

        await AssertNothingToCancelAsync(store, backup, BackupReplicaStatus.Deleting, due);

        Assert.True(await store.FailDeletionAsync(claim.ReplicaId, claim.LeaseToken, due, BackupDeleteFailureCodes.IoError, due + TimeSpan.FromMinutes(1), default));
        await AssertNothingToCancelAsync(store, backup, BackupReplicaStatus.DeleteFailed, due);

        var retry = await RetentionWorld.ClaimAsync(store, backup, due + TimeSpan.FromMinutes(1));
        Assert.NotNull(retry);
        Assert.True(await store.CompleteDeletionAsync(retry.ReplicaId, retry.LeaseToken, due + TimeSpan.FromMinutes(2), default));
        await AssertNothingToCancelAsync(store, backup, BackupReplicaStatus.Deleted, due + TimeSpan.FromMinutes(3));
    }

    private async Task AssertNothingToCancelAsync(PostgreSqlBackupRetentionStore store, SeededBackup backup, BackupReplicaStatus expected, DateTimeOffset now)
    {
        var result = await store.CancelDeleteAsync(backup.TenantId, backup.ArtifactId, now, default);

        Assert.Equal(BackupArtifactCancelKind.NothingToCancel, result.Kind);
        Assert.NotNull(result.Artifact);
        Assert.Equal(expected, (await _world.ReplicaAsync(backup.ReplicaId)).Status);
    }

    [Fact]
    public async Task CancellingABackupWithNothingPendingChangesNothing()
    {
        var backup = await SeedAsync();
        var store = _world.Store();

        var result = await store.CancelDeleteAsync(backup.TenantId, backup.ArtifactId, Expiry + TimeSpan.FromDays(1), default);

        Assert.Equal(BackupArtifactCancelKind.NothingToCancel, result.Kind);
        Assert.Null((await _world.ArtifactAsync(backup.ArtifactId)).RetentionPinnedAt);
    }

    // ---- manual delete ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ManualDeleteIsASoftDeleteCarryingTheRequesterAndNeverPhysical()
    {
        var backup = await SeedAsync();
        var store = _world.Store();
        var user = Guid.NewGuid();
        var requestedAt = RetentionWorld.Accepted.AddDays(1);

        var result = await store.RequestDeleteAsync(backup.TenantId, backup.ArtifactId, user, requestedAt, default);

        Assert.Equal(BackupArtifactDeleteKind.Requested, result.Kind);
        Assert.Equal(BackupArtifactState.DeletePending, result.Artifact!.Artifact.State);
        Assert.Equal(backup.RunId, result.Artifact.RunId);
        var replica = await _world.ReplicaAsync(backup.ReplicaId);
        Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status);
        Assert.Equal(BackupDeleteReason.Manual, replica.DeleteReason);
        Assert.Equal(user, replica.DeleteRequestedByUserId);
        Assert.Equal(requestedAt, replica.DeleteRequestedAt);
        Assert.Equal(requestedAt + Grace, replica.DeleteAfter);
        Assert.Equal(0, replica.DeleteAttempts);
        Assert.Null(replica.DeleteLeaseToken);
        Assert.True(replica.IsUsable);
        // An administrator deleting early does not count as retention having acted.
        Assert.Null((await _world.ArtifactAsync(backup.ArtifactId)).RetentionAppliedAt);
    }

    [Fact]
    public async Task ManualDeleteTwiceIsAlreadyInProgressAndAfterTheDeleteFinishedAlreadyDeleted()
    {
        var backup = await SeedAsync();
        var store = _world.Store();
        var requestedAt = RetentionWorld.Accepted.AddDays(1);
        var due = await _world.RequestDeleteAsync(store, backup, requestedAt, Guid.NewGuid());
        var original = await _world.ReplicaAsync(backup.ReplicaId);

        var second = await store.RequestDeleteAsync(backup.TenantId, backup.ArtifactId, Guid.NewGuid(), requestedAt.AddHours(1), default);

        Assert.Equal(BackupArtifactDeleteKind.AlreadyInProgress, second.Kind);
        var unchanged = await _world.ReplicaAsync(backup.ReplicaId);
        Assert.Equal(original.DeleteRequestedByUserId, unchanged.DeleteRequestedByUserId);
        Assert.Equal(original.DeleteAfter, unchanged.DeleteAfter);
        var claim = await RetentionWorld.ClaimAsync(store, backup, due);
        Assert.NotNull(claim);
        Assert.Equal(BackupArtifactDeleteKind.AlreadyInProgress, (await store.RequestDeleteAsync(backup.TenantId, backup.ArtifactId, null, due, default)).Kind);
        Assert.True(await store.CompleteDeletionAsync(claim.ReplicaId, claim.LeaseToken, due + Step, default));

        var after = await store.RequestDeleteAsync(backup.TenantId, backup.ArtifactId, Guid.NewGuid(), due + TimeSpan.FromMinutes(1), default);

        Assert.Equal(BackupArtifactDeleteKind.AlreadyDeleted, after.Kind);
        Assert.Equal(BackupArtifactState.Deleted, after.Artifact!.Artifact.State);
    }

    [Fact]
    public async Task AScanAfterAManualDeleteDoesNotRewriteWhyOrWhenTheReplicaWillBeDeleted()
    {
        var backup = await SeedAsync();
        var store = _world.Store();
        var user = Guid.NewGuid();
        await _world.RequestDeleteAsync(store, backup, RetentionWorld.Accepted.AddDays(1), user);
        var before = await _world.ReplicaAsync(backup.ReplicaId);

        await store.ApplyExpiredRetentionAsync(Expiry, 1000, default);

        var after = await _world.ReplicaAsync(backup.ReplicaId);
        Assert.Equal(BackupDeleteReason.Manual, after.DeleteReason);
        Assert.Equal(user, after.DeleteRequestedByUserId);
        Assert.Equal(before.DeleteAfter, after.DeleteAfter);
    }

    // ---- tenant isolation -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnotherTenantsArtifactIsNotFoundForGetDeleteAndCancelAndIsLeftUntouched()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync();
        var store = _world.Store();
        var requestedAt = RetentionWorld.Accepted.AddDays(1);
        await _world.RequestDeleteAsync(store, theirs, requestedAt);

        Assert.Null(await store.GetArtifactAsync(mine.TenantId, theirs.ArtifactId, default));
        Assert.Equal(BackupArtifactDeleteKind.NotFound, (await store.RequestDeleteAsync(mine.TenantId, theirs.ArtifactId, Guid.NewGuid(), requestedAt, default)).Kind);
        var cancel = await store.CancelDeleteAsync(mine.TenantId, theirs.ArtifactId, requestedAt.AddHours(1), default);
        Assert.Equal(BackupArtifactCancelKind.NotFound, cancel.Kind);
        Assert.Null(cancel.Artifact);
        Assert.Equal(BackupReplicaStatus.DeletePending, (await _world.ReplicaAsync(theirs.ReplicaId)).Status);
        Assert.Equal(BackupReplicaStatus.Available, (await _world.ReplicaAsync(mine.ReplicaId)).Status);
        Assert.Equal(BackupArtifactDeleteKind.NotFound, (await store.RequestDeleteAsync(mine.TenantId, Guid.NewGuid(), null, requestedAt, default)).Kind);
    }
}
