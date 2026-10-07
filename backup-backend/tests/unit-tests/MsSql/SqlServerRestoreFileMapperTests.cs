using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.Restore;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerRestoreFileMapperTests
{
    private static readonly SqlServerTargetDirectories LinuxDirectories = new(new DatabaseServerPath("/var/opt/mssql/data"), new DatabaseServerPath("/var/opt/mssql/log"), false, false);
    private static readonly SqlServerTargetDirectories WindowsDirectories = new(new DatabaseServerPath(@"D:\SQLData"), new DatabaseServerPath(@"E:\SQLLogs\"), false, false);

    private static RestoreBackupFile Data(string logical, string? source = null, string? group = "PRIMARY") =>
        new(logical, source ?? $@"C:\Source\{logical}.mdf", RestoreFileKind.Data, group, 8 * 1024 * 1024);

    private static RestoreBackupFile Log(string logical, string? source = null) =>
        new(logical, source ?? $@"C:\Source\{logical}.ldf", RestoreFileKind.Log, null, 4 * 1024 * 1024);

    private static SqlServerMasterFile Existing(string database, string logical, string path, RestoreFileKind kind = RestoreFileKind.Data, long size = 0) =>
        new(database, 7, 1, kind, logical, path, size);

    private static SqlServerRestoreMappingResult Map(
        IReadOnlyList<RestoreBackupFile> files,
        string database = "Sales",
        SqlServerTargetDirectories? directories = null,
        SqlServerHostPlatform platform = SqlServerHostPlatform.Linux,
        bool replace = false,
        IReadOnlyList<SqlServerMasterFile>? targetFiles = null,
        IReadOnlyList<SqlServerMasterFile>? allFiles = null) =>
        SqlServerRestoreFileMapper.Map(new SqlServerRestoreMappingInput(
            database, files, directories ?? LinuxDirectories, platform, replace, targetFiles ?? [], allFiles ?? targetFiles ?? []));

    private static string[] Paths(SqlServerRestoreMappingResult result) => result.Mappings!.Select(mapping => mapping.TargetPath.Value).ToArray();

    [Fact]
    public void LinuxFilesLandInTheTargetsDataAndLogDirectoriesWithTheDocumentedNames()
    {
        var result = Map([Data("Sales"), Data("Sales_2", group: "Archive"), Log("Sales_log")]);

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["/var/opt/mssql/data/Sales_Sales_1.mdf", "/var/opt/mssql/data/Sales_Sales_2_1.ndf", "/var/opt/mssql/log/Sales_Sales_log_1.ldf"],
            Paths(result));
        Assert.Equal(0, result.ReusedFiles);
    }

    [Fact]
    public void WindowsFilesUseBackslashesAndTheTrailingSeparatorOfTheDirectoryIsNotDoubled()
    {
        var result = Map([Data("Sales"), Log("Sales_log")], directories: WindowsDirectories, platform: SqlServerHostPlatform.Windows);

        Assert.Equal([@"D:\SQLData\Sales_Sales_1.mdf", @"E:\SQLLogs\Sales_Sales_log_1.ldf"], Paths(result));
    }

    [Fact]
    public void MappingsKeepLogicalNameKindSourcePathSizeAndFileGroup()
    {
        var result = Map([Data("Sales", group: "PRIMARY"), Log("Sales_log")]);

        var data = result.Mappings![0];
        Assert.Equal("Sales", data.LogicalName);
        Assert.Equal(RestoreFileKind.Data, data.Kind);
        Assert.Equal(@"C:\Source\Sales.mdf", data.SourcePhysicalName);
        Assert.Equal(8 * 1024 * 1024, data.SizeBytes);
        Assert.Equal("PRIMARY", data.FileGroupName);
        Assert.Equal(RestoreFileKind.Log, result.Mappings[1].Kind);
    }

    [Fact]
    public void ADestinationIsNeverTheSourcePathEvenWhenTheSourceUsesTheGeneratedName()
    {
        var source = "/var/opt/mssql/data/Sales_Sales_1.mdf";

        var result = Map([Data("Sales", source), Log("Sales_log", "/var/opt/mssql/log/Sales_Sales_log_1.ldf")]);

        Assert.Equal(["/var/opt/mssql/data/Sales_Sales_2.mdf", "/var/opt/mssql/log/Sales_Sales_log_2.ldf"], Paths(result));
    }

    [Fact]
    public void ADestinationAnotherDatabaseOnTheTargetAlreadyOwnsIsSkippedWhateverTheCase()
    {
        var taken = new[]
        {
            Existing("Other", "x", "/var/opt/mssql/data/Sales_Sales_1.mdf"),
            Existing("Other", "y", "/var/opt/mssql/data/SALES_SALES_2.MDF")
        };

        var result = Map([Data("Sales")], allFiles: taken);

        Assert.Equal(["/var/opt/mssql/data/Sales_Sales_3.mdf"], Paths(result));
    }

    [Fact]
    public void NamesThatSanitizeToTheSameTextStillGetDistinctDestinations()
    {
        var result = Map([Data("a b"), Data("a.b"), Data("a/b"), Data("a_b")]);

        var paths = Paths(result);
        Assert.Equal(4, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            [
                "/var/opt/mssql/data/Sales_a_b_1.mdf",
                "/var/opt/mssql/data/Sales_a_b_1.ndf",
                "/var/opt/mssql/data/Sales_a_b_2.ndf",
                "/var/opt/mssql/data/Sales_a_b_3.ndf"
            ],
            paths);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData(@"..\..\Windows\System32\x")]
    [InlineData("C:\\boot.ini")]
    [InlineData("a;b'c\"d")]
    [InlineData("data:stream")]
    [InlineData("CON")]
    [InlineData("名前")]
    public void NoDatabaseOrLogicalNameCanProduceASeparatorADotSegmentOrAStreamMarker(string hostile)
    {
        var result = Map([Data(hostile), Log(hostile + "_log")], database: hostile);

        Assert.True(result.Succeeded);
        foreach (var mapping in result.Mappings!)
        {
            var name = mapping.TargetPath.Value[(mapping.TargetPath.Value.LastIndexOf('/') + 1)..];
            Assert.Matches("^[A-Za-z0-9_-]+_[1-9][0-9]*\\.(mdf|ndf|ldf)$", name);
            Assert.StartsWith(mapping.Kind == RestoreFileKind.Log ? "/var/opt/mssql/log/" : "/var/opt/mssql/data/", mapping.TargetPath.Value, StringComparison.Ordinal);
            // Five separators: the four of the directory and the one before the file name; nothing the names contain adds one.
            Assert.Equal(5, mapping.TargetPath.Value.Count(character => character == '/'));
        }
    }

    [Fact]
    public void LongNamesAreBoundedSoThePathStaysShort()
    {
        var result = Map([Data(new string('x', 300)), Log(new string('y', 300))], database: new string('d', 128));

        foreach (var mapping in result.Mappings!)
        {
            var name = mapping.TargetPath.Value[(mapping.TargetPath.Value.LastIndexOf('/') + 1)..];
            Assert.True(name.Length <= (2 * SqlServerRestoreFileMapper.MaxNamePartLength) + 2 + 3 + 4, name);
        }
    }

    [Fact]
    public void SanitizeKeepsOnlyLettersDigitsUnderscoreAndHyphenAndNeverReturnsNothing()
    {
        Assert.Equal("Sales_Data-1", SqlServerRestoreFileMapper.Sanitize("Sales_Data-1"));
        Assert.Equal("a_b_c_d", SqlServerRestoreFileMapper.Sanitize("a b/c.d"));
        Assert.Equal("__", SqlServerRestoreFileMapper.Sanitize("名前"));
        Assert.Equal("_", SqlServerRestoreFileMapper.Sanitize(string.Empty));
        Assert.Equal(SqlServerRestoreFileMapper.MaxNamePartLength, SqlServerRestoreFileMapper.Sanitize(new string('a', 500)).Length);
    }

    [Theory]
    [InlineData(RestoreFileKind.FileStream, "FILESTREAM")]
    [InlineData(RestoreFileKind.FullText, "full-text")]
    [InlineData(RestoreFileKind.Other, "unknown type")]
    public void UnsupportedFileTypesAreRefusedWithAFixedSentence(RestoreFileKind kind, string expectedFragment)
    {
        var result = Map([Data("Sales"), new RestoreBackupFile("Extra", "/x/extra", kind, "FG", 1)]);

        Assert.False(result.Succeeded);
        Assert.Contains(expectedFragment, result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Extra", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("/x/extra", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ABackupWithoutFilesWithDuplicateNamesOrWithoutADataFileIsRefused()
    {
        Assert.False(Map([]).Succeeded);
        Assert.False(Map([Data("Sales"), Data("SALES")]).Succeeded);
        Assert.False(Map([Log("Sales_log")]).Succeeded);
        Assert.Null(SqlServerRestoreFileMapper.RejectUnsupportedFiles([Data("Sales"), Log("Sales_log")]));
    }

    [Theory]
    [InlineData(SqlServerHostPlatform.Windows)]
    [InlineData(SqlServerHostPlatform.Linux)]
    public void DirectoriesInTheWrongStyleForTheServersPlatformAreRefused(SqlServerHostPlatform platform)
    {
        var wrong = platform == SqlServerHostPlatform.Windows ? LinuxDirectories : WindowsDirectories;

        var result = Map([Data("Sales")], directories: wrong, platform: platform);

        Assert.False(result.Succeeded);
        Assert.Contains("operating system", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownPlatformAcceptsTheStyleOfTheDirectories()
    {
        Assert.True(Map([Data("Sales")], directories: LinuxDirectories, platform: SqlServerHostPlatform.Unknown).Succeeded);
        Assert.True(Map([Data("Sales")], directories: WindowsDirectories, platform: SqlServerHostPlatform.Unknown).Succeeded);
    }

    [Fact]
    public void ADirectoryThatLeavesNoRoomForANameIsRefused()
    {
        var deep = new SqlServerTargetDirectories(new DatabaseServerPath("/" + new string('a', 250)), new DatabaseServerPath("/log"), false, false);

        var result = Map([Data("Sales"), Log("Sales_log")], directories: deep);

        Assert.False(result.Succeeded);
        Assert.Contains("too long", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacingReusesTheTargetsOwnFileForTheSameLogicalNameAndType()
    {
        var target = new[]
        {
            Existing("Sales", "Sales", "/mnt/disk2/sales/Sales.mdf", RestoreFileKind.Data, 1000),
            Existing("Sales", "Sales_log", "/mnt/disk3/log/Sales_log.ldf", RestoreFileKind.Log, 500)
        };

        var result = Map([Data("sales"), Log("SALES_LOG")], replace: true, targetFiles: target);

        Assert.Equal(["/mnt/disk2/sales/Sales.mdf", "/mnt/disk3/log/Sales_log.ldf"], Paths(result));
        Assert.Equal(2, result.ReusedFiles);
    }

    [Fact]
    public void ReplacingGeneratesAPathForAFileTheTargetDoesNotHaveOrHasWithAnotherType()
    {
        var target = new[]
        {
            Existing("Sales", "Sales", "/mnt/disk2/sales/Sales.mdf"),
            // Same logical name as a backup data file but a log file in the target: never reused.
            Existing("Sales", "Sales_2", "/mnt/disk3/log/Sales_2.ldf", RestoreFileKind.Log)
        };

        var result = Map([Data("Sales"), Data("Sales_2", group: "FG2")], replace: true, targetFiles: target, allFiles: target);

        Assert.Equal(["/mnt/disk2/sales/Sales.mdf", "/var/opt/mssql/data/Sales_Sales_2_1.ndf"], Paths(result));
        Assert.Equal(1, result.ReusedFiles);
    }

    [Fact]
    public void ReplacingTheSourceDatabaseItselfReusesItsOwnPathsEvenThoughTheyAreTheSourcePaths()
    {
        var path = "/var/opt/mssql/data/Sales.mdf";
        var target = new[] { Existing("Sales", "Sales", path) };

        var result = Map([Data("Sales", path)], replace: true, targetFiles: target);

        Assert.Equal([path], Paths(result));
    }

    [Fact]
    public void ANewDatabaseNeverReusesPathsEvenWhenTargetFilesAreSupplied()
    {
        var target = new[] { Existing("Sales", "Sales", "/mnt/disk2/sales/Sales.mdf") };

        var result = Map([Data("Sales")], replace: false, targetFiles: target);

        Assert.Equal(["/var/opt/mssql/data/Sales_Sales_1.mdf"], Paths(result));
        Assert.Equal(0, result.ReusedFiles);
    }

    [Fact]
    public void AGeneratedNameNeverCollidesWithAReusedPath()
    {
        // The target's own file for "Sales" happens to sit where the generated name for "Other" would go.
        var target = new[] { Existing("Sales", "Sales", "/var/opt/mssql/data/Sales_Other_1.ndf") };

        var result = Map([Data("Sales"), Data("Other", group: "FG2")], replace: true, targetFiles: target);

        Assert.Equal(["/var/opt/mssql/data/Sales_Other_1.ndf", "/var/opt/mssql/data/Sales_Other_2.ndf"], Paths(result));
        Assert.Equal(2, Paths(result).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ManyFilesAllGetUniqueDestinationsAndTheResultFitsARestorePlan()
    {
        var files = Enumerable.Range(0, 60).Select(i => Data($"File {i}", group: "PRIMARY")).Concat([Log("Log 1"), Log("Log.1")]).ToArray();

        var result = Map(files);

        Assert.True(result.Succeeded);
        Assert.Equal(62, Paths(result).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var target = new RestoreTargetInfo(
            new DatabaseInstanceInfo(DatabaseEngineId.MsSql, "SRV", null, "16.0", null, "Linux"), LinuxDirectories.Data, LinuxDirectories.Log, false, null);
        Assert.Equal(62, new RestorePlan(result.Mappings!, target).Mappings.Count);
    }

    [Fact]
    public void TheFirstDataFileGetsMdfAndTheOthersNdf()
    {
        var result = Map([Data("A"), Data("B"), Data("C"), Log("L")]);

        Assert.Equal(["mdf", "ndf", "ndf", "ldf"], Paths(result).Select(path => path[(path.LastIndexOf('.') + 1)..]));
    }

    [Theory]
    [InlineData("/var/opt/mssql/data/", "/var/opt/mssql/data")]
    [InlineData("/var/opt/mssql/data", "/var/opt/mssql/data")]
    [InlineData(@"D:\SQL Data\", @"D:\SQL Data")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData("/", "/")]
    [InlineData(@"\\server\share\data", @"\\server\share\data")]
    public void UsableNormalizesAnAbsoluteDirectory(string value, string expected) => Assert.Equal(expected, SqlServerTargetDirectoryResolver.Usable(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("data/relative")]
    [InlineData("D:")]
    [InlineData("/var/../etc")]
    [InlineData("/var/a\nb")]
    public void UsableRefusesRelativeTraversingAndControlCharacterDirectories(string? value) => Assert.Null(SqlServerTargetDirectoryResolver.Usable(value));

    [Theory]
    [InlineData("/var/opt/mssql/data/master.mdf", "/var/opt/mssql/data")]
    [InlineData(@"C:\Program Files\MSSQL\DATA\master.mdf", @"C:\Program Files\MSSQL\DATA")]
    [InlineData("/master.mdf", "/")]
    [InlineData(@"C:\master.mdf", @"C:\")]
    [InlineData("master.mdf", null)]
    [InlineData(null, null)]
    public void DirectoryOfTakesTheDirectoryPartInEitherStyle(string? file, string? expected) => Assert.Equal(expected, SqlServerTargetDirectoryResolver.DirectoryOf(file));

    [Fact]
    public void ResolveUsesTheInstanceDefaultsWhenReportedAndFlagsNothingAsFallback()
    {
        var resolved = SqlServerTargetDirectoryResolver.Resolve(new SqlServerRawDirectories("/var/opt/mssql/data/", "/var/opt/mssql/log/", "/m/master.mdf", "/m/mastlog.ldf"));

        Assert.Equal("/var/opt/mssql/data", resolved!.Data.Value);
        Assert.Equal("/var/opt/mssql/log", resolved.Log.Value);
        Assert.False(resolved.DataIsFallback);
        Assert.False(resolved.LogIsFallback);
    }

    [Fact]
    public void ResolveFallsBackToTheDirectoriesOfMastersFiles()
    {
        var resolved = SqlServerTargetDirectoryResolver.Resolve(new SqlServerRawDirectories(null, null, @"C:\MSSQL\DATA\master.mdf", @"C:\MSSQL\LOG\mastlog.ldf"));

        Assert.Equal(@"C:\MSSQL\DATA", resolved!.Data.Value);
        Assert.Equal(@"C:\MSSQL\LOG", resolved.Log.Value);
        Assert.True(resolved.DataIsFallback);
        Assert.True(resolved.LogIsFallback);
    }

    [Fact]
    public void ResolveUsesTheDataDirectoryForTheLogWhenNothingKnowsTheLogDirectory()
    {
        var resolved = SqlServerTargetDirectoryResolver.Resolve(new SqlServerRawDirectories("/data", null, null, null));

        Assert.Equal("/data", resolved!.Log.Value);
        Assert.True(resolved.LogIsFallback);
        Assert.False(resolved.DataIsFallback);
    }

    [Fact]
    public void ResolveIsNullWhenNoUsableDataDirectoryExists()
    {
        Assert.Null(SqlServerTargetDirectoryResolver.Resolve(new SqlServerRawDirectories(null, null, null, null)));
        Assert.Null(SqlServerTargetDirectoryResolver.Resolve(new SqlServerRawDirectories("relative/dir", null, "master.mdf", null)));
    }
}
