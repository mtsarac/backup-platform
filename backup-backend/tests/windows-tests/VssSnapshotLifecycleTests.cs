using BackupPlatform.SnapshotProviders.Abstractions;
using Xunit;

namespace BackupPlatform.Agent.Platform.Windows.Tests;

public sealed class VssSnapshotLifecycleTests
{
    [WindowsFact]
    public async Task VssViewIsReadableByDevicePathAndDeletesOnlyItsOwnedShadowCopy()
    {
        var commands = new Commands();
        var snapshot = await new NativeSnapshotProvider(SnapshotKind.Vss, commands)
            .CreateAsync(new SnapshotRequest("C:\\", "C:\\Snapshots", "attempt-1"), default);
        Assert.Equal("\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy42\\", snapshot.Path);
        await snapshot.DisposeAsync();
        await snapshot.DisposeAsync();
        Assert.Equal(1, commands.Deletes);
        Assert.Contains("11111111-2222-3333-4444-555555555555", commands.DeleteScript);
    }

    [WindowsFact]
    public async Task CancellationAfterCreationStillDeletesTheSnapshotBeforeReturning()
    {
        using var cancellation = new CancellationTokenSource();
        var commands = new Commands { Created = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NativeSnapshotProvider(SnapshotKind.Vss, commands)
            .CreateAsync(new SnapshotRequest("C:\\", "C:\\Snapshots", "attempt-2"), cancellation.Token));
        Assert.Equal(1, commands.Deletes);
    }

    private sealed class Commands : ISnapshotCommandRunner
    {
        public Action? Created { get; init; }
        public int Deletes { get; private set; }
        public string DeleteScript { get; private set; } = string.Empty;
        public Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token)
        {
            if (arguments[^1].Contains(".Create(", StringComparison.Ordinal))
            {
                Created?.Invoke();
                return Task.FromResult("""{"ID":"{11111111-2222-3333-4444-555555555555}","DeviceObject":"\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy42"}""");
            }
            Deletes++;
            DeleteScript = arguments[^1];
            return Task.FromResult(string.Empty);
        }
    }
}
