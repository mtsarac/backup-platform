using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Host;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests;

public sealed class StagingCleanupHostedServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bp_agent_cleanup_" + Guid.NewGuid().ToString("N")[..10]);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly StagingUsageRegistry _registry = new();
    private readonly CaptureLoggerProvider _logs = new();
    private readonly StagingCleanupOptions _options = new() { MinimumAge = TimeSpan.FromHours(12), ScanInterval = TimeSpan.FromHours(1) };

    private string StagingPath => Path.Combine(_root, "staging");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AgentStagingCleanupHostedService CreateService(StagingCleanupOptions? options = null)
    {
        var factory = _logs.CreateFactory();
        var cleaner = new StagingCleaner(
            new AgentStagingDirectory(new AgentPath(StagingPath)), _registry, _time, factory.CreateLogger<StagingCleaner>());
        return new AgentStagingCleanupHostedService(cleaner, options ?? _options, _time, factory.CreateLogger<AgentStagingCleanupHostedService>());
    }

    private string WriteOldManagedFile(string database)
    {
        Directory.CreateDirectory(StagingPath);
        var name = ManagedBackupLayout.StagedFileName(database, Start.AddDays(-30), Guid.NewGuid());
        var path = Path.Combine(StagingPath, name);
        File.WriteAllBytes(path, new byte[8]);
        File.SetLastWriteTimeUtc(path, Start.AddDays(-2).UtcDateTime);
        return path;
    }

    [Fact]
    public async Task ItSweepsOnceAfterTheInitialDelayAndThenEveryInterval()
    {
        var first = WriteOldManagedFile("first");
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        Assert.True(File.Exists(first), "nothing may be swept before the initial delay");

        // The host may start ExecuteAsync after StartAsync returned, so the initial delay's timer can appear after this point: keep moving the
        // fake clock past the delay until the sweep shows.
        await Wait.UntilAsync(
            () =>
            {
                _time.Advance(AgentStagingCleanupHostedService.InitialDelay);
                return !File.Exists(first);
            },
            "the first sweep");

        var second = WriteOldManagedFile("second");
        await Wait.UntilAsync(
            () =>
            {
                _time.Advance(_options.ScanInterval);
                return !File.Exists(second);
            },
            "the periodic sweep");

        await service.StopAsync(CancellationToken.None);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task AFilesHeldByARunningAttemptSurvivesEverySweepUntilItIsReleased()
    {
        var path = WriteOldManagedFile("busy");
        var hold = _registry.Hold(Path.GetFileName(path));
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        await Wait.UntilAsync(
            () =>
            {
                _time.Advance(AgentStagingCleanupHostedService.InitialDelay);
                return _logs.AllText.Contains("nothing to remove", StringComparison.Ordinal);
            },
            "the first sweep");

        Assert.True(File.Exists(path));

        hold.Dispose();
        await Wait.UntilAsync(
            () =>
            {
                _time.Advance(_options.ScanInterval);
                return !File.Exists(path);
            },
            "the sweep after the release");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFailedSweepIsLoggedAndTheNextOneStillRuns()
    {
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        await Wait.UntilAsync(
            () =>
            {
                _time.Advance(AgentStagingCleanupHostedService.InitialDelay);
                return _logs.Entries.Any(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("list-failed", StringComparison.Ordinal));
            },
            "the failed sweep to be reported");

        var path = WriteOldManagedFile("late");
        await Wait.UntilAsync(
            () =>
            {
                _time.Advance(_options.ScanInterval);
                return !File.Exists(path);
            },
            "the sweep after the directory appeared");

        await service.StopAsync(CancellationToken.None);
        Assert.DoesNotContain(_root, _logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADisabledCleanupNeverTouchesTheDirectoryAndTheServiceEndsAtOnce()
    {
        var path = WriteOldManagedFile("kept");
        var service = CreateService(_options with { Enabled = false });

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        _time.Advance(TimeSpan.FromDays(2));
        await service.StopAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task StoppingBeforeTheInitialDelayEndsCleanlyWithoutSweeping()
    {
        var path = WriteOldManagedFile("early");
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask!.IsCompleted && !service.ExecuteTask.IsFaulted, service.ExecuteTask.Status + " " + service.ExecuteTask.Exception);
        Assert.True(File.Exists(path));
    }
}
