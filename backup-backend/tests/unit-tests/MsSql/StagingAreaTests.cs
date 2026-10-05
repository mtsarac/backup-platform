using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class StagingAreaTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bp-unit-staging-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AgentStagingDirectory Staging() => new(new AgentPath(_root));

    [Fact]
    public void SameHostUsesOnePathOnBothSidesAndMappedKeepsThemApart()
    {
        var same = StagingArea.SameHost(MsSqlTestData.AgentAbsolute("/srv/stage"));
        var mapped = StagingArea.Mapped(new DatabaseServerPath("/var/opt/mssql/stage"), new AgentPath(MsSqlTestData.AgentAbsolute("/srv/stage")));

        Assert.Equal(StagingMappingKind.SameHost, same.Kind);
        Assert.Equal(same.ServerDirectory.Value, same.AgentDirectory.Value);
        Assert.Equal(StagingMappingKind.Mapped, mapped.Kind);
        Assert.NotEqual(mapped.ServerDirectory.Value, mapped.AgentDirectory.Value);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("")]
    [InlineData("/tmp/a\nb")]
    public void AgentPathsMustBeAbsoluteAndFreeOfControlCharacters(string value) =>
        Assert.ThrowsAny<ArgumentException>(() => new AgentPath(value));

    [Fact]
    public void InspectReportsAMissingDirectoryWithoutThrowing()
    {
        var access = new AgentStagingDirectory(new AgentPath(Path.Combine(_root, "missing"))).Inspect();

        Assert.False(access.DirectoryExists);
        Assert.False(access.CanList);
        Assert.False(access.CanWrite);
    }

    [Fact]
    public void InspectReportsAUsableDirectoryAndLeavesNoProbeFileBehind()
    {
        var access = Staging().Inspect();

        Assert.True(access.DirectoryExists);
        Assert.True(access.CanList);
        Assert.True(access.CanWrite);
        Assert.True(access.FreeBytes > 0);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void FilesAreHandledBySimpleNameOnly()
    {
        var staging = Staging();
        staging.CreateMarkerFile("a.bak");

        Assert.True(staging.FileExists("a.bak"));
        Assert.Equal(1, staging.FileLength("a.bak"));
        Assert.True(staging.EntryExists("a.bak"));
        Assert.Equal(Path.Combine(_root, "a.bak"), staging.FilePath("a.bak"));
        Assert.True(staging.TryDeleteFile("a.bak"));
        Assert.False(staging.FileExists("a.bak"));
        Assert.Null(staging.FileLength("a.bak"));
        Assert.True(staging.TryDeleteFile("a.bak"));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("a\0b")]
    [InlineData("c:x")]
    public void NamesWithDirectoryComponentsAreRefusedEverywhere(string name)
    {
        var staging = Staging();

        Assert.Throws<ArgumentException>(() => staging.FilePath(name));
        Assert.Throws<ArgumentException>(() => staging.EntryExists(name));
        Assert.Throws<ArgumentException>(() => staging.TryDeleteFile(name));
        Assert.Throws<ArgumentException>(() => staging.TryDeleteEmptyDirectory(name));
        Assert.Throws<ArgumentException>(() => staging.CreateMarkerFile(name));
    }

    [Fact]
    public void ACreatedMarkerCannotBeOverwritten()
    {
        var staging = Staging();
        staging.CreateMarkerFile("m.bak");

        Assert.ThrowsAny<IOException>(() => staging.CreateMarkerFile("m.bak"));
    }

    [Fact]
    public void EntryExistsSeesDirectoriesAndEmptyDirectoriesAreRemovable()
    {
        var staging = Staging();
        Directory.CreateDirectory(Path.Combine(_root, "bp_probe_x"));

        Assert.True(staging.EntryExists("bp_probe_x"));
        Assert.False(staging.FileExists("bp_probe_x"));
        Assert.True(staging.TryDeleteEmptyDirectory("bp_probe_x"));
        Assert.False(staging.EntryExists("bp_probe_x"));
        Assert.True(staging.TryDeleteEmptyDirectory("bp_probe_x"));
    }

    [Fact]
    public void ANonEmptyDirectoryIsNotRemovedAndSaysSo()
    {
        var staging = Staging();
        var directory = Directory.CreateDirectory(Path.Combine(_root, "full"));
        File.WriteAllText(Path.Combine(directory.FullName, "f"), "x");

        Assert.False(staging.TryDeleteEmptyDirectory("full"));
        Assert.True(Directory.Exists(directory.FullName));
    }

    [Theory]
    [InlineData("a.bak")]
    [InlineData("it's.bak")]
    public void RequestsAcceptSimpleNames(string name) =>
        Assert.Equal(name, new FullBackupRequest("Sales", StagingArea.SameHost(MsSqlTestData.AgentAbsolute("/tmp")), name).StagedFileName);

    [Theory]
    [InlineData("")]
    [InlineData("a/b.bak")]
    [InlineData("a\\b.bak")]
    [InlineData("c:b.bak")]
    [InlineData("..")]
    [InlineData("a\nb.bak")]
    public void RequestsRejectNamesThatAreNotSimple(string name) =>
        Assert.ThrowsAny<ArgumentException>(() => new FullBackupRequest("Sales", StagingArea.SameHost(MsSqlTestData.AgentAbsolute("/tmp")), name));

    [Fact]
    public void RequestTimeoutsAreFiniteWholeSecondsWithinBounds()
    {
        var request = new FullBackupRequest("Sales", StagingArea.SameHost(MsSqlTestData.AgentAbsolute("/tmp")), "a.bak", backupTimeout: TimeSpan.FromMilliseconds(1500));

        Assert.Equal(TimeSpan.FromSeconds(2), request.BackupTimeout);
        Assert.Equal(FullBackupRequest.DefaultVerifyTimeout, request.VerifyTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => new FullBackupRequest("Sales", StagingArea.SameHost(MsSqlTestData.AgentAbsolute("/tmp")), "a.bak", backupTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FullBackupRequest("Sales", StagingArea.SameHost(MsSqlTestData.AgentAbsolute("/tmp")), "a.bak", verifyTimeout: Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public void ResultsEnforceTheirInvariants()
    {
        var now = DateTimeOffset.UnixEpoch;
        var failure = new BackupFailure(BackupFailureKind.SqlBackupFailed, BackupExecutionStage.BackingUp, "r", "m");
        var cancelled = new BackupFailure(BackupFailureKind.Cancelled, BackupExecutionStage.BackingUp, "cancelled", "m");

        FullBackupResult Make(FullBackupStatus status, BackupFailure? f) =>
            new(status, f, null, null, now, now, null, null, null, BackupVerification.NotRun, [], StagingCleanup.NotNeeded);

        Assert.Equal(FullBackupStatus.Failed, Make(FullBackupStatus.Failed, failure).Status);
        Assert.Equal(FullBackupStatus.Cancelled, Make(FullBackupStatus.Cancelled, cancelled).Status);
        Assert.Throws<ArgumentException>(() => Make(FullBackupStatus.Failed, null));
        Assert.Throws<ArgumentException>(() => Make(FullBackupStatus.Failed, cancelled));
        Assert.Throws<ArgumentException>(() => Make(FullBackupStatus.Cancelled, failure));
        Assert.Throws<ArgumentException>(() => Make(FullBackupStatus.Succeeded, null));
    }
}
