using BackupPlatform.Domain.Backups;

namespace BackupPlatform.UnitTests;

/// <summary>The replica's physical-delete lifecycle: who may move it, when it is due, and which holder may finish it.</summary>
public sealed class RetentionReplicaDomainTests
{
    private static readonly DateTimeOffset Requested = RetentionDomain.Accepted.AddDays(2);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(6);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    private static BackupReplica Pending() => Single(RetentionDomain.Policy(grace: Grace), artifact =>
    {
        Assert.Equal(BackupArtifactDeleteOutcome.Requested, artifact.RequestDelete(Requested, Guid.NewGuid()));
    });

    private static BackupReplica Single(BackupRetentionPolicy policy, Action<BackupArtifact>? prepare = null)
    {
        var artifact = RetentionDomain.Artifact(policy);
        prepare?.Invoke(artifact);
        return Assert.Single(artifact.Replicas);
    }

    private static BackupReplica Deleting(out Guid token)
    {
        var replica = Pending();
        token = Guid.NewGuid();
        Assert.True(replica.BeginDelete(token, "node-a", replica.DeleteAfter!.Value, Lease));
        return replica;
    }

    [Fact]
    public void AnAvailableReplicaIsNotDueAndCannotBeBegunCompletedOrFailed()
    {
        var replica = Single(RetentionDomain.Policy());
        var token = Guid.NewGuid();
        var far = RetentionDomain.Accepted.AddYears(10);

        Assert.False(replica.IsDeletionDue(far));
        Assert.False(replica.BeginDelete(token, "node-a", far, Lease));
        Assert.False(replica.CompleteDelete(token, far));
        Assert.False(replica.FailDelete(token, far, BackupDeleteFailureCodes.IoError, far.AddMinutes(1)));
        Assert.Equal(BackupReplicaStatus.Available, replica.Status);
        Assert.Equal(0, replica.DeleteAttempts);
        Assert.Null(replica.DeleteLeaseToken);
    }

