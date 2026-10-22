using BackupPlatform.SnapshotProviders.Abstractions;

namespace BackupPlatform.AgentTests;

public sealed class SnapshotLifecycleTests
{
    [Fact]
    public async Task BtrfsSnapshotIsReadOnlyAndDisposedExactlyOnce()
    {
        var commands = new Commands();
        var provider = new NativeSnapshotProvider(SnapshotKind.Btrfs, commands);
        var root = Path.Combine(Path.GetTempPath(), "snapshot-tests-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(root, "attempt-1");
        var snapshot = await provider.CreateAsync(new SnapshotRequest("/data", root, "attempt-1"), default);
        Assert.Equal(destination, snapshot.Path);
        Assert.Equal(new[] { "subvolume", "snapshot", "-r", "/data", destination }, commands.Calls[0].Args);
        await snapshot.DisposeAsync();
        await snapshot.DisposeAsync();
        Assert.Equal(2, commands.Calls.Count);
        Assert.Equal(new[] { "subvolume", "delete", destination }, commands.Calls[1].Args);
    }

    [Fact]
    public async Task FailedMountRemovesOnlyTheOwnedLvmSnapshot()
    {
        var commands = new Commands { Fail = "mount" };
        var provider = new NativeSnapshotProvider(SnapshotKind.Lvm, commands);
        var root = Path.Combine(Path.GetTempPath(), "snapshot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => provider.CreateAsync(new SnapshotRequest("/dev/vg/data", root, "attempt-2", "1G"), default));
            Assert.Contains(commands.Calls, call => call.File == "mount");
            Assert.Contains(commands.Calls, call => call.File == "lvremove" && call.Args.SequenceEqual(new[] { "--force", "/dev/vg/attempt-2" }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Commands : ISnapshotCommandRunner
    {
        public string? Fail { get; init; }
        public List<(string File, string[] Args)> Calls { get; } = [];
        public Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, arguments.ToArray()));
            if (fileName == Fail) throw new IOException("Command failed");
            return Task.FromResult(string.Empty);
        }
    }
}
