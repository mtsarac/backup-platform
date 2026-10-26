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

    private static string Owned(char c) => "bp_" + new string(c, 32);

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "snapshot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task BtrfsCancelledAfterCreationDeletesTheSnapshot()
    {
        var commands = new Commands();
        var root = TempRoot();
        try
        {
            using var cancel = new CancellationTokenSource();
            commands.OnRun = file => cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NativeSnapshotProvider(SnapshotKind.Btrfs, commands)
                .CreateAsync(new SnapshotRequest("/data", root, "attempt-3"), cancel.Token));
            Assert.Equal(["snapshot", "delete"], commands.Calls.Select(call => call.Args[1]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BtrfsRefusesAnExistingDestinationWithoutRunningAnything()
    {
        var commands = new Commands();
        var root = TempRoot();
        Directory.CreateDirectory(Path.Combine(root, "attempt-4"));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new NativeSnapshotProvider(SnapshotKind.Btrfs, commands)
                .CreateAsync(new SnapshotRequest("/data", root, "attempt-4"), default));
            Assert.Empty(commands.Calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ZfsSnapshotIsReadFromTheDotZfsDirectoryAndDestroyedOnce()
    {
        var commands = new Commands { Outputs = { ["zfs get"] = "/tank/data\n" } };
        var lease = await new NativeSnapshotProvider(SnapshotKind.Zfs, commands).CreateAsync(new SnapshotRequest("tank/data", "/unused", "attempt-5"), default);
        Assert.Equal(Path.Combine("/tank/data", ".zfs", "snapshot", "attempt-5"), lease.Path);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Equal(["zfs snapshot tank/data@attempt-5", "zfs get -H -o value mountpoint tank/data", "zfs destroy tank/data@attempt-5"],
            commands.Calls.Select(call => call.File + " " + string.Join(' ', call.Args)));
    }

    [Fact]
    public async Task ZfsUnmountedDatasetDestroysTheSnapshotItJustTook()
    {
        var commands = new Commands { Outputs = { ["zfs get"] = "none\n" } };
        await Assert.ThrowsAsync<IOException>(() => new NativeSnapshotProvider(SnapshotKind.Zfs, commands)
            .CreateAsync(new SnapshotRequest("tank/data", "/unused", "attempt-6"), default));
        Assert.Equal(["snapshot", "get", "destroy"], commands.Calls.Select(call => call.Args[0]));
    }

    [Theory]
    [InlineData("tank/../data")]
    [InlineData("-r")]
    [InlineData("/tank/data")]
    public async Task ZfsRejectsASourceThatIsNotADatasetName(string source)
    {
        var commands = new Commands();
        await Assert.ThrowsAsync<ArgumentException>(() => new NativeSnapshotProvider(SnapshotKind.Zfs, commands)
            .CreateAsync(new SnapshotRequest(source, "/unused", "attempt-7"), default));
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task LvmSnapshotIsMountedReadOnlyAndUnmountedThenRemovedOnce()
    {
        var commands = new Commands();
        var root = TempRoot();
        try
        {
            var destination = Path.Combine(root, "attempt-8");
            var lease = await new NativeSnapshotProvider(SnapshotKind.Lvm, commands).CreateAsync(new SnapshotRequest("/dev/vg/data", root, "attempt-8", "1G"), default);
            Assert.Equal(destination, lease.Path);
            Assert.True(Directory.Exists(destination));
            await lease.DisposeAsync();
            await lease.DisposeAsync();
            Assert.False(Directory.Exists(destination));
            Assert.Equal(
            [
                "lvcreate --snapshot --permission r --size 1G --name attempt-8 /dev/vg/data",
                $"mount -o ro /dev/vg/attempt-8 {destination}",
                $"umount {destination}",
                "lvremove --force /dev/vg/attempt-8"
            ], commands.Calls.Select(call => call.File + " " + string.Join(' ', call.Args)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LvmCancelledAfterMountUnmountsAndRemovesTheSnapshot()
    {
        var commands = new Commands();
        var root = TempRoot();
        try
        {
            using var cancel = new CancellationTokenSource();
            commands.OnRun = file => { if (file == "mount") cancel.Cancel(); };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NativeSnapshotProvider(SnapshotKind.Lvm, commands)
                .CreateAsync(new SnapshotRequest("/dev/vg/data", root, "attempt-9", "1G"), cancel.Token));
            Assert.Equal(["lvcreate", "mount", "umount", "lvremove"], commands.Calls.Select(call => call.File));
            Assert.False(Directory.Exists(Path.Combine(root, "attempt-9")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LvmFailedCreationLeavesNothingToRemove()
    {
        var commands = new Commands { Fail = "lvcreate" };
        var root = TempRoot();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new NativeSnapshotProvider(SnapshotKind.Lvm, commands)
                .CreateAsync(new SnapshotRequest("/dev/vg/data", root, "attempt-10", "1G"), default));
            Assert.Equal(["lvcreate"], commands.Calls.Select(call => call.File));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("1G; rm -rf /")]
    public async Task LvmRequiresABoundedSnapshotSize(string? size)
    {
        var commands = new Commands();
        await Assert.ThrowsAsync<ArgumentException>(() => new NativeSnapshotProvider(SnapshotKind.Lvm, commands)
            .CreateAsync(new SnapshotRequest("/dev/vg/data", Path.GetTempPath(), "attempt-11", size), default));
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task SweepRemovesOnlyOwnedBtrfsLeftoversAndSparesLiveSnapshots()
    {
        var commands = new Commands();
        var root = TempRoot();
        try
        {
            var provider = new NativeSnapshotProvider(SnapshotKind.Btrfs, commands);
            foreach (var name in new[] { Owned('a'), Owned('b'), "keep-me", "bp_short" }) Directory.CreateDirectory(Path.Combine(root, name));
            var live = await provider.CreateAsync(new SnapshotRequest("/data", root, Owned('c')), default);
            Directory.CreateDirectory(live.Path);
            commands.Calls.Clear();

            Assert.Equal(2, await provider.SweepLeftoversAsync(root, default));
            Assert.Equal([Path.Combine(root, Owned('a')), Path.Combine(root, Owned('b'))],
                commands.Calls.Select(call => call.Args[2]).Order());
            Assert.All(commands.Calls, call => Assert.Equal(["subvolume", "delete"], call.Args[..2]));

            await live.DisposeAsync();
            commands.Calls.Clear();
            foreach (var swept in new[] { Owned('a'), Owned('b') }) Directory.Delete(Path.Combine(root, swept));
            Assert.Equal(1, await provider.SweepLeftoversAsync(root, default));
            Assert.Equal([Path.Combine(root, Owned('c'))], commands.Calls.Select(call => call.Args[2]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SweepDestroysOnlyOwnedZfsSnapshots()
    {
        var commands = new Commands { Outputs = { ["zfs list"] = $"tank/data@{Owned('a')}\ntank/data@weekly\ntank@{Owned('b')}\ntank/data@bp_short\n" } };
        var removed = await new NativeSnapshotProvider(SnapshotKind.Zfs, commands).SweepLeftoversAsync("/unused", default);
        Assert.Equal(2, removed);
        Assert.Equal([$"destroy tank/data@{Owned('a')}", $"destroy tank@{Owned('b')}"],
            commands.Calls.Where(call => call.Args[0] == "destroy").Select(call => string.Join(' ', call.Args)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SweepUnmountsRemovesAndCleansUpOwnedLvmLeftovers(bool stillMounted)
    {
        var root = TempRoot();
        try
        {
            var commands = new Commands { Outputs = { ["lvs"] = $"  vg/{Owned('a')}\n  vg/root\n  vg/{Owned('b')}\n" } };
            var provider = new NativeSnapshotProvider(SnapshotKind.Lvm, commands, path => stillMounted && path == Path.Combine(root, Owned('a')));
            Directory.CreateDirectory(Path.Combine(root, Owned('a')));

            Assert.Equal(2, await provider.SweepLeftoversAsync(root, default));
            var calls = commands.Calls.Where(call => call.File != "lvs").Select(call => call.File + " " + string.Join(' ', call.Args)).ToArray();
            Assert.Equal(stillMounted
                ? [$"umount {Path.Combine(root, Owned('a'))}", $"lvremove --force /dev/vg/{Owned('a')}", $"lvremove --force /dev/vg/{Owned('b')}"]
                : [$"lvremove --force /dev/vg/{Owned('a')}", $"lvremove --force /dev/vg/{Owned('b')}"], calls);
            Assert.False(Directory.Exists(Path.Combine(root, Owned('a'))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SweepKeepsGoingAfterAFailureAndReportsAllOfThem()
    {
        var root = TempRoot();
        try
        {
            var commands = new Commands { Fail = "btrfs" };
            foreach (var name in new[] { Owned('a'), Owned('b') }) Directory.CreateDirectory(Path.Combine(root, name));
            var failure = await Assert.ThrowsAsync<AggregateException>(() => new NativeSnapshotProvider(SnapshotKind.Btrfs, commands).SweepLeftoversAsync(root, default));
            Assert.Equal(2, failure.InnerExceptions.Count);
            Assert.Equal(2, commands.Calls.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SweepDoesNothingForVss()
    {
        var commands = new Commands();
        Assert.Equal(0, await new NativeSnapshotProvider(SnapshotKind.Vss, commands).SweepLeftoversAsync("/unused", default));
        Assert.Empty(commands.Calls);
    }

    private sealed class Commands : ISnapshotCommandRunner
    {
        public string? Fail { get; init; }
        public Dictionary<string, string> Outputs { get; } = [];
        public Action<string>? OnRun { get; set; }
        public List<(string File, string[] Args)> Calls { get; } = [];
        public Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, arguments.ToArray()));
            OnRun?.Invoke(fileName);
            if (fileName == Fail) throw new IOException("Command failed");
            return Task.FromResult(Outputs.GetValueOrDefault(fileName + " " + arguments[0]) ?? Outputs.GetValueOrDefault(fileName) ?? string.Empty);
        }
    }
}