    [Fact]
    public void TheGracePeriodBoundaryIsExactToTheTick()
    {
        var replica = Pending();
        var due = replica.DeleteAfter!.Value;

        Assert.Equal(Requested + Grace, due);
        Assert.False(replica.IsDeletionDue(due - OneTick));
        Assert.False(replica.BeginDelete(Guid.NewGuid(), "node-a", due - OneTick, Lease));
        Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status);
        Assert.True(replica.IsDeletionDue(due));
        Assert.True(replica.BeginDelete(Guid.NewGuid(), "node-a", due, Lease));
        Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
    }

    [Fact]
    public void ABackupWaitingOutItsGracePeriodIsIntactAndUsableButOnceTheDeleteBeginsItIsNot()
    {
        var replica = Pending();
        Assert.True(replica.IsUsable);

        Assert.True(replica.BeginDelete(Guid.NewGuid(), "node-a", replica.DeleteAfter!.Value, Lease));

        Assert.False(replica.IsUsable);
    }

    [Fact]
    public void BeginningTheDeleteRecordsTheLeaseAndCountsTheAttempt()
    {
        var replica = Pending();
        var now = replica.DeleteAfter!.Value + TimeSpan.FromMinutes(3);
        var token = Guid.NewGuid();

        Assert.True(replica.BeginDelete(token, "node-a", now, Lease));

        Assert.Equal(token, replica.DeleteLeaseToken);
        Assert.Equal("node-a", replica.DeleteLeaseOwner);
        Assert.Equal(now + Lease, replica.DeleteLeaseExpiresAt);
        Assert.Equal(now, replica.LastDeleteAttemptAt);
        Assert.Equal(1, replica.DeleteAttempts);
    }

    [Fact]
    public void ADeleteThatIsAlreadyRunningCannotBeBegunAgainBeforeItsLeaseRunsOut()
    {
        var replica = Deleting(out _);
        var expires = replica.DeleteLeaseExpiresAt!.Value;

        Assert.False(replica.BeginDelete(Guid.NewGuid(), "node-b", expires - OneTick, Lease));

        Assert.Equal("node-a", replica.DeleteLeaseOwner);
        Assert.Equal(1, replica.DeleteAttempts);
    }

    [Fact]
    public void ADeleteWhoseLeaseExpiredIsDueAgainAtTheExactExpiryAndTheTakeoverGetsANewTokenAndAttempt()
    {
        var replica = Deleting(out var firstToken);
        var expires = replica.DeleteLeaseExpiresAt!.Value;
        Assert.False(replica.IsDeletionDue(expires - OneTick));
        Assert.True(replica.IsDeletionDue(expires));
        var secondToken = Guid.NewGuid();

        Assert.True(replica.BeginDelete(secondToken, "node-b", expires, Lease));

        Assert.Equal(secondToken, replica.DeleteLeaseToken);
        Assert.Equal("node-b", replica.DeleteLeaseOwner);
        Assert.Equal(2, replica.DeleteAttempts);
        Assert.False(replica.CompleteDelete(firstToken, expires));
        Assert.False(replica.FailDelete(firstToken, expires, BackupDeleteFailureCodes.IoError, expires.AddMinutes(1)));
        Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
        Assert.True(replica.CompleteDelete(secondToken, expires));
    }

    [Fact]
    public void TheHolderMayStillCompleteAfterItsLeaseRanOutAsLongAsNobodyTookTheDeleteOver()
    {
        var replica = Deleting(out var token);
        var late = replica.DeleteLeaseExpiresAt!.Value.AddHours(1);

        Assert.True(replica.CompleteDelete(token, late));

        Assert.Equal(BackupReplicaStatus.Deleted, replica.Status);
        Assert.Equal(late, replica.DeletedAt);
    }

    [Fact]
    public void CompletingWithAnyOtherTokenIsRefusedAndChangesNothing()
    {
        var replica = Deleting(out var token);
        var now = replica.DeleteLeaseExpiresAt!.Value.AddSeconds(-1);

        Assert.False(replica.CompleteDelete(Guid.NewGuid(), now));
        Assert.False(replica.CompleteDelete(Guid.Empty, now));

        Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
        Assert.Equal(token, replica.DeleteLeaseToken);
        Assert.Null(replica.DeletedAt);
    }

    [Fact]
    public void CompletionClearsTheLeaseIsFinalAndTheReplicaIsNeverDueAgain()
    {
        var replica = Deleting(out var token);
        var now = replica.DeleteLeaseExpiresAt!.Value.AddSeconds(-1);

        Assert.True(replica.CompleteDelete(token, now));

        Assert.Null(replica.DeleteLeaseToken);
        Assert.Null(replica.DeleteLeaseOwner);
        Assert.Null(replica.DeleteLeaseExpiresAt);
        Assert.False(replica.IsUsable);
        Assert.False(replica.CompleteDelete(token, now));
        Assert.False(replica.FailDelete(token, now, BackupDeleteFailureCodes.IoError, now.AddMinutes(1)));
        Assert.False(replica.IsDeletionDue(DateTimeOffset.MaxValue));
        Assert.False(replica.BeginDelete(Guid.NewGuid(), "node-b", DateTimeOffset.MaxValue, Lease));
        Assert.Equal(BackupReplicaStatus.Deleted, replica.Status);
    }

    [Fact]
    public void AFailedDeleteRecordsItsCodeAndRetryTimeAndIsDueExactlyThenWithAnIncrementedAttempt()
    {
        var replica = Deleting(out var token);
        var failedAt = replica.DeleteLeaseExpiresAt!.Value.AddSeconds(-30);
        var retryAt = failedAt.AddMinutes(10);

        Assert.True(replica.FailDelete(token, failedAt, BackupDeleteFailureCodes.StorageUnavailable, retryAt));

        Assert.Equal(BackupReplicaStatus.DeleteFailed, replica.Status);
        Assert.Equal(BackupDeleteFailureCodes.StorageUnavailable, replica.DeleteFailureCode);
        Assert.Equal(retryAt, replica.DeleteNextAttemptAt);
        Assert.Null(replica.DeleteLeaseToken);
        Assert.False(replica.CompleteDelete(token, failedAt));
        Assert.False(replica.IsDeletionDue(retryAt - OneTick));
        Assert.False(replica.BeginDelete(Guid.NewGuid(), "node-b", retryAt - OneTick, Lease));
        Assert.True(replica.IsDeletionDue(retryAt));
        Assert.True(replica.BeginDelete(Guid.NewGuid(), "node-b", retryAt, Lease));
        Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
        Assert.Equal(2, replica.DeleteAttempts);
        Assert.Null(replica.DeleteNextAttemptAt);
    }

    [Fact]
    public void AFailureCannotScheduleItsRetryInThePastOrWithoutACodeAndKeepsTheDeleteRunning()
    {
        var replica = Deleting(out var token);
        var now = replica.DeleteLeaseExpiresAt!.Value.AddSeconds(-30);

        Assert.Throws<ArgumentException>(() => replica.FailDelete(token, now, BackupDeleteFailureCodes.IoError, now - OneTick));
        Assert.ThrowsAny<ArgumentException>(() => replica.FailDelete(token, now, "", now.AddMinutes(1)));

        Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
        Assert.Null(replica.DeleteFailureCode);
        Assert.True(replica.FailDelete(token, now, BackupDeleteFailureCodes.IoError, now));
    }

    [Fact]
    public void ABeginRefusedForAnInvalidLeaseDoesNotLeaveTheReplicaHalfClaimed()
    {
        var replica = Pending();
        var due = replica.DeleteAfter!.Value;

        Assert.Throws<ArgumentException>(() => replica.BeginDelete(Guid.NewGuid(), "node-a", due, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => replica.BeginDelete(Guid.Empty, "node-a", due, Lease));

        Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status);
        Assert.Equal(0, replica.DeleteAttempts);
        Assert.Null(replica.DeleteLeaseToken);
    }
}
