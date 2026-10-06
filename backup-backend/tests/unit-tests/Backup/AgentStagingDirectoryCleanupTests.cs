using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.UnitTests.Backup;

public sealed class AgentStagingDirectoryCleanupTests : IDisposable
{
    private readonly TempStaging _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void ListFilesReturnsRegularFilesWithSizeAndUtcLastWriteAndNothingElse()
    {
        _t.Write("a.bak", TimeSpan.FromDays(2), size: 10);
        _t.Write("b.bak", TimeSpan.FromHours(1), size: 20);
        Directory.CreateDirectory(_t.PathOf("folder"));
        File.WriteAllBytes(Path.Combine(_t.PathOf("folder"), "nested.bak"), new byte[3]);

        var files = _t.Staging.ListFiles().OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();

        Assert.Equal(["a.bak", "b.bak"], files.Select(file => file.Name));
        Assert.Equal([10L, 20L], files.Select(file => file.SizeBytes));
        Assert.Equal(TempStaging.Now - TimeSpan.FromDays(2), files[0].LastWriteUtc);
        Assert.Equal(TimeSpan.Zero, files[0].LastWriteUtc.Offset);
    }

    [Fact]
    public void ListFilesOfAnEmptyDirectoryIsEmpty() => Assert.Empty(_t.Staging.ListFiles());

    [Fact]
    public void ListFilesOfAMissingDirectoryThrowsTheIoExceptionForTheCallerToClassify()
    {
        using var missing = new TempStaging(createStagingDirectory: false);

        Assert.IsAssignableFrom<IOException>(Record.Exception(() => missing.Staging.ListFiles()));
    }

    [LinuxFact]
    public void ListFilesSkipsLinksOfEveryKind()
    {
        var target = _t.WriteOutside("target.bak", TimeSpan.FromDays(1));
        File.CreateSymbolicLink(_t.PathOf("link.bak"), target);
        File.CreateSymbolicLink(_t.PathOf("dangling.bak"), Path.Combine(_t.OutsidePath, "missing.bak"));
        Directory.CreateSymbolicLink(_t.PathOf("dirlink"), _t.OutsidePath);
        _t.Write("real.bak", TimeSpan.FromDays(1));

        Assert.Equal(["real.bak"], _t.Staging.ListFiles().Select(file => file.Name));
    }

    [Fact]
    public void AnIdleFileIsDeleted()
    {
        _t.Write("idle.bak", TimeSpan.FromDays(1));

        Assert.Equal(StagingDeleteOutcome.Deleted, _t.Staging.TryDeleteIdleFile("idle.bak"));
        Assert.False(_t.Exists("idle.bak"));
    }

    [Fact]
    public void AMissingFileIsNotFound() => Assert.Equal(StagingDeleteOutcome.NotFound, _t.Staging.TryDeleteIdleFile("gone.bak"));

    [Fact]
    public void AFileInAMissingDirectoryIsNotFound()
    {
        using var missing = new TempStaging(createStagingDirectory: false);

        Assert.Equal(StagingDeleteOutcome.NotFound, missing.Staging.TryDeleteIdleFile("gone.bak"));
    }

    [Fact]
    public void AFileSomebodyHoldsOpenExclusivelyIsInUseAndStaysUntilReleased()
    {
        var path = _t.Write("busy.bak", TimeSpan.FromDays(1));
        var staging = _t.Staging;

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(StagingDeleteOutcome.InUse, staging.TryDeleteIdleFile("busy.bak"));
            Assert.True(File.Exists(path));
        }

        Assert.Equal(StagingDeleteOutcome.Deleted, staging.TryDeleteIdleFile("busy.bak"));
    }

    [Fact]
    public void ADirectoryIsRefusedAndKept()
    {
        Directory.CreateDirectory(_t.PathOf("folder.bak"));

        Assert.Equal(StagingDeleteOutcome.Failed, _t.Staging.TryDeleteIdleFile("folder.bak"));
        Assert.True(Directory.Exists(_t.PathOf("folder.bak")));
    }

    [LinuxFact]
    public void ALinkIsRefusedAndNeitherItNorItsTargetIsRemoved()
    {
        var target = _t.WriteOutside("target.bak", TimeSpan.FromDays(1));
        File.CreateSymbolicLink(_t.PathOf("link.bak"), target);
        File.CreateSymbolicLink(_t.PathOf("dangling.bak"), Path.Combine(_t.OutsidePath, "missing.bak"));

        Assert.Equal(StagingDeleteOutcome.Failed, _t.Staging.TryDeleteIdleFile("link.bak"));
        Assert.NotEqual(StagingDeleteOutcome.Deleted, _t.Staging.TryDeleteIdleFile("dangling.bak"));
        Assert.True(File.Exists(target));
        Assert.True(_t.Exists("link.bak"));
        Assert.True(_t.Exists("dangling.bak"));
    }

    [LinuxFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void AFileInADirectoryThatCannotBeModifiedIsAccessDenied()
    {
        _t.Write("locked.bak", TimeSpan.FromDays(1));
        File.SetUnixFileMode(_t.StagingPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var probe = _t.PathOf("probe");
            try
            {
                File.WriteAllBytes(probe, [1]);
                File.Delete(probe);
                return; // running as a user the mode bits do not bind (root): the delete cannot be refused
            }
            catch (UnauthorizedAccessException)
            {
            }

            Assert.Equal(StagingDeleteOutcome.AccessDenied, _t.Staging.TryDeleteIdleFile("locked.bak"));
            Assert.True(_t.Exists("locked.bak"));
        }
        finally
        {
            File.SetUnixFileMode(_t.StagingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../x.bak")]
    [InlineData("sub/x.bak")]
    [InlineData("sub\\x.bak")]
    public void NamesThatAreNotSimpleNamesAreRejectedBeforeTouchingTheFileSystem(string name) =>
        Assert.ThrowsAny<ArgumentException>(() => _t.Staging.TryDeleteIdleFile(name));
}
