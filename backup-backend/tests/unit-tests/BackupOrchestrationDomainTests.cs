using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests;

public sealed class BackupOrchestrationDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly string SourceHash = new('a', 64);
    private static readonly string ArtifactHash = new('b', 64);
    [Fact]
    public void RunSnapshotDoesNotChangeWhenJobConfigurationChanges()
    {
        var job = new BackupJob(Guid.NewGuid(), TenantId.New(), "Database", AgentId.New(), Configuration(), Now);
        var run = new BackupRun(Guid.NewGuid(), job.TenantId, job.Id, job.AgentId, job.Configuration, "manual-1", Now);
        run.CreateAttempt(Guid.NewGuid(), Now);
        job.Reconfigure(job.Configuration with { Database = "OtherDatabase", StoragePrefix = "other" });
        Assert.Equal("Database", run.Configuration.Database);
        Assert.Equal("", run.Configuration.StoragePrefix);
        Assert.Equal("OtherDatabase", job.Configuration.Database);
    }
    [Fact]
    public void LeaseExpiryAndTokensFenceEveryMutation()
    {
        var run = Run();
        var token = Guid.NewGuid();
        run.Claim(token, Now, TimeSpan.FromSeconds(120));
        Assert.Throws<InvalidOperationException>(() => run.Start(Guid.NewGuid(), Now, "1.0", "1.0", "Linux"));
        run.Start(token, Now, "1.0", "1.0", "Linux");
        var expires = Now.AddSeconds(120);
        Assert.Throws<InvalidOperationException>(() => run.Renew(token, expires, TimeSpan.FromSeconds(120)));
        Assert.Throws<InvalidOperationException>(() => run.Progress(token, expires, "Uploading", 50));
        Assert.Throws<InvalidOperationException>(() => run.Complete(token, expires, new BackupCompletion(BackupStatus.Failed, "StorageWriteFailed", null)));
        Assert.True(run.Expire(expires));
        Assert.Equal(BackupStatus.AgentLost, run.Status);
        Assert.False(run.Expire(expires));
        Assert.Single(run.Attempts);
        Assert.Equal("AgentLost", run.TerminalReason);
    }
    [Fact]
    public void RenewalExtendsLeaseWithoutRegressingAndProgressRejectsInvalidPercent()
    {
        var run = Run();
        var token = Guid.NewGuid();
        run.Claim(token, Now, TimeSpan.FromMinutes(2));
        run.Start(token, Now, "1.0", "1.0", "Linux");
        run.Renew(token, Now.AddMinutes(1), TimeSpan.FromMinutes(2));
        run.Renew(token, Now.AddSeconds(30), TimeSpan.FromMinutes(2));
        Assert.Equal(Now.AddMinutes(3), run.CurrentAttempt.LeaseExpiresAt);
        run.Progress(token, Now.AddMinutes(1), "Uploading", 42.5);
        run.Progress(token, Now.AddSeconds(59), "Preparing", 1);
        Assert.Equal("Uploading", run.CurrentAttempt.ProgressStage);
        Assert.Equal(42.5, run.CurrentAttempt.ProgressPercent);
        Assert.Throws<ArgumentException>(() => run.Progress(token, Now.AddMinutes(1), "Uploading", double.NaN));
    }
    [Fact]
    public void CancellationIsImmediateWhenPendingAndCooperativeWhenLeased()
    {
        var pending = Run();
        pending.RequestCancellation(Now);
        Assert.Equal(BackupStatus.Cancelled, pending.Status);
        Assert.Equal(Now, pending.CompletedAt);
        var leased = Run();
        var token = Guid.NewGuid();
        leased.Claim(token, Now, TimeSpan.FromMinutes(2));
        leased.RequestCancellation(Now.AddSeconds(1));
        Assert.Equal(BackupStatus.Leased, leased.Status);
        Assert.Equal(Now.AddSeconds(1), leased.CancellationRequestedAt);
        leased.Complete(token, Now.AddSeconds(2), new BackupCompletion(BackupStatus.Cancelled, "Cancelled", null));
        Assert.Equal(BackupStatus.Cancelled, leased.Status);
    }
    [Fact]
    public void RetryIsExplicitAndPreservesFailedAttemptHistory()
    {
        var run = Run();
        var token = Guid.NewGuid();
        var first = run.CurrentAttempt;
        Assert.Throws<InvalidOperationException>(() => run.CreateAttempt(Guid.NewGuid(), Now));
        run.Claim(token, Now, TimeSpan.FromMinutes(2));
        run.Complete(token, Now, new BackupCompletion(BackupStatus.Failed, "PreCheckFailed", null));
        Assert.Single(run.Attempts);
        var second = run.CreateAttempt(Guid.NewGuid(), Now.AddSeconds(1));
        Assert.Equal(2, second.Number);
        Assert.Equal(BackupStatus.Failed, first.Status);
        Assert.Equal(BackupStatus.Pending, run.Status);
        Assert.Equal(1, first.Number);
    }
    [Fact]
    public void SuccessRequiresVerifiedArtifactAndKeepsDistinctHashesAndImmutableOutcome()
    {
        var run = Run();
        var token = Guid.NewGuid();
        run.Claim(token, Now, TimeSpan.FromMinutes(2));
        run.Start(token, Now, "1.0", "1.0", "Linux");
        var artifact = Artifact(run);
        Assert.Throws<InvalidOperationException>(() => run.Complete(token, Now, new BackupCompletion(BackupStatus.Succeeded, null, artifact)));
        artifact.AddReplica(Replica(artifact));
        var result = new BackupCompletion(BackupStatus.Succeeded, null, artifact);
        run.Complete(token, Now.AddSeconds(1), result);
        run.Complete(token, Now.AddHours(1), result);
        Assert.Equal(Now.AddSeconds(1), run.CompletedAt);
        Assert.Equal(SourceHash, Assert.Single(run.CurrentAttempt.Artifacts).SourceChecksum);
        Assert.Equal(ArtifactHash, artifact.ArtifactChecksum);
        Assert.Equal("Completed", run.CurrentAttempt.ProgressStage);
        Assert.Equal(100, run.CurrentAttempt.ProgressPercent);
        Assert.Equal(Now.AddSeconds(1), run.CurrentAttempt.ProgressAt);
        Assert.Throws<InvalidOperationException>(() => artifact.AddReplica(Replica(artifact, "other-key")));
        Assert.Throws<InvalidOperationException>(() => run.Complete(token, Now, new BackupCompletion(BackupStatus.Failed, "InternalError", null)));
        Assert.Throws<InvalidOperationException>(() => run.Complete(Guid.NewGuid(), Now, result));
        Assert.Throws<InvalidOperationException>(() => run.CreateAttempt(Guid.NewGuid(), Now));
        Assert.Single(run.Facts, fact => fact.Kind == "BackupRunSucceeded");
    }
    [Fact]
    public void ReplicaMustMatchArtifactOwnershipBytesAndChecksum()
    {
        var artifact = Artifact(Run());
        Assert.Throws<ArgumentException>(() => artifact.AddReplica(new BackupReplica(Guid.NewGuid(), TenantId.New(), artifact.Id,
            "local", "default", "key", 100, ArtifactHash, "sha256", "Verified", "ReadBack", Now, Now)));
        Assert.Throws<ArgumentException>(() => artifact.AddReplica(new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id,
            "local", "default", "key", 101, ArtifactHash, "sha256", "Verified", "ReadBack", Now, Now)));
        Assert.Throws<ArgumentException>(() => artifact.AddReplica(new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id,
            "local", "default", "key", 100, SourceHash, "sha256", "Verified", "ReadBack", Now, Now)));
        artifact.AddReplica(Replica(artifact));
        Assert.Throws<ArgumentException>(() => artifact.AddReplica(Replica(artifact)));
        Assert.Throws<ArgumentException>(() => Replica(artifact, "../escape"));
    }
    [Theory]
    [InlineData("source")]
    [InlineData("artifact")]
    [InlineData("algorithm")]
    public void ArtifactRejectsMalformedOrUnsupportedChecksums(string invalidPart)
    {
        var run = Run();
        var source = invalidPart == "source" ? new string('g', 64) : SourceHash;
        var artifact = invalidPart == "artifact" ? ArtifactHash[..63] : ArtifactHash;
        var algorithm = invalidPart == "algorithm" ? "sha512" : "sha256";
        Assert.Throws<ArgumentException>(() => new BackupArtifact(Guid.NewGuid(), run.TenantId, run.CurrentAttempt.Id,
            100, source, artifact, algorithm, Now, "{}", "{}"));
    }
    [Fact]
    public void EmptyArtifactCannotBePublished()
    {
        var run = Run();
        Assert.Throws<ArgumentException>(() => new BackupArtifact(Guid.NewGuid(), run.TenantId, run.CurrentAttempt.Id,
            0, SourceHash, ArtifactHash, "sha256", Now, "{}", "{}"));
    }
    [Theory]
    [InlineData("Verified", false)]
    [InlineData("Unverified", true)]
    [InlineData("Failed", true)]
    public void ReplicaVerificationStateMustAgreeWithItsVerificationTimestamp(string state, bool hasTimestamp)
    {
        var artifact = Artifact(Run());
        Assert.Throws<ArgumentException>(() => new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id,
            "local", "default", "backup-key", 100, ArtifactHash, "sha256", state, "ReadBack", Now, hasTimestamp ? Now : null));
    }
    [Theory]
    [InlineData("SqlException: password=secret")]
    [InlineData("UnexpectedFailure")]
    [InlineData(null)]
    public void FailureCategoriesRejectArbitraryExceptionDetailsWithoutMutatingOutcome(string? reason)
    {
        var run = Run();
        var token = Guid.NewGuid();
        run.Claim(token, Now, TimeSpan.FromMinutes(2));
        Assert.Throws<ArgumentException>(() => run.Complete(token, Now, new BackupCompletion(BackupStatus.Failed, reason, null)));
        Assert.Equal(BackupStatus.Leased, run.Status);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.TerminalReason);
    }
    private static BackupRun Run()
    {
        var run = new BackupRun(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), AgentId.New(), Configuration(), "manual", Now);
        run.CreateAttempt(Guid.NewGuid(), Now);
        return run;
    }
    private static BackupConfiguration Configuration() => new("mssql", "Database", "localhost", null, true, null,
        true, false, "default", "local", "", true, "Preferred", "Preferred");
    private static BackupArtifact Artifact(BackupRun run) => new(Guid.NewGuid(), run.TenantId, run.CurrentAttempt.Id,
        100, SourceHash, ArtifactHash, "sha256", Now, "{}", "{}");
    private static BackupReplica Replica(BackupArtifact artifact, string key = "backup-key") => new(Guid.NewGuid(), artifact.TenantId,
        artifact.Id, "local", "default", key, 100, ArtifactHash, "sha256", "Verified", "ReadBack", Now, Now);
}
