using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.UnitTests;

public sealed class BackupShareDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private const long Size = 1024;
    private static readonly string Checksum = new('a', 64);

    private static (BackupArtifact Artifact, BackupReplica Replica) CreateArtifact(
        bool hasVerifiedReplica = true,
        BackupReplicaStatus status = BackupReplicaStatus.Available)
    {
        var tenant = TenantId.New();
        var artifact = new BackupArtifact(
            Guid.NewGuid(),
            tenant,
            Guid.NewGuid(),
            Size,
            Checksum,
            Checksum,
            "sha256",
            Now,
            "{}",
            "{}");

        var replica = new BackupReplica(
            Guid.NewGuid(),
            tenant,
            artifact.Id,
            "central",
            "default",
            "backup.bak",
            Size,
            Checksum,
            "sha256",
            hasVerifiedReplica ? "Verified" : "Unverified",
            "ReadBack",
            Now,
            hasVerifiedReplica ? Now : null);

        if (status != BackupReplicaStatus.Available)
        {
            if (status == BackupReplicaStatus.Quarantined)
            {
                replica.Quarantine();
            }
            else
            {
                replica.RequestDelete(Now, BackupDeleteReason.RetentionExpired, TimeSpan.FromDays(1), Guid.NewGuid());
            }
        }

        artifact.AddReplica(replica);
        return (artifact, replica);
    }

    [Fact]
    public void OmittingDurationYieldsExactly24HoursExpiry()
    {
        var (artifact, _) = CreateArtifact();
        var actor = UserId.New();

        var share = BackupShare.Create(
            Guid.NewGuid(),
            artifact,
            actor,
            UserRole.UserAdmin,
            Now,
            duration: null,
            reason: "PlatformAdmin inspection");

        Assert.Equal(Now, share.CreatedAtUtc);
        Assert.Equal(Now.AddHours(24), share.ExpiresAtUtc);
        Assert.Equal(BackupShare.DefaultDuration, share.ExpiresAtUtc - share.CreatedAtUtc);
        Assert.Equal(BackupShareStatus.Active, share.GetStatus(Now));
    }

    [Fact]
    public void CustomDurationUpTo72HoursAccepted()
    {
        var (artifact, _) = CreateArtifact();
        var actor = UserId.New();
        var duration = TimeSpan.FromHours(72);

        var share = BackupShare.Create(
            Guid.NewGuid(),
            artifact,
            actor,
            UserRole.UserAdmin,
            Now,
            duration: duration);

        Assert.Equal(Now.AddHours(72), share.ExpiresAtUtc);
        Assert.Equal(BackupShareStatus.Active, share.GetStatus(Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(73)]
    [InlineData(100)]
    public void DurationZeroNegativeOrAbove72HoursRejected(int hours)
    {
        var (artifact, _) = CreateArtifact();
        var actor = UserId.New();
        var duration = TimeSpan.FromHours(hours);

        var exception = Assert.Throws<ArgumentException>(() =>
            BackupShare.Create(Guid.NewGuid(), artifact, actor, UserRole.UserAdmin, Now, duration));

        Assert.Contains("duration", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CannotCreateShareWithoutVerifiedAndAvailableReplica()
    {
        var (unverifiedArtifact, _) = CreateArtifact(hasVerifiedReplica: false);
        var actor = UserId.New();

        Assert.Throws<InvalidOperationException>(() =>
            BackupShare.Create(Guid.NewGuid(), unverifiedArtifact, actor, UserRole.UserAdmin, Now));

        var (deletePendingArtifact, _) = CreateArtifact(hasVerifiedReplica: true, status: BackupReplicaStatus.DeletePending);
        Assert.Throws<InvalidOperationException>(() =>
            BackupShare.Create(Guid.NewGuid(), deletePendingArtifact, actor, UserRole.UserAdmin, Now));
    }

    [Fact]
    public void DerivedStatusTransitionsFromActiveToExpired()
    {
        var (artifact, _) = CreateArtifact();
        var share = BackupShare.Create(
            Guid.NewGuid(),
            artifact,
            UserId.New(),
            UserRole.UserAdmin,
            Now,
            TimeSpan.FromHours(24));

        Assert.Equal(BackupShareStatus.Active, share.GetStatus(Now));
        Assert.Equal(BackupShareStatus.Active, share.GetStatus(Now.AddHours(23).AddMinutes(59)));
        Assert.Equal(BackupShareStatus.Expired, share.GetStatus(Now.AddHours(24)));
        Assert.Equal(BackupShareStatus.Expired, share.GetStatus(Now.AddHours(25)));
        Assert.True(share.IsActive(Now));
        Assert.False(share.IsActive(Now.AddHours(24)));
    }

    [Fact]
    public void RevokedShareHasRevokedStatus()
    {
        var (artifact, _) = CreateArtifact();
        var actor = UserId.New();
        var revoker = UserId.New();

        var share = BackupShare.Create(
            Guid.NewGuid(),
            artifact,
            actor,
            UserRole.UserAdmin,
            Now,
            TimeSpan.FromHours(24));

        var revokeTime = Now.AddHours(2);
        share.Revoke(revoker, revokeTime);

        Assert.Equal(revokeTime, share.RevokedAtUtc);
        Assert.Equal(revoker, share.RevokedByUserId);
        Assert.Equal(BackupShareStatus.Revoked, share.GetStatus(revokeTime));
        Assert.Equal(BackupShareStatus.Revoked, share.GetStatus(Now.AddHours(30)));
        Assert.False(share.IsActive(revokeTime));
    }

    [Fact]
    public void SeveralActiveSharesForSameArtifactCoexist()
    {
        var (artifact, _) = CreateArtifact();
        var user1 = UserId.New();
        var user2 = UserId.New();

        var share1 = BackupShare.Create(Guid.NewGuid(), artifact, user1, UserRole.UserAdmin, Now, TimeSpan.FromHours(12), "Reason 1");
        var share2 = BackupShare.Create(Guid.NewGuid(), artifact, user2, UserRole.UserAdmin, Now, TimeSpan.FromHours(48), "Reason 2");

        Assert.NotEqual(share1.Id, share2.Id);
        Assert.Equal(share1.ArtifactId, share2.ArtifactId);
        Assert.NotEqual(share1.CreatedByUserId, share2.CreatedByUserId);
        Assert.NotEqual(share1.ExpiresAtUtc, share2.ExpiresAtUtc);
        Assert.Equal("Reason 1", share1.Reason);
        Assert.Equal("Reason 2", share2.Reason);
        Assert.True(share1.IsActive(Now));
        Assert.True(share2.IsActive(Now));
    }

    [Fact]
    public void PlatformAdminShare_RecordsPlatformAdminRole()
    {
        var (artifact, _) = CreateArtifact();
        var platformUser = UserId.New();

        var share = BackupShare.Create(Guid.NewGuid(), artifact, platformUser, UserRole.PlatformAdmin, Now, TimeSpan.FromHours(24), "Platform debug");

        Assert.Equal(UserRole.PlatformAdmin, share.CreatedByRole);
        Assert.Equal(platformUser, share.CreatedByUserId);
        Assert.True(share.CanBeManagedBy(UserRole.PlatformAdmin));
        Assert.False(share.CanBeManagedBy(UserRole.UserAdmin));
    }

    [Fact]
    public void PlatformAdminShare_TenantUserAdminCannotRevoke_ThrowsInvalidOperationException()
    {
        var (artifact, _) = CreateArtifact();
        var platformUser = UserId.New();
        var tenantUser = UserId.New();

        var share = BackupShare.Create(Guid.NewGuid(), artifact, platformUser, UserRole.PlatformAdmin, Now);

        Assert.Throws<InvalidOperationException>(() =>
            share.Revoke(tenantUser, UserRole.UserAdmin, Now.AddHours(1)));
        Assert.True(share.IsActive(Now.AddHours(1)));
    }

    [Fact]
    public void PlatformAdminShare_PlatformAdminCanRevoke()
    {
        var (artifact, _) = CreateArtifact();
        var platformUser = UserId.New();
        var anotherPlatformUser = UserId.New();

        var share = BackupShare.Create(Guid.NewGuid(), artifact, platformUser, UserRole.PlatformAdmin, Now);
        share.Revoke(anotherPlatformUser, UserRole.PlatformAdmin, Now.AddHours(1));

        Assert.Equal(BackupShareStatus.Revoked, share.GetStatus(Now.AddHours(2)));
        Assert.Equal(anotherPlatformUser, share.RevokedByUserId);
    }
}
