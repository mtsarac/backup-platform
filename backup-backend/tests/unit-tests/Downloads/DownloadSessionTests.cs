using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests;

public sealed class DownloadSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private const long Size = 1024;
    private static readonly string Checksum = new('b', 64);

    private static (BackupArtifact Artifact, BackupReplica Replica) Source(bool local = false)
    {
        var tenant = TenantId.New();
        var artifact = new BackupArtifact(Guid.NewGuid(), tenant, Guid.NewGuid(), Size, Checksum, Checksum, "sha256", Now, "{}", "{}");
        var replica = new BackupReplica(Guid.NewGuid(), tenant, artifact.Id, local ? "local" : "central", "default", "backup.bak", Size,
            Checksum, "sha256", "Verified", "ReadBack", Now, Now, local ? BackupStorageExecutionLocation.Agent : BackupStorageExecutionLocation.Backend,
            local ? Guid.NewGuid() : null);
        artifact.AddReplica(replica);
        return (artifact, replica);
    }

    private static DownloadSession Session(bool local = false)
    {
        var source = Source(local);
        return new DownloadSession(Guid.NewGuid(), source.Artifact, source.Replica, Guid.NewGuid(), "master.bak", Now, TimeSpan.FromHours(1));
    }

    [Theory]
    [InlineData(false, DownloadState.Ready)]
    [InlineData(true, DownloadState.Preparing)]
    public void NewSessionSnapshotsVerifiedSourceAndPinsIt(bool local, DownloadState state)
    {
        var source = Source(local);
        var user = Guid.NewGuid();
        var session = new DownloadSession(Guid.NewGuid(), source.Artifact, source.Replica, user, "master.bak", Now, TimeSpan.FromHours(1));
        Assert.Equal(state, session.State);
        Assert.Equal(user, session.UserId);
        Assert.Equal(source.Artifact.TenantId, session.TenantId);
        Assert.Equal(source.Artifact.Id, session.ArtifactId);
        Assert.Equal(source.Replica.Id, session.ReplicaId);
        Assert.Equal(source.Replica.OwningAgentId, session.AgentId);
        Assert.Equal(source.Replica.Key, session.Key);
        Assert.Equal(Checksum, session.Checksum);
        Assert.Equal(Size, session.SizeBytes);
        Assert.True(session.IsPinned(Now));
        Assert.False(session.IsPinned(session.ExpiresAt));
        Assert.Null(session.ReleasedAt);
    }

    [Fact]
    public void ReplicaFromAnotherArtifactOrTenantCannotBecomeASession()
    {
        var source = Source();
        var foreign = Source();
        Assert.Throws<ArgumentException>(() => new DownloadSession(Guid.NewGuid(), source.Artifact, foreign.Replica, Guid.NewGuid(), "master.bak", Now, Lease));
        var sameTenant = new BackupReplica(Guid.NewGuid(), source.Artifact.TenantId, Guid.NewGuid(), "central", "default", "other.bak", Size,
            Checksum, "sha256", "Verified", "ReadBack", Now, Now);
        Assert.Throws<ArgumentException>(() => new DownloadSession(Guid.NewGuid(), source.Artifact, sameTenant, Guid.NewGuid(), "master.bak", Now, Lease));
        source.Artifact.RequestDelete(Now, Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => new DownloadSession(Guid.NewGuid(), source.Artifact, source.Replica, Guid.NewGuid(), "master.bak", Now, Lease));
    }

    [Fact]
    public void CompletionRequiresExactSizeAndAValidStreamingLease()
    {
        var session = Session();
        Assert.False(session.Transition(DownloadState.Ready, null, DownloadState.Completed, null, Now, null, Size, null));
        Assert.False(session.Transition(DownloadState.Ready, null, DownloadState.Streaming, null, Now, Lease, 0, null));
        Assert.True(session.Transition(DownloadState.Ready, null, DownloadState.Streaming, "node-a", Now, Lease, 0, null));
        Assert.False(session.Transition(DownloadState.Streaming, "node-a", DownloadState.Completed, null, Now.AddSeconds(1), null, Size - 1, null));
        Assert.True(session.Transition(DownloadState.Streaming, "node-a", DownloadState.Completed, null, Now.AddSeconds(2), null, Size, null));
        Assert.Equal(Size, session.TransferredBytes);
        Assert.Equal(Now.AddSeconds(2), session.ReleasedAt);
        Assert.Null(session.LeaseOwner);
        Assert.Null(session.LeaseExpiresAt);
        Assert.False(session.IsPinned(Now.AddSeconds(2)));
        Assert.False(session.Transition(DownloadState.Completed, null, DownloadState.Streaming, "node-b", Now.AddSeconds(3), Lease, Size, null));
        Assert.False(session.Expire(Now.AddDays(1)));
    }

    [Fact]
    public void ProgressIsMonotonicAndCannotExceedTheSnapshotSize()
    {
        var session = Session();
        Assert.True(session.Transition(DownloadState.Ready, null, DownloadState.Streaming, "node-a", Now, Lease, 10, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Transition(DownloadState.Streaming, "node-a", DownloadState.Streaming, "node-a", Now, Lease, 9, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Transition(DownloadState.Streaming, "node-a", DownloadState.Completed, null, Now, null, Size + 1, null));
        Assert.Equal(10, session.TransferredBytes);
        Assert.Equal(DownloadState.Streaming, session.State);
    }

    [Fact]
    public void StateOwnerAndLeaseExpiryFenceStaleWriters()
    {
        var session = Session();
        Assert.True(session.Transition(DownloadState.Ready, null, DownloadState.Streaming, "node-a", Now, Lease, 0, null));
        Assert.False(session.Transition(DownloadState.Ready, null, DownloadState.Streaming, "node-b", Now, Lease, 0, null));
        Assert.False(session.Transition(DownloadState.Streaming, "node-b", DownloadState.Completed, null, Now, null, Size, null));
        Assert.False(session.Transition(DownloadState.Streaming, "node-a", DownloadState.Completed, null, Now + Lease, null, Size, null));
        Assert.False(session.IsPinned(Now + Lease));
        Assert.True(session.Expire(Now + Lease));
        Assert.Equal(DownloadState.Failed, session.State);
        Assert.Equal("download_interrupted", session.FailureCode);
        Assert.Equal(Now + Lease, session.ReleasedAt);
    }

    [Fact]
    public void UnclaimedSessionExpiresAtItsExactDeadline()
    {
        var session = Session();
        Assert.False(session.Expire(session.ExpiresAt.AddMilliseconds(-1)));
        Assert.False(session.Transition(DownloadState.Ready, null, DownloadState.Streaming, "node-a", session.ExpiresAt, Lease, 0, null));
        Assert.True(session.Expire(session.ExpiresAt));
        Assert.Equal("download_expired", session.FailureCode);
        Assert.Equal(session.ExpiresAt, session.ReleasedAt);
        Assert.True(session.IsTerminal);
    }

    [Theory]
    [InlineData(DownloadState.Failed, "integrity_mismatch")]
    [InlineData(DownloadState.Cancelled, "download_cancelled")]
    public void FailureAndCancellationReleaseThePinAndKeepPartialBytes(DownloadState state, string code)
    {
        var session = Session();
        Assert.True(session.Transition(DownloadState.Ready, null, DownloadState.Streaming, "node-a", Now, Lease, 128, null));
        Assert.True(session.Transition(DownloadState.Streaming, "node-a", state, null, Now.AddSeconds(1), null, 128, code));
        Assert.Equal(state, session.State);
        Assert.Equal(code, session.FailureCode);
        Assert.Equal(128, session.TransferredBytes);
        Assert.NotNull(session.ReleasedAt);
        Assert.False(session.IsPinned(Now));
    }

    [Fact]
    public void PreparingAgentLeaseMustBeFencedBeforeReady()
    {
        var session = Session(local: true);
        Assert.True(session.Transition(DownloadState.Preparing, null, DownloadState.Preparing, "agent-a", Now, Lease, 0, null));
        Assert.False(session.Transition(DownloadState.Preparing, "agent-b", DownloadState.Ready, null, Now.AddSeconds(1), null, 0, null));
        Assert.True(session.Transition(DownloadState.Preparing, "agent-a", DownloadState.Ready, null, Now.AddSeconds(1), null, 0, null));
        Assert.Null(session.LeaseOwner);
        Assert.Null(session.LeaseExpiresAt);
        Assert.True(session.IsPinned(Now.AddSeconds(1)));
    }

    [Fact]
    public void RenewingActiveTransferExtendsDeadlineWithoutLosingPin()
    {
        var session = Session();
        var started = session.ExpiresAt.AddSeconds(-1);
        Assert.True(session.Transition(DownloadState.Ready, null, DownloadState.Streaming, "node-a", started, Lease, 0, null));
        Assert.Equal(started + Lease, session.ExpiresAt);
        var renewed = started.AddSeconds(30);
        Assert.True(session.Transition(DownloadState.Streaming, "node-a", DownloadState.Streaming, "node-a", renewed, Lease, 512, null));
        Assert.Equal(renewed + Lease, session.ExpiresAt);
        Assert.True(session.IsPinned(started + Lease));
        Assert.False(session.Expire(started + Lease));
        Assert.True(session.Transition(DownloadState.Streaming, "node-a", DownloadState.Completed, null, renewed.AddSeconds(1), null, Size, null));
    }

    [Theory]
    [InlineData("Unverified")]
    [InlineData("Failed")]
    public void UnverifiedReplicaCannotCreateDownload(string verification)
    {
        var source = Source();
        var replica = new BackupReplica(Guid.NewGuid(), source.Artifact.TenantId, source.Artifact.Id, "central", "default", "unverified.bak", Size,
            Checksum, "sha256", verification, "ReadBack", Now, null);
        Assert.Throws<ArgumentException>(() => new DownloadSession(Guid.NewGuid(), source.Artifact, replica, Guid.NewGuid(), "master.bak", Now, Lease));
    }

    [Fact]
    public void TenSecondRenewalsKeepTransferPinnedPastFifteenMinuteInitialWindow()
    {
        var source = Source();
        var session = new DownloadSession(Guid.NewGuid(), source.Artifact, source.Replica, Guid.NewGuid(), "master.bak", Now, TimeSpan.FromMinutes(15));
        var lease = TimeSpan.FromSeconds(60);
        Assert.True(session.Transition(DownloadState.Ready, null, DownloadState.Streaming, "node-a", Now, lease, 0, null));
        for (var seconds = 10; seconds <= 1200; seconds += 10)
        {
            var at = Now.AddSeconds(seconds);
            Assert.True(session.Transition(DownloadState.Streaming, "node-a", DownloadState.Streaming, "node-a", at, lease, seconds / 10, null));
            Assert.True(session.IsPinned(at));
            Assert.False(session.Expire(at));
            Assert.Equal(DownloadState.Streaming, session.State);
            Assert.Equal(at + lease, session.LeaseExpiresAt);
        }
        Assert.True(session.ExpiresAt > Now.AddMinutes(20));
        Assert.True(session.Transition(DownloadState.Streaming, "node-a", DownloadState.Completed, null, Now.AddMinutes(20).AddSeconds(1), null, Size, null));
        Assert.False(session.IsPinned(Now.AddMinutes(20).AddSeconds(1)));
    }
    [Fact]
    public void ReplicaSwitchRejectsForeignContentAndAgentLocalSources()
    {
        var source = Source();
        var session = new DownloadSession(Guid.NewGuid(), source.Artifact, source.Replica, Guid.NewGuid(), "backup.bak", Now, Lease);
        BackupReplica Replica(long size = Size, string? checksum = null, bool local = false) =>
            new(Guid.NewGuid(), source.Artifact.TenantId, source.Artifact.Id, local ? "local" : "central", "default", "alternate.bak",
                size, checksum ?? Checksum, "sha256", "Verified", "ReadBack", Now, Now,
                local ? BackupStorageExecutionLocation.Agent : BackupStorageExecutionLocation.Backend, local ? Guid.NewGuid() : null);
        Assert.False(session.SwitchReplica(Source().Replica, null, Now));
        Assert.False(session.SwitchReplica(Replica(size: Size - 1), null, Now));
        Assert.False(session.SwitchReplica(Replica(checksum: new string('a', 64)), null, Now));
        Assert.False(session.SwitchReplica(Replica(local: true), null, Now));
        var alternate = Replica();
        Assert.True(session.SwitchReplica(alternate, null, Now));
        Assert.Equal(alternate.Id, session.ReplicaId);
        Assert.Equal(alternate.Key, session.Key);
    }

    [Fact]
    public void ReplicaSwitchRequiresLiveOwnerAndCannotChangeSourceAfterBytesAreSent()
    {
        var source = Source();
        var session = new DownloadSession(Guid.NewGuid(), source.Artifact, source.Replica, Guid.NewGuid(), "backup.bak", Now, Lease);
        Assert.True(session.Transition(DownloadState.Ready, null, DownloadState.Streaming, "owner", Now, Lease, 0, null));
        Assert.False(session.SwitchReplica(source.Replica, "stale-owner", Now));
        Assert.False(session.SwitchReplica(source.Replica, "owner", Now + Lease));
        Assert.True(session.Transition(DownloadState.Streaming, "owner", DownloadState.Streaming, "owner", Now, Lease, 1, null));
        Assert.False(session.SwitchReplica(source.Replica, "owner", Now));
        Assert.Equal(source.Replica.Id, session.ReplicaId);
    }
}
