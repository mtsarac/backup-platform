using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using static BackupPlatform.UnitTests.RestoreFixtures;

namespace BackupPlatform.UnitTests;

/// <summary>Which replica may serve a restore on which agent, decided from persisted facts only.</summary>
// Not constructible through the public API, so not tested here: a replica or artifact with a checksum other than a 64-hex SHA-256
// (BackupGuard.Checksum rejects it, hence RestoreSourceRejection.ChecksumUnavailable is unreachable) and an artifact whose BackupType
// is not "Full" (the setter is private and the constructor always yields "Full").
public sealed class RestoreSourceEvaluatorTests
{
    private static readonly AgentId Agent = AgentId.New();
    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(10);
    private static readonly TimeSpan DeleteLease = TimeSpan.FromMinutes(5);

    private static (BackupArtifact Artifact, BackupReplica Replica) Central()
    {
        var artifact = Artifact();
        return (artifact, AddTo(artifact, CentralReplica(artifact)));
    }

    private static (BackupArtifact Artifact, BackupReplica Replica) OwnLocal()
    {
        var artifact = Artifact();
        return (artifact, AddTo(artifact, AgentReplica(artifact, Agent.Value)));
    }

    private static RestoreSourceRejection? RejectionOf(BackupArtifact artifact, BackupReplica replica, DateTimeOffset? at = null) =>
        RestoreSourceEvaluator.Evaluate(artifact, replica, Agent, at ?? Now).Rejection;

    // ---- Evaluate: accepted ----

    [Fact]
    public void AnAvailableVerifiedCentralReplicaIsReadFromCentralStorage()
    {
        var (artifact, replica) = Central();
        var decision = RestoreSourceEvaluator.Evaluate(artifact, replica, Agent, Now);

        Assert.True(decision.Eligible);
        Assert.Null(decision.Rejection);
        Assert.Equal(RestoreSourceAccess.CentralStorage, decision.Access);
    }

    [Fact]
    public void AnAvailableVerifiedReplicaOnTheTargetAgentsOwnDiskIsReadLocally()
    {
        var (artifact, replica) = OwnLocal();
        var decision = RestoreSourceEvaluator.Evaluate(artifact, replica, Agent, Now);

        Assert.True(decision.Eligible);
        Assert.Equal(RestoreSourceAccess.AgentLocal, decision.Access);
        Assert.Null(decision.Rejection);
    }

    [Fact]
    public void DecisionsCarryExactlyOneOfAnAccessAndARejection()
    {
        var accepted = RestoreSourceDecision.Accept(RestoreSourceAccess.AgentLocal);
        Assert.True(accepted.Eligible);
        Assert.Equal(RestoreSourceAccess.AgentLocal, accepted.Access);
        Assert.Null(accepted.Rejection);

        var rejected = RestoreSourceDecision.Reject(RestoreSourceRejection.ReplicaNotVerified);
        Assert.False(rejected.Eligible);
        Assert.Null(rejected.Access);
        Assert.Equal(RestoreSourceRejection.ReplicaNotVerified, rejected.Rejection);
    }

    // ---- Evaluate: delete lifecycle ----

