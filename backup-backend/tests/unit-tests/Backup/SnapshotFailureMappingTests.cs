using BackupPlatform.BackupProviders.Abstractions;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.FileSystem;
using BackupPlatform.SnapshotProviders.Abstractions;
using Xunit;

namespace BackupPlatform.UnitTests.Backup;

public sealed class SnapshotFailureMappingTests
{
    [Theory]
    [InlineData(0x80070005u, SnapshotFailureKind.AccessDenied)]
    [InlineData(0x80070057u, SnapshotFailureKind.InvalidConfiguration)]
    [InlineData(0x8004230Cu, SnapshotFailureKind.InvalidConfiguration)]
    [InlineData(0x8004230Eu, SnapshotFailureKind.InvalidConfiguration)]
    [InlineData(0x8004231Bu, SnapshotFailureKind.InvalidConfiguration)]
    [InlineData(0x8004231Fu, SnapshotFailureKind.Failed)]
    [InlineData(0x80042316u, SnapshotFailureKind.Failed)]
    [InlineData(0x80042301u, SnapshotFailureKind.Failed)]
    public void A_VSS_result_is_classified_by_what_the_operator_can_do_about_it(uint hresult, SnapshotFailureKind expected) =>
        Assert.Equal(expected, VssResult.ToException(unchecked((int)hresult), "test").Kind);

    [Theory]
    [InlineData(SnapshotFailureKind.AccessDenied, "snapshot-access-denied", BackupFailureCause.PermissionDenied)]
    [InlineData(SnapshotFailureKind.Failed, "snapshot-failed", BackupFailureCause.SnapshotFailed)]
    [InlineData(SnapshotFailureKind.InvalidConfiguration, "snapshot-invalid-configuration", BackupFailureCause.InvalidConfiguration)]
    public void A_snapshot_failure_is_never_reported_as_a_staging_problem(SnapshotFailureKind kind, string reason, BackupFailureCause cause) =>
        Assert.Equal((reason, cause), FileSystemBackupEngine.FailureOf(new SnapshotException(kind, "x")));

    [Fact]
    public void A_plain_IO_failure_is_still_blamed_on_staging()
    {
        Assert.Equal(BackupFailureCause.StagingInvalid, FileSystemBackupEngine.FailureOf(new IOException("disk")).Cause);
        Assert.Equal(BackupFailureCause.StagingInvalid, FileSystemBackupEngine.FailureOf(new UnauthorizedAccessException()).Cause);
    }

    [Fact]
    public async Task The_provider_lets_the_snapshot_failure_reach_the_engine_and_creates_no_archive()
    {
        var temp = Path.Combine(Path.GetTempPath(), "bp-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "source"));
        Directory.CreateDirectory(Path.Combine(temp, "staging"));
        try
        {
            var sut = new FileSystemBackupProvider([new Denied()], null);
            var source = new FileSystemBackupSource(Path.Combine(temp, "source"), ["**"], [], SnapshotKind: SnapshotKind.Lvm, SnapshotSource: "/dev/vg/lv", LvmSize: "1G");
            var error = await Assert.ThrowsAsync<SnapshotException>(() => sut.CreateBackupAsync(source, Path.Combine(temp, "staging"), null, default));
            Assert.Equal(SnapshotFailureKind.AccessDenied, error.Kind);
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(temp, "staging")));
        }
        finally { Directory.Delete(temp, true); }
    }

    private sealed class Denied : ISnapshotProvider
    {
        public SnapshotKind Kind => SnapshotKind.Lvm;
        public Task<SnapshotLease> CreateAsync(SnapshotRequest request, CancellationToken cancellationToken) =>
            throw new SnapshotException(SnapshotFailureKind.AccessDenied, "denied");
    }
}
