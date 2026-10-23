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

    [Theory]
    [InlineData(false, RestoreSourceAccess.CentralStorage)]
    [InlineData(true, RestoreSourceAccess.AgentLocal)]
    public void AnAvailableVerifiedReplicaIsReadFromWhereItLives(bool onTheTargetAgentsDisk, RestoreSourceAccess expected)
    {
        var (artifact, replica) = onTheTargetAgentsDisk ? OwnLocal() : Central();
        var decision = RestoreSourceEvaluator.Evaluate(artifact, replica, Agent, Now);

        Assert.True(decision.Eligible);
        Assert.Null(decision.Rejection);
        Assert.Equal(expected, decision.Access);
    }

    // ---- Evaluate: delete lifecycle ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeletePendingReplicaInsideItsGracePeriodIsStillRestorable(bool ownLocal)
    {
        var (artifact, replica) = ownLocal ? OwnLocal() : Central();
        artifact.RequestDelete(Now, null);
        var deleteAfter = replica.DeleteAfter!.Value;

        Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status);
        Assert.True(RestoreSourceEvaluator.Evaluate(artifact, replica, Agent, Now).Eligible);
        Assert.True(RestoreSourceEvaluator.Evaluate(artifact, replica, Agent, deleteAfter - OneTick).Eligible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeletePendingReplicaAtOrAfterItsDeleteAfterIsNotRestorable(bool ownLocal)
    {
        var (artifact, replica) = ownLocal ? OwnLocal() : Central();
        artifact.RequestDelete(Now, null);
        var deleteAfter = replica.DeleteAfter!.Value;

        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RejectionOf(artifact, replica, deleteAfter));
        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RejectionOf(artifact, replica, deleteAfter + OneTick));
        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RejectionOf(artifact, replica, deleteAfter.AddDays(30)));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheNewestOfEquallyRankedReplicasIsSelected(bool ownLocal)
    {
        var artifact = Artifact();
        BackupReplica Make(string path, int ageDays) => ownLocal ? AgentReplica(artifact, Agent.Value, path, Now.AddDays(-ageDays)) : CentralReplica(artifact, path, Now.AddDays(-ageDays));
        AddTo(artifact, Make("tenant/older.bak", 3));
        var newest = AddTo(artifact, Make("tenant/newest.bak", 1));
        AddTo(artifact, Make("tenant/middle.bak", 2));

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

    [Fact]
    public void AnAvailableVerifiedBackendSftpReplicaWithMatchingConnectionTargetIsReadViaBackendRelayed()
    {
        var artifact = Artifact();
        var connectionId = Guid.NewGuid();
        var replica = new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, "sftp", connectionId.ToString("D"),
            "tenant/sales.bak", artifact.SizeBytes, Hash, "sha256", "Verified", "ReadBack", Now.AddDays(-1), Now.AddDays(-1),
            BackupStorageExecutionLocation.Backend, null, connectionId);
        AddTo(artifact, replica);

        var decision = RestoreSourceEvaluator.Evaluate(artifact, replica, Agent, Now);

        Assert.True(decision.Eligible);
        Assert.Equal(RestoreSourceAccess.BackendRelayed, decision.Access);
        Assert.Null(decision.Rejection);
    }

    [Theory]
    [InlineData("sftp")]
    [InlineData("google-drive")]
    public void AnExternalReplicaIsReadDirectlyByAnAgentThatSupportsDirectStorage(string provider)
    {
        var artifact = Artifact();
        var connectionId = Guid.NewGuid();
        var replica = new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, provider, connectionId.ToString("D"),
            "tenant/sales.bak", artifact.SizeBytes, Hash, "sha256", "Verified", "ReadBack", Now.AddDays(-1), Now.AddDays(-1),
            BackupStorageExecutionLocation.Backend, null, connectionId);
        AddTo(artifact, replica);

        Assert.Equal(RestoreSourceAccess.AgentDirect, RestoreSourceEvaluator.Evaluate(artifact, replica, Agent, Now, directStorage: true).Access);
        Assert.Same(replica, RestoreSourceEvaluator.SelectReplica(artifact, Agent, Now, directStorage: true));
        // An agent without the capability keeps the backend relay: direct access is never assumed.
        Assert.Equal(RestoreSourceAccess.BackendRelayed, RestoreSourceEvaluator.Evaluate(artifact, replica, Agent, Now).Access);
    }

    [Fact]
    public void DirectStorageDoesNotMakeAnUnverifiedExternalReplicaEligible()
    {
        var artifact = Artifact();
        var connectionId = Guid.NewGuid();
        var unverified = new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, "sftp", connectionId.ToString("D"),
            "tenant/sales.bak", artifact.SizeBytes, Hash, "sha256", "Unverified", "ReadBack", Now.AddDays(-1), null,
            BackupStorageExecutionLocation.Backend, null, connectionId);
        AddTo(artifact, unverified);

        Assert.Null(RestoreSourceEvaluator.SelectReplica(artifact, Agent, Now, directStorage: true));
        Assert.Equal(RestoreSourceRejection.ReplicaNotVerified, RestoreSourceEvaluator.Explain(artifact, Agent, Now, directStorage: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void AnSftpReplicaWithInvalidOrMismatchedTargetGuidIsRejected(string target)
    {
        var artifact = Artifact();
        var connectionId = Guid.NewGuid();
        var replica = new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, "sftp",
            string.IsNullOrEmpty(target) ? Guid.NewGuid().ToString("D") : target,
            "tenant/sales.bak", artifact.SizeBytes, Hash, "sha256", "Verified", "ReadBack", Now.AddDays(-1), Now.AddDays(-1),
            BackupStorageExecutionLocation.Backend, null, connectionId);
        AddTo(artifact, replica);

        var decision = RestoreSourceEvaluator.Evaluate(artifact, replica, Agent, Now);

        Assert.False(decision.Eligible);
        Assert.Equal(RestoreSourceRejection.NoAccessibleReplica, decision.Rejection);
    }

    [Fact]
    public void AnSftpReplicaWithoutOwningAgentIsSelectableByAnyAgent()
    {
        var artifact = Artifact();
        var connectionId = Guid.NewGuid();
        var replica = new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, "sftp", connectionId.ToString("D"),
            "tenant/sales.bak", artifact.SizeBytes, Hash, "sha256", "Verified", "ReadBack", Now.AddDays(-1), Now.AddDays(-1),
            BackupStorageExecutionLocation.Backend, null, connectionId);
        AddTo(artifact, replica);

        var anotherAgent = AgentId.New();
        var selected = RestoreSourceEvaluator.SelectReplica(artifact, anotherAgent, Now);

        Assert.Same(replica, selected);
    }

    [Fact]
    public void AnUnverifiedOrDeletingSftpReplicaIsRejected()
    {
        var artifact = Artifact();
        var connectionId = Guid.NewGuid();
        var unverified = new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, "sftp", connectionId.ToString("D"),
            "tenant/sales.bak", artifact.SizeBytes, Hash, "sha256", "Unverified", "ReadBack", Now.AddDays(-1), null,
            BackupStorageExecutionLocation.Backend, null, connectionId);
        AddTo(artifact, unverified);

        Assert.Equal(RestoreSourceRejection.ReplicaNotVerified, RestoreSourceEvaluator.Evaluate(artifact, unverified, Agent, Now).Rejection);

        var restorable = new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, "sftp", connectionId.ToString("D"),
            "tenant/sales2.bak", artifact.SizeBytes, Hash, "sha256", "Verified", "ReadBack", Now.AddDays(-1), Now.AddDays(-1),
            BackupStorageExecutionLocation.Backend, null, connectionId);
        AddTo(artifact, restorable);
        artifact.RequestDelete(Now, null);
        var due = restorable.DeleteAfter!.Value;
        restorable.BeginDelete(Guid.NewGuid(), "node-a", due, DeleteLease);

        Assert.Equal(RestoreSourceRejection.ReplicaNotRestorable, RestoreSourceEvaluator.Evaluate(artifact, restorable, Agent, Now).Rejection);
    }
}