    [Fact]
    public void ADeletePendingReplicaInsideItsGracePeriodIsStillRestorable()
    {
        var (artifact, replica) = Central();
        artifact.RequestDelete(Now, null);
        var deleteAfter = replica.DeleteAfter!.Value;

        Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status);
        Assert.True(RestoreSourceEvaluator.Evaluate(artifact, replica, Agent, Now).Eligible);
        Assert.True(RestoreSourceEvaluator.Evaluate(artifact, replica, Agent, deleteAfter - OneTick).Eligible);
    }

    [Fact]
    public void ADeletePendingReplicaAtOrAfterItsDeleteAfterIsNotRestorable()
    {
        var (artifact, replica) = Central();
        artifact.RequestDelete(Now, null);
        var deleteAfter = replica.DeleteAfter!.Value;

        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RejectionOf(artifact, replica, deleteAfter));
        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RejectionOf(artifact, replica, deleteAfter + OneTick));
        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RejectionOf(artifact, replica, deleteAfter.AddDays(30)));
    }

    [Fact]
    public void ADeletePendingAgentLocalReplicaFollowsTheSameGraceRule()
    {
        var (artifact, replica) = OwnLocal();
        artifact.RequestDelete(Now, null);
        var deleteAfter = replica.DeleteAfter!.Value;

        Assert.Equal(RestoreSourceAccess.AgentLocal, RestoreSourceEvaluator.Evaluate(artifact, replica, Agent, deleteAfter - OneTick).Access);
        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RejectionOf(artifact, replica, deleteAfter));
    }

    [Fact]
    public void ADeletingBackendReplicaIsNotRestorable()
    {
        var (artifact, replica) = Central();
        artifact.RequestDelete(Now, null);
        Assert.True(replica.BeginDelete(Guid.NewGuid(), "node-a", replica.DeleteAfter!.Value, DeleteLease));

        Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RejectionOf(artifact, replica));
    }

    [Fact]
    public void ADeletingAgentReplicaIsNotRestorable()
    {
        var (artifact, replica) = OwnLocal();
        artifact.RequestDelete(Now, null);
        var due = replica.DeleteAfter!.Value;
        Assert.True(replica.DispatchAgentDeletion(Guid.NewGuid(), due, due.AddMinutes(1)));

        Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RejectionOf(artifact, replica));
    }

    [Fact]
    public void ADeleteFailedReplicaIsNotRestorable()
    {
        var (artifact, replica) = Central();
        artifact.RequestDelete(Now, null);
        var due = replica.DeleteAfter!.Value;
        var token = Guid.NewGuid();
        Assert.True(replica.BeginDelete(token, "node-a", due, DeleteLease));
        Assert.True(replica.FailDelete(token, due, BackupDeleteFailureCodes.IoError, due.AddMinutes(10)));

        Assert.Equal(BackupReplicaStatus.DeleteFailed, replica.Status);
        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RejectionOf(artifact, replica));
    }

    [Fact]
    public void ADeletedReplicaIsNotRestorable()
    {
        var (artifact, replica) = Central();
        artifact.RequestDelete(Now, null);
        var due = replica.DeleteAfter!.Value;
        var token = Guid.NewGuid();
        Assert.True(replica.BeginDelete(token, "node-a", due, DeleteLease));
        Assert.True(replica.CompleteDelete(token, due.AddSeconds(1)));

        Assert.Equal(BackupReplicaStatus.Deleted, replica.Status);
        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RejectionOf(artifact, replica));
    }

    // ---- Evaluate: verification, location, ownership ----

    [Theory]
    [InlineData("Unverified")]
    [InlineData("Failed")]
    public void AReplicaThatIsNotVerifiedIsRejected(string verification)
    {
        var artifact = Artifact();
        var central = AddTo(artifact, CentralReplica(artifact, verification: verification));
        var local = AddTo(artifact, AgentReplica(artifact, Agent.Value, verification: verification));

        Assert.Equal(RestoreSourceRejection.ReplicaNotVerified, RejectionOf(artifact, central));
        Assert.Equal(RestoreSourceRejection.ReplicaNotVerified, RejectionOf(artifact, local));
        Assert.Null(RestoreSourceEvaluator.Evaluate(artifact, central, Agent, Now).Access);
    }

    [Fact]
    public void AnAgentLocalReplicaOfAnotherAgentRequiresATransfer()
    {
        var artifact = Artifact();
        var foreign = AddTo(artifact, AgentReplica(artifact, Guid.NewGuid()));

        var decision = RestoreSourceEvaluator.Evaluate(artifact, foreign, Agent, Now);

        Assert.False(decision.Eligible);
        Assert.Equal(RestoreSourceRejection.AgentTransferRequired, decision.Rejection);
        Assert.Null(decision.Access);
    }

    [Fact]
    public void ALocalProviderReplicaThatLivesOnTheBackendIsReachableByNoAgent()
    {
        var artifact = Artifact();
        var replica = AddTo(artifact, BackendLocalReplica(artifact));

        Assert.Equal(BackupStorageExecutionLocation.Backend, replica.ExecutionLocation);
        Assert.Equal(RestoreSourceRejection.NoAccessibleReplica, RejectionOf(artifact, replica));
    }

    [Fact]
    public void AnUnavailableReplicaIsRejectedBeforeAnOwnershipQuestionIsAsked()
    {
        var artifact = Artifact();
        var foreign = AddTo(artifact, AgentReplica(artifact, Guid.NewGuid()));
        artifact.RequestDelete(Now, null);
        var due = foreign.DeleteAfter!.Value;

        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RejectionOf(artifact, foreign, due));
    }

    [Fact]
    public void AnUnverifiedForeignReplicaIsRejectedAsUnverifiedBeforeTheTransferQuestion()
    {
        var artifact = Artifact();
        var foreign = AddTo(artifact, AgentReplica(artifact, Guid.NewGuid(), verification: "Unverified"));
        Assert.Equal(RestoreSourceRejection.ReplicaNotVerified, RejectionOf(artifact, foreign));
    }

    [Fact]
    public void AReplicaOfAnotherArtifactIsNotFoundWhateverElseIsWrongWithIt()
    {
        var artifact = Artifact(tenant: TenantId.New());
        var other = Artifact(artifact.TenantId);
        var stranger = CentralReplica(other, verification: "Unverified");

        Assert.Equal(RestoreSourceRejection.ReplicaNotFound, RejectionOf(artifact, stranger));
    }

    [Fact]
    public void AReplicaOfAnotherTenantIsNotFound()
    {
        var artifact = Artifact();
        var replica = new BackupReplica(Guid.NewGuid(), TenantId.New(), artifact.Id, "central", "default", "tenant/other.bak", 1000, Hash, "sha256", "Verified", "ReadBack",
            Now.AddDays(-1), Now.AddDays(-1));

        Assert.Equal(RestoreSourceRejection.ReplicaNotFound, RejectionOf(artifact, replica));
    }

    [Fact]
    public void EvaluateRefusesMissingArguments()
    {
        var (artifact, replica) = Central();
        Assert.Throws<ArgumentNullException>(() => RestoreSourceEvaluator.Evaluate(null!, replica, Agent, Now));
        Assert.Throws<ArgumentNullException>(() => RestoreSourceEvaluator.Evaluate(artifact, null!, Agent, Now));
        Assert.Throws<ArgumentNullException>(() => RestoreSourceEvaluator.Evaluate(artifact, replica, null!, Now));
    }

    // ---- SelectReplica ----

    [Fact]
    public void AnArtifactWithoutReplicasSelectsNothing() => Assert.Null(RestoreSourceEvaluator.SelectReplica(Artifact(), Agent, Now));

    [Fact]
    public void ASingleEligibleReplicaIsSelected()
    {
        var (artifact, replica) = Central();
        Assert.Same(replica, RestoreSourceEvaluator.SelectReplica(artifact, Agent, Now));
    }

    [Fact]
    public void TheNewestOfTwoEqualReplicasIsSelected()
    {
        var artifact = Artifact();
        AddTo(artifact, CentralReplica(artifact, "tenant/older.bak", Now.AddDays(-3)));
        var newest = AddTo(artifact, CentralReplica(artifact, "tenant/newest.bak", Now.AddDays(-1)));
        AddTo(artifact, CentralReplica(artifact, "tenant/middle.bak", Now.AddDays(-2)));

        Assert.Same(newest, RestoreSourceEvaluator.SelectReplica(artifact, Agent, Now));
    }

    [Fact]
    public void TheNewestOfTwoOwnLocalReplicasIsSelected()
    {
        var artifact = Artifact();
        AddTo(artifact, AgentReplica(artifact, Agent.Value, "tenant/older-local.bak", Now.AddDays(-3)));
        var newest = AddTo(artifact, AgentReplica(artifact, Agent.Value, "tenant/newer-local.bak", Now.AddDays(-1)));

        Assert.Same(newest, RestoreSourceEvaluator.SelectReplica(artifact, Agent, Now));
    }

    [Fact]
    public void TheTargetAgentsOwnCopyIsPreferredOverANewerSharedCentralCopy()
    {
        var artifact = Artifact();
        AddTo(artifact, CentralReplica(artifact, "tenant/central-newer.bak", Now.AddDays(-1)));
        var local = AddTo(artifact, AgentReplica(artifact, Agent.Value, "tenant/local-older.bak", Now.AddDays(-5)));

        var selected = RestoreSourceEvaluator.SelectReplica(artifact, Agent, Now);

        Assert.Same(local, selected);
        Assert.Equal(RestoreSourceAccess.AgentLocal, RestoreSourceEvaluator.Evaluate(artifact, selected!, Agent, Now).Access);
    }

    [Fact]
    public void AnAvailableReplicaIsPreferredOverOneInItsGracePeriod()
    {
        var artifact = Artifact();
        var pending = AddTo(artifact, AgentReplica(artifact, Agent.Value, "tenant/pending-local.bak", Now.AddDays(-1)));
        artifact.RequestDelete(Now, null);
        var available = AddTo(artifact, CentralReplica(artifact, "tenant/available-central.bak", Now.AddDays(-5)));

        Assert.Equal(BackupReplicaStatus.DeletePending, pending.Status);
        Assert.Equal(BackupReplicaStatus.Available, available.Status);
        Assert.Same(available, RestoreSourceEvaluator.SelectReplica(artifact, Agent, Now));
    }

    [Fact]
    public void AReplicaInItsGracePeriodIsTheLastResort()
    {
        var artifact = Artifact();
        var pending = AddTo(artifact, CentralReplica(artifact));
        artifact.RequestDelete(Now, null);

        Assert.Same(pending, RestoreSourceEvaluator.SelectReplica(artifact, Agent, Now));
    }

    [Fact]
    public void AmongReplicasInTheirGracePeriodTheOwnLocalCopyStillComesFirst()
    {
        var artifact = Artifact();
        AddTo(artifact, CentralReplica(artifact, "tenant/central.bak", Now.AddDays(-1)));
        var local = AddTo(artifact, AgentReplica(artifact, Agent.Value, "tenant/local.bak", Now.AddDays(-9)));
        artifact.RequestDelete(Now, null);

        Assert.Same(local, RestoreSourceEvaluator.SelectReplica(artifact, Agent, Now));
    }

    [Fact]
    public void ReplicasThatAreNotEligibleAreNeverSelectedEvenWhenTheyAreTheNewest()
    {
        var artifact = Artifact();
        AddTo(artifact, AgentReplica(artifact, Guid.NewGuid(), "tenant/foreign.bak", Now.AddHours(-1)));
        AddTo(artifact, CentralReplica(artifact, "tenant/unverified.bak", Now.AddHours(-2), "Unverified"));
        AddTo(artifact, BackendLocalReplica(artifact, "tenant/backend-local.bak", Now.AddHours(-3)));
        var usable = AddTo(artifact, CentralReplica(artifact, "tenant/usable.bak", Now.AddDays(-30)));

        Assert.Same(usable, RestoreSourceEvaluator.SelectReplica(artifact, Agent, Now));
    }

    [Fact]
    public void AnotherAgentsLocalCopyIsOnlyUsableByItsOwner()
    {
        var owner = AgentId.New();
        var artifact = Artifact();
        var local = AddTo(artifact, AgentReplica(artifact, owner.Value));

        Assert.Null(RestoreSourceEvaluator.SelectReplica(artifact, Agent, Now));
        Assert.Same(local, RestoreSourceEvaluator.SelectReplica(artifact, owner, Now));
    }

    [Fact]
    public void NothingIsSelectedWhenEveryReplicaIsPastItsGracePeriod()
    {
        var (artifact, replica) = Central();
        artifact.RequestDelete(Now, null);

        Assert.Null(RestoreSourceEvaluator.SelectReplica(artifact, Agent, replica.DeleteAfter!.Value));
    }

    // ---- Explain ----

    [Fact]
    public void WithoutReplicasTheExplanationIsNoAccessibleReplica() =>
        Assert.Equal(RestoreSourceRejection.NoAccessibleReplica, RestoreSourceEvaluator.Explain(Artifact(), Agent, Now));

    [Fact]
    public void AnAgentTransferProblemOutranksEveryOtherRejection()
    {
        var artifact = Artifact();
        AddTo(artifact, CentralReplica(artifact, "tenant/unverified.bak", verification: "Unverified"));
        AddTo(artifact, AgentReplica(artifact, Guid.NewGuid()));

        Assert.Equal(RestoreSourceRejection.AgentTransferRequired, RestoreSourceEvaluator.Explain(artifact, Agent, Now));
    }

    [Fact]
    public void WithoutATransferProblemTheClosestRejectionIsTheSmallestOne()
    {
        var artifact = Artifact();
        AddTo(artifact, CentralReplica(artifact, "tenant/unverified.bak", verification: "Unverified"));
        AddTo(artifact, BackendLocalReplica(artifact));
        Assert.Equal(RestoreSourceRejection.ReplicaNotVerified, RestoreSourceEvaluator.Explain(artifact, Agent, Now));

        artifact.RequestDelete(Now, null);
        var pastGrace = artifact.Replicas[0].DeleteAfter!.Value;
        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RestoreSourceEvaluator.Explain(artifact, Agent, pastGrace));
    }

    [Fact]
    public void ASingleBackendLocalReplicaIsExplainedAsNoAccessibleReplica()
    {
        var artifact = Artifact();
        AddTo(artifact, BackendLocalReplica(artifact));
        Assert.Null(RestoreSourceEvaluator.SelectReplica(artifact, Agent, Now));
        Assert.Equal(RestoreSourceRejection.NoAccessibleReplica, RestoreSourceEvaluator.Explain(artifact, Agent, Now));
    }

    [Fact]
    public void ADeletedReplicaIsExplainedAsNotRestorable()
    {
        var (artifact, replica) = Central();
        artifact.RequestDelete(Now, null);
        var due = replica.DeleteAfter!.Value;
        var token = Guid.NewGuid();
        replica.BeginDelete(token, "node-a", due, DeleteLease);
        replica.CompleteDelete(token, due);

        Assert.Null(RestoreSourceEvaluator.SelectReplica(artifact, Agent, due));
        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RestoreSourceEvaluator.Explain(artifact, Agent, due));
    }
}
