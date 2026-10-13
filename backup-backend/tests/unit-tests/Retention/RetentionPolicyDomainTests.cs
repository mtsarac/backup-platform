using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using static BackupPlatform.UnitTests.BackupRunTestSupport;

namespace BackupPlatform.UnitTests;

/// <summary>Builds accepted backups through the public domain API only, so every expiration is produced by <c>Seal</c> exactly as in production.</summary>
internal static class RetentionDomain
{
    public static readonly DateTimeOffset Accepted = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly string SourceHash = new('a', 64);
    private static readonly string ArtifactHash = new('b', 64);

    private static BackupConfiguration BackendConfiguration() => Configuration();

    public static BackupRun NewRun(BackupRetentionPolicy? policy, DateTimeOffset createdAt)
    {
        var run = new BackupRun(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), AgentId.New(), BackendConfiguration(), "key", createdAt, retentionPolicy: policy);
        run.QueueFirstAttempt(Guid.NewGuid(), createdAt);
        return run;
    }

    /// <summary>
    /// Drives <paramref name="run"/> to success: the attempt starts a minute before the backend accepts the completion, and the agent reports
    /// <paramref name="agentReportedAt"/> as the time of its artifact (deliberately unrelated to either).
    /// </summary>
    public static BackupArtifact Complete(BackupRun run, DateTimeOffset acceptedAt, DateTimeOffset agentReportedAt, int replicas = 1)
    {
        var token = Begin(run, acceptedAt.AddMinutes(-1));
        var artifact = new BackupArtifact(Guid.NewGuid(), run.TenantId, run.CurrentAttempt.Id, 100, SourceHash, ArtifactHash, "sha256", agentReportedAt, "{}", "{}");
        for (var index = 0; index < replicas; index++)
        {
            artifact.AddReplica(new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, "local", "default", $"backup-key-{index}", 100, ArtifactHash,
                "sha256", "Verified", "ReadBack", agentReportedAt, agentReportedAt, executionLocation: BackupStorageExecutionLocation.Backend));
        }
        run.Complete(token, acceptedAt, new BackupCompletion(BackupStatus.Succeeded, null, artifact));
        return artifact;
    }

    /// <summary>An accepted backup of <paramref name="replicas"/> replicas, accepted at <see cref="Accepted"/> unless stated otherwise.</summary>
    public static BackupArtifact Artifact(BackupRetentionPolicy? policy = null, int replicas = 1, DateTimeOffset? acceptedAt = null)
    {
        var accepted = acceptedAt ?? Accepted;
        return Complete(NewRun(policy, accepted.AddDays(-1)), accepted, accepted.AddDays(-400), replicas);
    }

    public static BackupRetentionPolicy Policy(TimeSpan? keepFor = null, TimeSpan? grace = null, bool enabled = true) =>
        new() { Enabled = enabled, KeepFor = keepFor ?? TimeSpan.FromDays(30), DeleteGracePeriod = grace ?? TimeSpan.FromHours(24) };
}

public sealed class RetentionPolicyDomainTests
{
    public static TheoryData<TimeSpan> AcceptedKeepFor => new()
    {
        BackupRetentionPolicy.MinimumKeepForLimit,
        TimeSpan.FromDays(30),
        BackupRetentionPolicy.MaximumKeepForLimit
    };

