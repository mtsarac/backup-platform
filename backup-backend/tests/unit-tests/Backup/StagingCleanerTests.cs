using System.Text.Json;
using BackupPlatform.Agent.Core.Backup;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.UnitTests.Backup;

public sealed class StagingCleanerTests : IDisposable
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly TempStaging _t = new();
    private readonly StagingUsageRegistry _registry = new();
    private readonly RecordingLogger<StagingCleaner> _log = new();
    private readonly StagingCleanupOptions _options = new() { MinimumAge = TimeSpan.FromHours(12) };

    public void Dispose() => _t.Dispose();

    private StagingCleaner Cleaner(TempStaging? staging = null) =>
        new((staging ?? _t).Staging, _registry, new FixedTimeProvider(TempStaging.Now), _log);

    private Task<StagingCleanupReport> RunAsync(CancellationToken cancellationToken = default) => Cleaner().RunAsync(_options, cancellationToken);

    [Fact]
    public void TheDefaultsAreEnabledTwelveHoursAndHourly()
    {
        var defaults = new StagingCleanupOptions();

        Assert.True(defaults.Enabled);
        Assert.Equal(TimeSpan.FromHours(12), defaults.MinimumAge);
        Assert.Equal(TimeSpan.FromHours(1), defaults.ScanInterval);
    }

    [Fact]
    public async Task AnOldManagedFileIsRemovedAndReportedByName()
    {
        var name = TempStaging.ManagedName();
        _t.Write(name, Day);

        var report = await RunAsync();

        Assert.False(_t.Exists(name));
        Assert.Equal(1, report.Scanned);
        Assert.Equal(1, report.Deleted);
        Assert.Equal([name], report.DeletedNames);
        Assert.Equal(0, report.Failed);
        Assert.Empty(report.Failures);
    }

    [Theory]
    [InlineData(11)]   // younger than the minimum age
    [InlineData(-72)]  // timestamp in the future
    public async Task AManagedFileNotOldEnoughIsKept(double ageHours)
    {
        var name = TempStaging.ManagedName();
        _t.Write(name, TimeSpan.FromHours(ageHours));

        var report = await RunAsync();

        Assert.True(_t.Exists(name));
        Assert.Equal(1, report.KeptYoung);
        Assert.Equal(0, report.Deleted);
    }

    [Fact]
    public async Task AFileHeldByACurrentAttemptIsKeptEvenWhenItIsAncient()
    {
        var name = TempStaging.ManagedName();
        _t.Write(name, TimeSpan.FromDays(400));
        var hold = _registry.Hold(name);

        var report = await RunAsync();

        Assert.True(_t.Exists(name));
        Assert.Equal(1, report.KeptInUse);
        Assert.Equal(0, report.Deleted);

        hold.Dispose();
        var after = await RunAsync();

        Assert.False(_t.Exists(name));
        Assert.Equal(1, after.Deleted);
    }

    [Fact]
    public async Task AHoldOfAnotherNameProtectsNothing()
    {
        var held = TempStaging.ManagedName();
        var orphan = TempStaging.ManagedName();
        _t.Write(held, Day);
        _t.Write(orphan, Day);
        using var hold = _registry.Hold(held);

        var report = await RunAsync();

        Assert.True(_t.Exists(held));
        Assert.False(_t.Exists(orphan));
        Assert.Equal([orphan], report.DeletedNames);
    }

    [Fact]
    public async Task FilesOutsideTheManagedGrammarAreNeverTouchedHoweverOldTheyAre()
    {
        string[] names =
        [
            "Sales_FULL.bak",
            "backup.bak",
            "Sales_20261005T053015Z_A1B2C3D4_FULL.bak",
            "Sales_20261005T053015Z_a1b2c3d4_FULL.bak.old",
            "notes.txt",
            ".hidden"
        ];
        foreach (var name in names)
        {
            _t.Write(name, TimeSpan.FromDays(3650));
        }

        var report = await RunAsync();

        Assert.All(names, name => Assert.True(_t.Exists(name), name));
        Assert.Equal(names.Length, report.Scanned);
        Assert.Equal(names.Length, report.Unrecognized);
        Assert.Equal(0, report.Deleted);
        Assert.Equal(0, report.Failed);
    }

    [Fact]
    public async Task ADirectoryWithAManagedLookingNameIsNeverTouched()
    {
        var name = TempStaging.ManagedName();
        Directory.CreateDirectory(_t.PathOf(name));
        var inner = Path.Combine(_t.PathOf(name), TempStaging.ManagedName());
        File.WriteAllBytes(inner, new byte[8]);
        File.SetLastWriteTimeUtc(inner, (TempStaging.Now - Day).UtcDateTime);
        Directory.SetLastWriteTimeUtc(_t.PathOf(name), (TempStaging.Now - Day).UtcDateTime);

        var report = await RunAsync();

        Assert.True(Directory.Exists(_t.PathOf(name)));
        Assert.True(File.Exists(inner));
        Assert.Equal(0, report.Scanned);
        Assert.Equal(0, report.Deleted);
        Assert.Equal(0, report.Failed);
    }

    [LinuxFact]
    public async Task LinksWithAManagedLookingNameAreNeverFollowedOrRemoved()
    {
        var target = _t.WriteOutside("operator.bak", TimeSpan.FromDays(3650));
        var linkedFile = TempStaging.ManagedName("fileLink");
        File.CreateSymbolicLink(_t.PathOf(linkedFile), target);

        var linkedDirectoryTarget = Path.Combine(_t.OutsidePath, "operator-dir");
        Directory.CreateDirectory(linkedDirectoryTarget);
        var inside = Path.Combine(linkedDirectoryTarget, TempStaging.ManagedName("inner"));
        File.WriteAllBytes(inside, new byte[4]);
        File.SetLastWriteTimeUtc(inside, (TempStaging.Now - Day).UtcDateTime);
        var linkedDirectory = TempStaging.ManagedName("dirLink");
        Directory.CreateSymbolicLink(_t.PathOf(linkedDirectory), linkedDirectoryTarget);

        var dangling = TempStaging.ManagedName("dangling");
        File.CreateSymbolicLink(_t.PathOf(dangling), Path.Combine(_t.OutsidePath, "missing.bak"));

        var report = await RunAsync();

        Assert.True(_t.Exists(linkedFile));
        Assert.True(_t.Exists(linkedDirectory));
        Assert.True(_t.Exists(dangling));
        Assert.True(File.Exists(target));
        Assert.True(File.Exists(inside));
        Assert.Equal(0, report.Deleted);
        Assert.Equal(0, report.Failed);
    }

    [Fact]
    public async Task AFileSomebodyHasOpenIsKeptAsInUse()
    {
        var name = TempStaging.ManagedName();
        var path = _t.Write(name, Day);
        var open = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var report = await RunAsync();

            Assert.True(_t.Exists(name));
            Assert.Equal(1, report.KeptInUse);
            Assert.Equal(0, report.Deleted);
            Assert.Equal(0, report.Failed);
        }
        finally
        {
            await open.DisposeAsync();
        }

        var after = await RunAsync();

        Assert.False(_t.Exists(name));
        Assert.Equal(1, after.Deleted);
    }

    [Fact]
    public async Task EveryKindOfFileIsCountedOnceInTheReport()
    {
        var old = TempStaging.ManagedName("old");
        var young = TempStaging.ManagedName("young");
        var held = TempStaging.ManagedName("held");
        var open = TempStaging.ManagedName("open");
        _t.Write(old, Day);
        _t.Write(young, TimeSpan.FromMinutes(5));
        _t.Write(held, Day);
        var openPath = _t.Write(open, Day);
        _t.Write("Sales_FULL.bak", Day);
        using var hold = _registry.Hold(held);
        using var stream = new FileStream(openPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var report = await RunAsync();

        Assert.Equal(5, report.Scanned);
        Assert.Equal(1, report.Deleted);
        Assert.Equal(1, report.KeptYoung);
        Assert.Equal(2, report.KeptInUse);
        Assert.Equal(1, report.Unrecognized);
        Assert.Equal(0, report.Failed);
        Assert.Equal([old], report.DeletedNames);
    }

    [Fact]
    public async Task ADisabledCleanupDoesNotTouchTheDirectory()
    {
        var name = TempStaging.ManagedName();
        _t.Write(name, TimeSpan.FromDays(100));

        var report = await Cleaner().RunAsync(_options with { Enabled = false }, CancellationToken.None);

        Assert.Same(StagingCleanupReport.Empty, report);
        Assert.True(_t.Exists(name));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task ADisabledCleanupDoesNotEvenLookForTheDirectory()
    {
        using var missing = new TempStaging(createStagingDirectory: false);

        var report = await Cleaner(missing).RunAsync(_options with { Enabled = false }, CancellationToken.None);

        Assert.Same(StagingCleanupReport.Empty, report);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task AMissingStagingDirectoryIsReportedAsAFailureCodeNotThrown()
    {
        using var missing = new TempStaging(createStagingDirectory: false);

        var report = await Cleaner(missing).RunAsync(_options, CancellationToken.None);

        Assert.Equal(1, report.Failed);
        Assert.Equal([new StagingCleanupFailure(StagingCleanupReasons.ListFailed, 1)], report.Failures);
        Assert.Equal(0, report.Scanned);
        AssertNoPathLeaked(report, missing.Root);
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("list-failed", StringComparison.Ordinal));
    }

    [LinuxFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task ADirectoryThatCannotBeListedIsReportedAsAFailureCodeNotThrown()
    {
        _t.Write(TempStaging.ManagedName(), Day);
        File.SetUnixFileMode(_t.StagingPath, UnixFileMode.None);
        try
        {
            if (CanStillList(_t.StagingPath))
            {
                return; // running as a user the mode bits do not bind (root): nothing to observe
            }

            var report = await RunAsync();

            Assert.Equal([new StagingCleanupFailure(StagingCleanupReasons.ListAccessDenied, 1)], report.Failures);
            Assert.Equal(1, report.Failed);
            AssertNoPathLeaked(report, _t.Root);
        }
        finally
        {
            File.SetUnixFileMode(_t.StagingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [LinuxFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task APermissionFailureIsReportedByCodeWithoutAnyPath()
    {
        var name = TempStaging.ManagedName();
        _t.Write(name, Day);
        File.SetUnixFileMode(_t.StagingPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (CanStillWrite(_t.StagingPath))
            {
                return; // running as a user the mode bits do not bind (root): the delete cannot be refused
            }

            var report = await RunAsync();

            Assert.True(_t.Exists(name));
            Assert.Equal(0, report.Deleted);
            Assert.Equal(1, report.Failed);
            Assert.Equal([new StagingCleanupFailure(StagingCleanupReasons.DeleteAccessDenied, 1)], report.Failures);
            AssertNoPathLeaked(report, _t.Root);
            Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("delete-access-denied", StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(_t.StagingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task ADeletionIsLoggedAtInformationByManagedNameAndNeverByPath()
    {
        var name = TempStaging.ManagedName();
        _t.Write(name, Day);

        await RunAsync();

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains(name, StringComparison.Ordinal));
        Assert.DoesNotContain(_t.Root, _log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), _log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APreCancelledTokenStopsBeforeAnyFileIsTouched()
    {
        var name = TempStaging.ManagedName();
        _t.Write(name, Day);

        await Assert.ThrowsAsync<OperationCanceledException>(() => RunAsync(new CancellationToken(canceled: true)));

        Assert.True(_t.Exists(name));
    }

    [Fact]
    public async Task CancellationBetweenFilesKeepsWhatWasDeletedAndLeavesTheRest()
    {
        // Oldest first: the sweep deletes "first", the log of that deletion cancels, and the next file is never looked at.
        var first = TempStaging.ManagedName("a");
        var second = TempStaging.ManagedName("b");
        var third = TempStaging.ManagedName("c");
        _t.Write(first, TimeSpan.FromDays(30));
        _t.Write(second, TimeSpan.FromDays(20));
        _t.Write(third, TimeSpan.FromDays(10));
        using var cancellation = new CancellationTokenSource();
        _log.OnLog = (level, message) =>
        {
            if (level == LogLevel.Information && message.Contains(first, StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => RunAsync(cancellation.Token));

        Assert.False(_t.Exists(first));
        Assert.True(_t.Exists(second));
        Assert.True(_t.Exists(third));
    }

    private static void AssertNoPathLeaked(StagingCleanupReport report, string root)
    {
        var text = JsonSerializer.Serialize(report);
        Assert.DoesNotContain(root, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), text, StringComparison.Ordinal);
    }

    private static bool CanStillList(string directory)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(directory).FirstOrDefault();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanStillWrite(string directory)
    {
        var probe = Path.Combine(directory, "probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(probe, [1]);
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
