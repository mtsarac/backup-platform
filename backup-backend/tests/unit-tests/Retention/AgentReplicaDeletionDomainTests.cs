using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests;

public sealed class AgentReplicaDeletionDomainTests
{
    private static readonly DateTimeOffset Now = RetentionDomain.Accepted.AddDays(1);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(1);
    private static readonly string Hash = new('b', 64);

    private static BackupArtifact Pending()
    {
        var configuration = BackupRunTestSupport.Configuration() with { Destinations = [new BackupDestination("local", null, true) { StorageExecutionLocation = BackupStorageExecutionLocation.Agent }] };
        var run = new BackupRun(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), AgentId.New(), configuration, "agent-delete", Now.AddDays(-2),
            retentionPolicy: RetentionDomain.Policy(grace: TimeSpan.FromSeconds(1)));
        run.QueueFirstAttempt(Guid.NewGuid(), Now.AddDays(-2));
        var token = BackupRunTestSupport.Begin(run, RetentionDomain.Accepted.AddMinutes(-1));
        var artifact = new BackupArtifact(Guid.NewGuid(), run.TenantId, run.CurrentAttempt.Id, 100, Hash, Hash, "sha256", RetentionDomain.Accepted, "{}", "{}");
        artifact.AddReplica(new BackupReplica(Guid.NewGuid(), run.TenantId, artifact.Id, "local", "local", "managed/backup.bak", 100, Hash, "sha256",
            "Verified", "ReadBack", RetentionDomain.Accepted, RetentionDomain.Accepted, owningAgentId: run.AgentId.Value));
        run.Complete(token, RetentionDomain.Accepted, new BackupCompletion(BackupStatus.Succeeded, null, artifact));
        Assert.Equal(BackupArtifactDeleteOutcome.Requested, artifact.RequestDelete(Now.AddSeconds(-1), Guid.NewGuid()));
        return artifact;
    }

    private static BackupReplica Claimed(out Guid operation, out Guid token)
    {
        var replica = Assert.Single(Pending().Replicas);
        operation = Guid.NewGuid();
        token = Guid.NewGuid();
        Assert.True(replica.DispatchAgentDeletion(operation, Now, Now.AddMinutes(1)));
        Assert.True(replica.ClaimAgentDeletion(operation, token, Now, Lease));
        return replica;
    }

    [Fact]
    public void DispatchIsIrreversibleVisibleAndCannotBeClaimedByABackendWorker()
    {
        var artifact = Pending();
        var replica = Assert.Single(artifact.Replicas);
        var operation = Guid.NewGuid();
        Assert.Equal(BackupStorageExecutionLocation.Agent, replica.ExecutionLocation);
        Assert.NotNull(replica.OwningAgentId);
        Assert.False(replica.BeginDelete(Guid.NewGuid(), "backend", Now, Lease));
        Assert.True(replica.DispatchAgentDeletion(operation, Now, Now.AddMinutes(1)));
        Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
        Assert.True(replica.WaitingForAgent);
        Assert.False(replica.IsUsable);
        Assert.Equal(operation, replica.DeleteOperationId);
        Assert.Null(replica.DeleteLeaseToken);
        Assert.Equal("agent_unavailable", replica.DeleteFailureCode);
        Assert.Equal(Now.AddMinutes(1), replica.DeleteNextAttemptAt);
        Assert.Equal(0, artifact.CancelDelete(Now));
        Assert.False(replica.BeginDelete(Guid.NewGuid(), "backend", Now.AddDays(1), Lease));
        Assert.False(replica.DispatchAgentDeletion(Guid.NewGuid(), Now, Now.AddMinutes(1)));
    }

    [Fact]
    public void PollCanClaimBeforeTheOfflineRecheckAndOnlyOneHolderWins()
    {
        var replica = Assert.Single(Pending().Replicas);
        var operation = Guid.NewGuid();
        var token = Guid.NewGuid();
        Assert.True(replica.DispatchAgentDeletion(operation, Now, Now.AddHours(1)));
        var claimedOperation = Guid.NewGuid();
        Assert.True(replica.ClaimAgentDeletion(claimedOperation, token, Now, Lease));
        Assert.Equal(operation, replica.DeleteOperationId);
        Assert.False(replica.WaitingForAgent);
        Assert.Equal(token, replica.DeleteLeaseToken);
        Assert.Equal(Now + Lease, replica.DeleteLeaseExpiresAt);
        Assert.Null(replica.DeleteNextAttemptAt);
        Assert.Null(replica.DeleteFailureCode);
        Assert.Equal(1, replica.DeleteAttempts);
        Assert.False(replica.ClaimAgentDeletion(operation, Guid.NewGuid(), Now, Lease));
    }

    [Fact]
    public void RenewalFencesOperationAndTokenAndRefusesTheExactExpiry()
    {
        var replica = Claimed(out var operation, out var token);
        Assert.False(replica.RenewAgentDeletion(Guid.NewGuid(), token, Now.AddMinutes(1), Lease));
        Assert.False(replica.RenewAgentDeletion(operation, Guid.NewGuid(), Now.AddMinutes(1), Lease));
        Assert.Equal(Now + Lease, replica.DeleteLeaseExpiresAt);
        Assert.True(replica.RenewAgentDeletion(operation, token, Now.AddMinutes(1), Lease));
        Assert.Equal(Now.AddMinutes(1) + Lease, replica.DeleteLeaseExpiresAt);
        Assert.False(replica.RenewAgentDeletion(operation, token, replica.DeleteLeaseExpiresAt!.Value, Lease));
    }

    [Fact]
    public void ExpiredClaimKeepsTheOperationAndFreshTokenFencesTheCrashedHolder()
    {
        var replica = Claimed(out var oldOperation, out var oldToken);
        var expiry = replica.DeleteLeaseExpiresAt!.Value;
        Assert.False(replica.DispatchAgentDeletion(Guid.NewGuid(), expiry - Step, expiry.AddMinutes(1)));
        Assert.False(replica.CompleteAgentDeletion(oldOperation, oldToken, expiry, true, null, null));
        var operation = Guid.NewGuid();
        var token = Guid.NewGuid();
        Assert.True(replica.DispatchAgentDeletion(operation, expiry, expiry.AddMinutes(1)));
        Assert.True(replica.ClaimAgentDeletion(operation, token, expiry, Lease));
        Assert.Equal(oldOperation, replica.DeleteOperationId);
        Assert.NotEqual(oldToken, replica.DeleteLeaseToken);
        Assert.False(replica.RenewAgentDeletion(oldOperation, oldToken, expiry, Lease));
        Assert.False(replica.CompleteAgentDeletion(oldOperation, oldToken, expiry, true, null, null));
        Assert.True(replica.CompleteAgentDeletion(oldOperation, token, expiry, true, null, null));
        Assert.Equal(BackupReplicaStatus.Deleted, replica.Status);
        Assert.Equal(2, replica.DeleteAttempts);
    }

    [Fact]
    public void VerifiedSuccessKeepsAnExactReceiptButChangedOutcomesAreNotDuplicates()
    {
        var replica = Claimed(out var operation, out var token);
        Assert.True(replica.CompleteAgentDeletion(operation, token, Now.AddSeconds(1), true, null, null));
        Assert.Equal(BackupReplicaStatus.Deleted, replica.Status);
        Assert.Equal(Now.AddSeconds(1), replica.DeletedAt);
        Assert.Null(replica.DeleteLeaseToken);
        Assert.Equal(token, replica.DeleteCompletionToken);
        Assert.True(replica.SameAgentCompletion(operation, token, true, null));
        Assert.False(replica.SameAgentCompletion(operation, token, false, BackupDeleteFailureCodes.IoError));
        Assert.False(replica.SameAgentCompletion(operation, Guid.NewGuid(), true, null));
        Assert.False(replica.SameAgentCompletion(Guid.NewGuid(), token, true, null));
        Assert.False(replica.IsDeletionDue(Now.AddYears(1)));
    }

    [Theory]
    [InlineData(BackupDeleteFailureCodes.InvalidLocation)]
    [InlineData(BackupDeleteFailureCodes.AccessDenied)]
    public void TerminalFailureHasNoAutomaticRetryAndOnlyTheExactFailureMatchesTheReceipt(string failureCode)
    {
        var replica = Claimed(out var operation, out var token);
        Assert.True(replica.CompleteAgentDeletion(operation, token, Now, false, failureCode, null));
        Assert.Equal(BackupReplicaStatus.DeleteFailed, replica.Status);
        Assert.Equal(failureCode, replica.DeleteFailureCode);
        Assert.Null(replica.DeleteNextAttemptAt);
        Assert.False(replica.WaitingForAgent);
        Assert.False(replica.IsDeletionDue(Now.AddYears(1)));
        Assert.False(replica.DispatchAgentDeletion(Guid.NewGuid(), Now.AddYears(1), Now.AddYears(1).AddMinutes(1)));
        Assert.True(replica.SameAgentCompletion(operation, token, false, failureCode));
        Assert.False(replica.SameAgentCompletion(operation, token, false, BackupDeleteFailureCodes.IoError));
        Assert.False(replica.SameAgentCompletion(operation, token, true, null));
    }

    [Fact]
    public void RetryableFailureCannotRedispatchBeforeItsDelayAndKeepsTheOperationWhenDue()
    {
        var replica = Claimed(out var operation, out var token);
        var retryAt = Now.AddMinutes(2);
        Assert.True(replica.CompleteAgentDeletion(operation, token, Now, false, BackupDeleteFailureCodes.IoError, retryAt));
        Assert.False(replica.IsDeletionDue(retryAt - Step));
        Assert.False(replica.DispatchAgentDeletion(Guid.NewGuid(), retryAt - Step, retryAt.AddMinutes(1)));
        Assert.True(replica.IsDeletionDue(retryAt));
        var nextOperation = Guid.NewGuid();
        Assert.True(replica.DispatchAgentDeletion(nextOperation, retryAt, retryAt.AddMinutes(1)));
        Assert.Equal(operation, replica.DeleteOperationId);
        Assert.False(replica.SameAgentCompletion(operation, token, false, BackupDeleteFailureCodes.IoError));
    }
}