    public static TheoryData<TimeSpan> RejectedKeepFor => new()
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(-1),
        BackupRetentionPolicy.MinimumKeepForLimit - TimeSpan.FromTicks(1),
        BackupRetentionPolicy.MaximumKeepForLimit + TimeSpan.FromTicks(1)
    };

    public static TheoryData<TimeSpan> AcceptedGrace => new()
    {
        BackupRetentionPolicy.MinimumDeleteGraceLimit,
        TimeSpan.FromHours(24),
        BackupRetentionPolicy.MaximumDeleteGraceLimit
    };

    public static TheoryData<TimeSpan> RejectedGrace => new()
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(-1),
        BackupRetentionPolicy.MinimumDeleteGraceLimit - TimeSpan.FromTicks(1),
        BackupRetentionPolicy.MaximumDeleteGraceLimit + TimeSpan.FromTicks(1)
    };

    [Theory]
    [MemberData(nameof(AcceptedKeepFor))]
    public void KeepForOnTheLimitsIsAccepted(TimeSpan keepFor) =>
        Assert.Equal(keepFor, new BackupRetentionPolicy { KeepFor = keepFor }.Validate().KeepFor);

    [Theory]
    [MemberData(nameof(RejectedKeepFor))]
    public void KeepForBeyondTheLimitsIsRejected(TimeSpan keepFor) =>
        Assert.Throws<ArgumentException>(() => new BackupRetentionPolicy { KeepFor = keepFor }.Validate());

    [Theory]
    [MemberData(nameof(AcceptedGrace))]
    public void GracePeriodOnTheLimitsIsAccepted(TimeSpan grace) =>
        Assert.Equal(grace, new BackupRetentionPolicy { DeleteGracePeriod = grace }.Validate().DeleteGracePeriod);

    [Theory]
    [MemberData(nameof(RejectedGrace))]
    public void GracePeriodBeyondTheLimitsIsRejected(TimeSpan grace) =>
        Assert.Throws<ArgumentException>(() => new BackupRetentionPolicy { DeleteGracePeriod = grace }.Validate());

    [Fact]
    public void ADisabledPolicyComputesNoExpiration()
    {
        // Disabled means "keep forever": nothing is computed from KeepFor, so the expiration is simply absent.
        var policy = RetentionDomain.Policy(enabled: false);
        Assert.Null(policy.ExpiresAt(RetentionDomain.Accepted));
    }

    [Fact]
    public void ExpirationIsTheBackendAcceptanceTimePlusKeepForNotTheRunCreationTheAttemptStartOrTheAgentReportedTime()
    {
        var keepFor = TimeSpan.FromDays(7);
        var runCreated = RetentionDomain.Accepted.AddDays(-3);
        var agentReported = RetentionDomain.Accepted.AddDays(-400);
        var run = RetentionDomain.NewRun(RetentionDomain.Policy(keepFor, TimeSpan.FromHours(6)), runCreated);

        var artifact = RetentionDomain.Complete(run, RetentionDomain.Accepted, agentReported);

        var attemptStartedAt = run.CurrentAttempt.StartedAt!.Value;
        Assert.Equal(RetentionDomain.Accepted, artifact.AcceptedAt);
        Assert.Equal(RetentionDomain.Accepted + keepFor, artifact.RetentionExpiresAt);
        Assert.NotEqual(runCreated + keepFor, artifact.RetentionExpiresAt);
        Assert.NotEqual(attemptStartedAt + keepFor, artifact.RetentionExpiresAt);
        Assert.NotEqual(agentReported + keepFor, artifact.RetentionExpiresAt);
        Assert.Equal(TimeSpan.FromHours(6), artifact.RetentionGracePeriod);
    }

    [Fact]
    public void AcceptanceTimeIsKeptAtTheMicrosecondPrecisionTheDatabaseStores()
    {
        var accepted = RetentionDomain.Accepted.AddTicks(7);
        var artifact = RetentionDomain.Artifact(acceptedAt: accepted);

        Assert.Equal(RetentionDomain.Accepted, artifact.AcceptedAt);
        Assert.Equal(RetentionDomain.Accepted + TimeSpan.FromDays(30), artifact.RetentionExpiresAt);
    }

    [Fact]
    public void DisabledRetentionNeverExpiresAnArtifact()
    {
        var artifact = RetentionDomain.Artifact(RetentionDomain.Policy(enabled: false), replicas: 2);

        Assert.Null(artifact.RetentionExpiresAt);
        Assert.NotNull(artifact.AcceptedAt);
        Assert.False(artifact.HasExpired(DateTimeOffset.MaxValue));
        Assert.Equal(0, artifact.ApplyRetention(DateTimeOffset.MaxValue));
        Assert.Null(artifact.RetentionAppliedAt);
        Assert.All(artifact.Replicas, replica => Assert.Equal(BackupReplicaStatus.Available, replica.Status));
    }

    [Fact]
    public void ExpirationBoundaryIsInclusiveToTheTick()
    {
        var artifact = RetentionDomain.Artifact();
        var expires = artifact.RetentionExpiresAt!.Value;

        Assert.False(artifact.HasExpired(expires - TimeSpan.FromTicks(1)));
        Assert.True(artifact.HasExpired(expires));
        Assert.True(artifact.HasExpired(expires + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void ChangingTheJobPolicyNeverRewritesAnExistingRunSnapshotOrArtifactButANewRunSnapshotsTheNewPolicy()
    {
        var original = RetentionDomain.Policy(TimeSpan.FromDays(30), TimeSpan.FromHours(24));
        var job = new BackupJob(Guid.NewGuid(), TenantId.New(), "Database", AgentId.New(),
            Configuration(), Now, retentionPolicy: original);
        var firstRun = new BackupRun(Guid.NewGuid(), job.TenantId, job.Id, job.AgentId, job.Configuration, "first", Now, retentionPolicy: job.RetentionPolicy);
        firstRun.QueueFirstAttempt(Guid.NewGuid(), Now);
        var firstArtifact = RetentionDomain.Complete(firstRun, RetentionDomain.Accepted, Now);
        var expiresBeforeEdit = firstArtifact.RetentionExpiresAt;

        job.ChangeRetention(RetentionDomain.Policy(TimeSpan.FromDays(1), TimeSpan.FromMinutes(5)));
        var secondRun = new BackupRun(Guid.NewGuid(), job.TenantId, job.Id, job.AgentId, job.Configuration, "second", Now, retentionPolicy: job.RetentionPolicy);
        secondRun.QueueFirstAttempt(Guid.NewGuid(), Now);
        var secondArtifact = RetentionDomain.Complete(secondRun, RetentionDomain.Accepted, Now);

        Assert.Equal(original, firstRun.RetentionPolicy);
        Assert.Equal(expiresBeforeEdit, firstArtifact.RetentionExpiresAt);
        Assert.Equal(TimeSpan.FromHours(24), firstArtifact.RetentionGracePeriod);
        Assert.Equal(TimeSpan.FromDays(1), secondRun.RetentionPolicy.KeepFor);
        Assert.Equal(RetentionDomain.Accepted + TimeSpan.FromDays(1), secondArtifact.RetentionExpiresAt);
        Assert.Equal(TimeSpan.FromMinutes(5), secondArtifact.RetentionGracePeriod);
    }

    [Fact]
    public void AnInvalidPolicyEditIsRefusedAndLeavesTheJobPolicyUntouched()
    {
        var original = RetentionDomain.Policy(TimeSpan.FromDays(30));
        var job = new BackupJob(Guid.NewGuid(), TenantId.New(), "Database", AgentId.New(),
            Configuration(), Now, retentionPolicy: original);

        Assert.Throws<ArgumentException>(() => job.ChangeRetention(new BackupRetentionPolicy { KeepFor = TimeSpan.Zero }));

        Assert.Equal(original, job.RetentionPolicy);
    }
}
