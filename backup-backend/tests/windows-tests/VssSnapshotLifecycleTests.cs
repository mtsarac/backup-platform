using BackupPlatform.Agent.Platform.Windows.Vss;
using BackupPlatform.SnapshotProviders.Abstractions;
using Xunit;

namespace BackupPlatform.Agent.Platform.Windows.Tests;

/// <summary>Runs the real VSS requester: needs Backup Operators or Administrators, so it is opt-in with the other frozen tests.</summary>
[Trait("Category", "Frozen")]
public sealed class VssSnapshotLifecycleTests
{
    private static readonly string SystemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;

    [WindowsFact]
    public async Task The_snapshot_is_readable_by_its_device_path_and_released_once()
    {
        var marker = Path.Combine(SystemRoot, "bp-vss-test-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(marker, "before");
        try
        {
            await using var snapshot = await new WindowsVssSnapshotProvider().CreateAsync(new SnapshotRequest(SystemRoot, SystemRoot, "attempt-1"), default);
            await File.WriteAllTextAsync(marker, "after");
            Assert.StartsWith(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy", snapshot.Path, StringComparison.Ordinal);
            Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(snapshot.Path, Path.GetFileName(marker))));
            await snapshot.DisposeAsync();
            await snapshot.DisposeAsync();
        }
        finally { File.Delete(marker); }
    }

    [WindowsFact]
    public async Task A_source_that_is_not_a_volume_root_is_refused_before_VSS_is_called() =>
        await Assert.ThrowsAsync<ArgumentException>(() => new WindowsVssSnapshotProvider().CreateAsync(new SnapshotRequest(@"C:\Users", SystemRoot, "attempt-2"), default));

    [WindowsFact]
    public async Task A_cancelled_request_creates_nothing() =>
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsVssSnapshotProvider().CreateAsync(new SnapshotRequest(SystemRoot, SystemRoot, "attempt-3"), new CancellationToken(true)));

    [WindowsFact]
    public async Task A_nested_source_folder_is_read_through_the_snapshot_of_its_volume_root()
    {
        var folder = Path.Combine(SystemRoot, "bp-vss-nested-" + Guid.NewGuid().ToString("N"), "inner");
        var staging = Path.Combine(Path.GetTempPath(), "bp-vss-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "content");
        try
        {
            var sut = new BackupPlatform.BackupProviders.FileSystem.FileSystemBackupProvider([new WindowsVssSnapshotProvider()], null);
            var source = new BackupPlatform.BackupProviders.FileSystem.FileSystemBackupSource(folder, ["**"], [], SnapshotKind: SnapshotKind.Vss);
            var (manifest, _) = await sut.CreateBackupAsync(source, staging, null, default);
            Assert.Equal("a.txt", Assert.Single(manifest.Files).RelativePath);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, true);
            Directory.Delete(staging, true);
        }
    }

    [WindowsFact]
    public async Task A_sweep_never_touches_a_snapshot_a_live_lease_owns_and_drops_ledger_entries_of_snapshots_that_are_gone()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bp-vss-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var provider = new WindowsVssSnapshotProvider();
            await using var live = await provider.CreateAsync(new SnapshotRequest(SystemRoot, directory, "attempt-4"), default);
            var ledger = Path.Combine(directory, ".bp-vss");
            var gone = Path.Combine(ledger, Guid.NewGuid().ToString("N") + ".owned");
            await File.WriteAllTextAsync(gone, "x");
            Assert.Equal(0, await provider.SweepLeftoversAsync(directory, default));
            Assert.False(File.Exists(gone));
            Assert.True(Directory.Exists(live.Path));
            Assert.Single(Directory.GetFiles(ledger));
            await live.DisposeAsync();
            Assert.Empty(Directory.GetFiles(ledger));
        }
        finally { Directory.Delete(directory, true); }
    }

    [WindowsFact]
    public async Task A_ledger_that_other_accounts_can_write_is_refused_and_a_forged_entry_deletes_nothing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bp-vss-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var ledger = Path.Combine(directory, ".bp-vss");
            BackupPlatform.Agent.Platform.Windows.Vss.ProtectedLedger.Ensure(ledger);
            BackupPlatform.Agent.Platform.Windows.Vss.ProtectedLedger.Verify(ledger);

            var forged = Path.Combine(ledger, Guid.NewGuid().ToString("N") + ".owned");
            await File.WriteAllTextAsync(forged, "not the marker");
            Assert.False(BackupPlatform.Agent.Platform.Windows.Vss.ProtectedLedger.IsEntry(forged));
            Assert.Equal(0, await new WindowsVssSnapshotProvider().SweepLeftoversAsync(directory, default));
            Assert.True(File.Exists(forged), "an entry without the agent's marker is ignored, not consumed");

            var users = new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                System.Security.AccessControl.FileSystemRights.Write, System.Security.AccessControl.AccessControlType.Allow);
            var security = new DirectoryInfo(ledger).GetAccessControl();
            security.AddAccessRule(users);
            new DirectoryInfo(ledger).SetAccessControl(security);
            Assert.Throws<UnauthorizedAccessException>(() => BackupPlatform.Agent.Platform.Windows.Vss.ProtectedLedger.Verify(ledger));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new WindowsVssSnapshotProvider().SweepLeftoversAsync(directory, default));
        }
        finally { Directory.Delete(directory, true); }
    }
}
