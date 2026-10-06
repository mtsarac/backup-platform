using BackupPlatform.Domain.Backups;

namespace BackupPlatform.UnitTests;

/// <summary>What the artifact does to its replicas: automatic expiry, manual delete, cancel, and the state derived from them.</summary>
public sealed class RetentionArtifactDomainTests
{
    private static readonly TimeSpan Keep = TimeSpan.FromDays(10);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(12);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Expiry = RetentionDomain.Accepted + Keep;

    private static BackupArtifact Artifact(int replicas = 1) => RetentionDomain.Artifact(RetentionDomain.Policy(Keep, Grace), replicas);

    /// <summary>Claims one replica's physical delete once its grace period is over and returns the lease token.</summary>
    private static Guid Claim(BackupReplica replica)
    {
        var token = Guid.NewGuid();
        Assert.True(replica.BeginDelete(token, "node-a", replica.DeleteAfter!.Value, Lease));
        return token;
    }

    // ---- derived state ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASingleReplicaArtifactFollowsItsReplicaThroughTheWholeLifecycle()
    {
        var artifact = Artifact();
        var replica = artifact.Replicas[0];
        Assert.Equal(BackupArtifactState.Available, artifact.State);

        artifact.RequestDelete(Expiry, null);
        Assert.Equal(BackupArtifactState.DeletePending, artifact.State);

        var token = Claim(replica);
        Assert.Equal(BackupArtifactState.DeletePending, artifact.State);

        var failedAt = replica.DeleteAfter!.Value;
        Assert.True(replica.FailDelete(token, failedAt, BackupDeleteFailureCodes.IoError, failedAt.AddMinutes(1)));
        Assert.Equal(BackupArtifactState.DeletePending, artifact.State);

        Assert.True(replica.BeginDelete(token = Guid.NewGuid(), "node-a", replica.DeleteNextAttemptAt!.Value, Lease));
        Assert.True(replica.CompleteDelete(token, replica.DeleteLeaseExpiresAt!.Value));
        Assert.Equal(BackupArtifactState.Deleted, artifact.State);
    }

    [Fact]
    public void AThreeReplicaArtifactIsDeletedOnlyOnceEveryReplicaIsAndStaysDeletePendingWhileOneRemains()
    {
        var artifact = Artifact(replicas: 3);
        artifact.RequestDelete(Expiry, null);
        var now = artifact.Replicas[0].DeleteAfter!.Value;

        foreach (var replica in artifact.Replicas.Take(2))
        {
            Assert.True(replica.CompleteDelete(Claim(replica), now));
            Assert.Equal(BackupArtifactState.DeletePending, artifact.State);
        }
        var last = artifact.Replicas[2];
        var lastToken = Claim(last);
        Assert.Equal(BackupArtifactState.DeletePending, artifact.State);
        Assert.True(last.FailDelete(lastToken, now, BackupDeleteFailureCodes.IoError, now.AddMinutes(1)));
        Assert.Equal(BackupArtifactState.DeletePending, artifact.State);

        Assert.True(last.BeginDelete(lastToken = Guid.NewGuid(), "node-a", now.AddMinutes(1), Lease));
        Assert.True(last.CompleteDelete(lastToken, now.AddMinutes(1)));
        Assert.Equal(BackupArtifactState.Deleted, artifact.State);
    }

    [Fact]
    public void AnArtifactStaysAvailableWhileAnyReplicaIsAvailable()
    {
        var artifact = Artifact(replicas: 3);
        artifact.RequestDelete(Expiry, null);
        var claimed = artifact.Replicas[0];
        Claim(claimed);

        // The administrator cancels: the two replicas still in their grace period come back, the one being deleted does not.
        Assert.Equal(2, artifact.CancelDelete(Expiry));

        Assert.Equal(BackupArtifactState.Available, artifact.State);
        Assert.Equal(BackupReplicaStatus.Deleting, claimed.Status);
    }

    // ---- automatic retention ----------------------------------------------------------------------------------------------------

