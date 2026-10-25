using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests.Backup;

public sealed class ReplicaMissingUnitTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private static BackupReplica Replica(string verification = "Verified") => new(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), "sftp", "target", "scope/a.bak", 10,
        new string('a', 64), "sha256", verification, "ReadBack", Now, verification == "Verified" ? Now : null, BackupStorageExecutionLocation.Backend, storageConnectionId: Guid.NewGuid());

    [Fact]
    public void A_verified_replica_whose_object_vanished_stops_being_verified_and_usable()
    {
        var replica = Replica();

        Assert.True(replica.MarkMissing());

        Assert.Equal(("Failed", BackupReplica.MissingVerificationMethod, null), (replica.VerificationState, replica.VerificationMethod, replica.VerifiedAt));
        Assert.False(replica.IsUsable);
        Assert.False(replica.MarkMissing());
    }

    [Fact]
    public void An_unverified_replica_is_left_alone()
    {
        var replica = Replica("Unverified");

        Assert.False(replica.MarkMissing());
        Assert.Equal("Unverified", replica.VerificationState);
    }
}