    [Fact]
    public void RetentionDoesNothingOneTickBeforeExpiryAndActsAtTheExactExpiry()
    {
        var artifact = Artifact(replicas: 2);

        Assert.Equal(0, artifact.ApplyRetention(Expiry - TimeSpan.FromTicks(1)));
        Assert.Null(artifact.RetentionAppliedAt);
        Assert.All(artifact.Replicas, replica => Assert.Equal(BackupReplicaStatus.Available, replica.Status));

        Assert.Equal(2, artifact.ApplyRetention(Expiry));
        Assert.Equal(Expiry, artifact.RetentionAppliedAt);
    }

    [Fact]
    public void RetentionSoftDeletesWithTheArtifactsOwnGraceAndRetentionReasonAndNoRequester()
    {
        var artifact = Artifact(replicas: 3);
        var scanAt = Expiry.AddHours(5);

        artifact.ApplyRetention(scanAt);

        Assert.All(artifact.Replicas, replica =>
        {
            Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status);
            Assert.Equal(BackupDeleteReason.RetentionExpired, replica.DeleteReason);
            Assert.Null(replica.DeleteRequestedByUserId);
            Assert.Equal(scanAt, replica.DeleteRequestedAt);
            // Counted from when retention acted, so a scan that runs late never shortens the time the backup stays recoverable.
            Assert.Equal(scanAt + Grace, replica.DeleteAfter);
            Assert.True(replica.IsUsable);
        });
        Assert.Equal(BackupArtifactState.DeletePending, artifact.State);
    }

    [Fact]
    public void RetentionIsIdempotent()
    {
        var artifact = Artifact(replicas: 2);
        artifact.ApplyRetention(Expiry);
        var applied = artifact.RetentionAppliedAt;
        var deleteAfter = artifact.Replicas.Select(replica => replica.DeleteAfter).ToList();

        Assert.Equal(0, artifact.ApplyRetention(Expiry.AddDays(3)));

        Assert.Equal(applied, artifact.RetentionAppliedAt);
        Assert.Equal(deleteAfter, artifact.Replicas.Select(replica => replica.DeleteAfter));
    }

    [Fact]
    public void RetentionMovesOnlyAvailableReplicasAndLeavesALiveDeleteAlone()
    {
        var artifact = Artifact(replicas: 3);
        // An administrator started a delete earlier; one replica is already being deleted when the others are cancelled back to Available.
        artifact.RequestDelete(Expiry.AddDays(-1), Guid.NewGuid());
        var running = artifact.Replicas[0];
        var token = Guid.NewGuid();
        var cancelledAt = running.DeleteAfter!.Value;
        Assert.True(running.BeginDelete(token, "node-a", cancelledAt, Lease));
        Assert.Equal(2, artifact.CancelDelete(cancelledAt));
        Assert.Null(artifact.RetentionPinnedAt);

        Assert.Equal(2, artifact.ApplyRetention(Expiry));

        Assert.Equal(BackupReplicaStatus.Deleting, running.Status);
        Assert.Equal(token, running.DeleteLeaseToken);
        Assert.Equal(BackupDeleteReason.Manual, running.DeleteReason);
        Assert.All(artifact.Replicas.Skip(1), replica => Assert.Equal(BackupDeleteReason.RetentionExpired, replica.DeleteReason));
    }

    [Fact]
    public void ARetentionScanAfterAManualDeleteDoesNotRewriteWhyOrWhenTheReplicaWasDeleted()
    {
        var artifact = Artifact();
        var user = Guid.NewGuid();
        artifact.RequestDelete(Expiry.AddDays(-2), user);
        var replica = artifact.Replicas[0];
        var deleteAfter = replica.DeleteAfter;

        Assert.Equal(0, artifact.ApplyRetention(Expiry));

        Assert.Equal(BackupDeleteReason.Manual, replica.DeleteReason);
        Assert.Equal(user, replica.DeleteRequestedByUserId);
        Assert.Equal(deleteAfter, replica.DeleteAfter);
    }

    [Fact]
    public void ACancelledDeleteOfAnExpiredBackupPinsItSoRetentionDoesNotDeleteItAgain()
    {
        var artifact = Artifact(replicas: 2);
        artifact.ApplyRetention(Expiry);
        var cancelledAt = Expiry.AddHours(1);

        Assert.Equal(2, artifact.CancelDelete(cancelledAt));

        Assert.Equal(cancelledAt, artifact.RetentionPinnedAt);
        Assert.Equal(0, artifact.ApplyRetention(Expiry.AddDays(30)));
        Assert.All(artifact.Replicas, replica => Assert.Equal(BackupReplicaStatus.Available, replica.Status));
        Assert.Equal(BackupArtifactState.Available, artifact.State);
    }

    // ---- cancel -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void CancellingBeforeExpiryRestoresTheReplicasWithoutPinningAndLeavesNoDeleteBookkeeping()
    {
        var artifact = Artifact(replicas: 2);
        artifact.RequestDelete(Expiry.AddDays(-5), Guid.NewGuid());

        Assert.Equal(2, artifact.CancelDelete(Expiry.AddDays(-4)));

        Assert.Null(artifact.RetentionPinnedAt);
        Assert.All(artifact.Replicas, replica =>
        {
            Assert.Equal(BackupReplicaStatus.Available, replica.Status);
            Assert.Null(replica.DeleteRequestedAt);
            Assert.Null(replica.DeleteReason);
            Assert.Null(replica.DeleteRequestedByUserId);
            Assert.Null(replica.DeleteAfter);
        });
        // Still subject to automatic retention once it expires for real.
        Assert.Equal(2, artifact.ApplyRetention(Expiry));
    }

    [Fact]
    public void CancellingWhenNothingIsPendingDoesNotPinAnExpiredBackup()
    {
        var artifact = Artifact();

        Assert.Equal(0, artifact.CancelDelete(Expiry.AddDays(1)));

        Assert.Null(artifact.RetentionPinnedAt);
        Assert.Equal(1, artifact.ApplyRetention(Expiry.AddDays(1)));
    }

    [Fact]
    public void CancelNeverRestoresAReplicaWhosePhysicalDeleteBeganFinishedOrFailed()
    {
        var artifact = Artifact(replicas: 3);
        artifact.RequestDelete(Expiry.AddDays(-1), null);
        var now = artifact.Replicas[0].DeleteAfter!.Value;
        var deleting = artifact.Replicas[0];
        var deleted = artifact.Replicas[1];
        var failed = artifact.Replicas[2];
        Claim(deleting);
        Assert.True(deleted.CompleteDelete(Claim(deleted), now));
        Assert.True(failed.FailDelete(Claim(failed), now, BackupDeleteFailureCodes.AccessDenied, now.AddMinutes(1)));

        Assert.Equal(0, artifact.CancelDelete(now));

        Assert.Equal(BackupReplicaStatus.Deleting, deleting.Status);
        Assert.Equal(BackupReplicaStatus.Deleted, deleted.Status);
        Assert.Equal(BackupReplicaStatus.DeleteFailed, failed.Status);
        Assert.Equal(BackupArtifactState.DeletePending, artifact.State);
    }

    [Fact]
    public void CancellingTwiceRestoresOnceAndThePinKeepsItsFirstTimestamp()
    {
        var artifact = Artifact();
        artifact.ApplyRetention(Expiry);
        var first = Expiry.AddHours(1);
        Assert.Equal(1, artifact.CancelDelete(first));

        Assert.Equal(0, artifact.CancelDelete(first.AddHours(1)));

        Assert.Equal(first, artifact.RetentionPinnedAt);
    }

    // ---- manual delete ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ManualDeleteSoftDeletesWithTheArtifactsGraceTheRequesterAndNoPhysicalDelete()
    {
        var artifact = Artifact(replicas: 2);
        var user = Guid.NewGuid();
        var now = RetentionDomain.Accepted.AddDays(1);

        Assert.Equal(BackupArtifactDeleteOutcome.Requested, artifact.RequestDelete(now, user));

        Assert.All(artifact.Replicas, replica =>
        {
            Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status);
            Assert.Equal(BackupDeleteReason.Manual, replica.DeleteReason);
            Assert.Equal(user, replica.DeleteRequestedByUserId);
            Assert.Equal(now + Grace, replica.DeleteAfter);
            Assert.Equal(0, replica.DeleteAttempts);
            Assert.Null(replica.DeleteLeaseToken);
        });
        Assert.False(artifact.HasExpired(now));
        Assert.Null(artifact.RetentionAppliedAt);
    }

    [Fact]
    public void ManualDeleteOfABackupWithRetentionDisabledStillWorks()
    {
        var artifact = RetentionDomain.Artifact(RetentionDomain.Policy(enabled: false));

        Assert.Equal(BackupArtifactDeleteOutcome.Requested, artifact.RequestDelete(RetentionDomain.Accepted, Guid.NewGuid()));
    }

    [Fact]
    public void ARepeatedManualDeleteIsAlreadyInProgressAtEveryStageBeforeEverythingIsGoneAndChangesNothing()
    {
        var artifact = Artifact();
        var replica = artifact.Replicas[0];
        var now = RetentionDomain.Accepted.AddDays(1);
        artifact.RequestDelete(now, Guid.NewGuid());
        var requestedBy = replica.DeleteRequestedByUserId;
        var deleteAfter = replica.DeleteAfter;

        Assert.Equal(BackupArtifactDeleteOutcome.AlreadyInProgress, artifact.RequestDelete(now.AddHours(1), Guid.NewGuid()));
        Assert.Equal(requestedBy, replica.DeleteRequestedByUserId);
        Assert.Equal(deleteAfter, replica.DeleteAfter);

        var token = Claim(replica);
        Assert.Equal(BackupArtifactDeleteOutcome.AlreadyInProgress, artifact.RequestDelete(deleteAfter!.Value, null));
        Assert.True(replica.FailDelete(token, deleteAfter.Value, BackupDeleteFailureCodes.IoError, deleteAfter.Value.AddMinutes(1)));
        Assert.Equal(BackupArtifactDeleteOutcome.AlreadyInProgress, artifact.RequestDelete(deleteAfter.Value, null));
    }

    [Fact]
    public void ManualDeleteOfAFullyDeletedBackupReportsAlreadyDeleted()
    {
        var artifact = Artifact(replicas: 2);
        artifact.RequestDelete(RetentionDomain.Accepted.AddDays(1), null);
        var now = artifact.Replicas[0].DeleteAfter!.Value;
        foreach (var replica in artifact.Replicas) Assert.True(replica.CompleteDelete(Claim(replica), now));

        Assert.Equal(BackupArtifactDeleteOutcome.AlreadyDeleted, artifact.RequestDelete(now.AddDays(1), Guid.NewGuid()));
        Assert.Equal(0, artifact.CancelDelete(now.AddDays(1)));
        Assert.Equal(BackupArtifactState.Deleted, artifact.State);
    }

    [Fact]
    public void ManualDeleteWithOneReplicaAlreadyGoneAndAnotherAvailableMovesOnlyTheAvailableOne()
    {
        var artifact = Artifact(replicas: 2);
        artifact.RequestDelete(RetentionDomain.Accepted.AddDays(1), null);
        var gone = artifact.Replicas[0];
        Assert.True(gone.CompleteDelete(Claim(gone), gone.DeleteAfter!.Value));
        // The other replica comes back through a cancel (the gone one stays gone) and is then deleted manually again.
        var goneAt = gone.DeleteAfter!.Value;
        Assert.Equal(1, artifact.CancelDelete(goneAt));
        var back = artifact.Replicas[1];

        Assert.Equal(BackupArtifactDeleteOutcome.Requested, artifact.RequestDelete(goneAt, null));

        Assert.Equal(BackupReplicaStatus.Deleted, gone.Status);
        Assert.Equal(BackupReplicaStatus.DeletePending, back.Status);
    }
}
