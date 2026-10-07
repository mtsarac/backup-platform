using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Restore;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerRestoreCommandBuilderTests
{
    private static readonly DatabaseServerPath BackupFile = new("/var/opt/mssql/staging/restore_20261007_ab12.bak");

    private static RestoreFileMapping Move(string logical, string target, RestoreFileKind kind = RestoreFileKind.Data) =>
        new(logical, kind, "/source/data/" + logical + ".mdf", new DatabaseServerPath(target), 8 * 1024 * 1024);

    private static readonly IReadOnlyList<RestoreFileMapping> TwoMoves =
    [
        Move("Sales", "/var/opt/mssql/data/Sales_Sales_1.mdf"),
        Move("Sales_log", "/var/opt/mssql/data/Sales_Sales_log_1.ldf", RestoreFileKind.Log)
    ];

    [Fact]
    public void HeaderOnlyAndFileListOnlyQuoteTheServerPath()
    {
        Assert.Equal("RESTORE HEADERONLY FROM DISK = N'/var/opt/mssql/staging/restore_20261007_ab12.bak';", SqlServerRestoreCommandBuilder.BuildRestoreHeaderOnly(BackupFile));
        Assert.Equal("RESTORE FILELISTONLY FROM DISK = N'/var/opt/mssql/staging/restore_20261007_ab12.bak';", SqlServerRestoreCommandBuilder.BuildRestoreFileListOnly(BackupFile));
    }

    [Fact]
    public void ThePathCannotEndItsLiteralOrStartANewStatement()
    {
        var hostile = new DatabaseServerPath("/x'; DROP DATABASE [master]; --.bak");

        var header = SqlServerRestoreCommandBuilder.BuildRestoreHeaderOnly(hostile);

        Assert.Equal("RESTORE HEADERONLY FROM DISK = N'/x''; DROP DATABASE [master]; --.bak';", header);
    }

    [Theory]
    [InlineData("/var/a\nb.bak")]
    [InlineData("/var/a\0b.bak")]
    [InlineData("/var/a\rb.bak")]
    public void ControlCharactersInTheServerPathAreRefused(string path)
    {
        var file = new DatabaseServerPath(path);

        Assert.Throws<ArgumentException>(() => SqlServerRestoreCommandBuilder.BuildRestoreHeaderOnly(file));
        Assert.Throws<ArgumentException>(() => SqlServerRestoreCommandBuilder.BuildRestoreFileListOnly(file));
    }

    [Fact]
    public void AReplaceRestoreSpellsEveryMoveRecoveryChecksumReplaceAndStatsInOrder()
    {
        var sql = SqlServerRestoreCommandBuilder.BuildRestoreDatabase("Sales", BackupFile, TwoMoves, replace: true, withChecksum: true, stats: true);

        Assert.Equal(
            "RESTORE DATABASE [Sales] FROM DISK = N'/var/opt/mssql/staging/restore_20261007_ab12.bak' WITH "
            + "MOVE N'Sales' TO N'/var/opt/mssql/data/Sales_Sales_1.mdf', MOVE N'Sales_log' TO N'/var/opt/mssql/data/Sales_Sales_log_1.ldf', "
            + "RECOVERY, CHECKSUM, REPLACE, STATS = 1;",
            sql);
    }

    [Fact]
    public void ReplaceAppearsOnlyWhenAskedFor()
    {
        var replace = SqlServerRestoreCommandBuilder.BuildRestoreDatabase("Sales", BackupFile, TwoMoves, replace: true, withChecksum: false, stats: true);
        var fresh = SqlServerRestoreCommandBuilder.BuildRestoreDatabase("Sales", BackupFile, TwoMoves, replace: false, withChecksum: false, stats: true);

        Assert.Contains(", REPLACE", replace, StringComparison.Ordinal);
        Assert.DoesNotContain("REPLACE", fresh, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksumIsRequestedOnlyForABackupThatHasChecksums()
    {
        var with = SqlServerRestoreCommandBuilder.BuildRestoreDatabase("Sales", BackupFile, TwoMoves, replace: false, withChecksum: true, stats: true);
        var without = SqlServerRestoreCommandBuilder.BuildRestoreDatabase("Sales", BackupFile, TwoMoves, replace: false, withChecksum: false, stats: true);

        Assert.Contains(", CHECKSUM,", with, StringComparison.Ordinal);
        Assert.DoesNotContain("CHECKSUM", without, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryIsAlwaysRequestedAndStatsOnlyWhenAsked()
    {
        var quiet = SqlServerRestoreCommandBuilder.BuildRestoreDatabase("Sales", BackupFile, TwoMoves, replace: false, withChecksum: false, stats: false);
        var loud = SqlServerRestoreCommandBuilder.BuildRestoreDatabase("Sales", BackupFile, TwoMoves, replace: false, withChecksum: false, stats: true);

        Assert.Contains("RECOVERY", quiet, StringComparison.Ordinal);
        Assert.DoesNotContain("NORECOVERY", loud, StringComparison.Ordinal);
        Assert.DoesNotContain("STATS", quiet, StringComparison.Ordinal);
        Assert.EndsWith("RECOVERY, STATS = 1;", loud, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewDatabaseRestoreIsGuardedAgainstADatabaseThatAppearsMeanwhile()
    {
        var fresh = SqlServerRestoreCommandBuilder.BuildRestoreDatabase("Sales", BackupFile, TwoMoves, replace: false, withChecksum: false, stats: true);
        var replace = SqlServerRestoreCommandBuilder.BuildRestoreDatabase("Sales", BackupFile, TwoMoves, replace: true, withChecksum: false, stats: true);

        Assert.StartsWith("IF DB_ID(N'Sales') IS NOT NULL THROW 50001, N'target-database-exists', 1; RESTORE DATABASE [Sales]", fresh, StringComparison.Ordinal);
        Assert.StartsWith("RESTORE DATABASE [Sales]", replace, StringComparison.Ordinal);
        Assert.Equal(50001, SqlServerRestoreCommandBuilder.TargetExistsErrorNumber);
    }

    [Fact]
    public void AHostileDatabaseNameStaysInsideItsIdentifierAndItsGuardLiteral()
    {
        var sql = SqlServerRestoreCommandBuilder.BuildRestoreDatabase("a]; DROP DATABASE x; --'", BackupFile, TwoMoves, replace: false, withChecksum: false, stats: true);

        Assert.Contains("DB_ID(N'a]; DROP DATABASE x; --''')", sql, StringComparison.Ordinal);
        Assert.Contains("RESTORE DATABASE [a]]; DROP DATABASE x; --'] FROM", sql, StringComparison.Ordinal);
        Assert.Equal("ALTER DATABASE [a]]; DROP DATABASE x; --'] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", SqlServerRestoreCommandBuilder.BuildSetSingleUser("a]; DROP DATABASE x; --'"));
        Assert.Equal("ALTER DATABASE [a]]; DROP DATABASE x; --'] SET MULTI_USER;", SqlServerRestoreCommandBuilder.BuildSetMultiUser("a]; DROP DATABASE x; --'"));
    }

    [Fact]
    public void HostileLogicalNamesAndTargetPathsStayInsideTheirLiterals()
    {
        var moves = new[] { Move("x'; DROP DATABASE [master]; --", "/data/it's.mdf") };

        var sql = SqlServerRestoreCommandBuilder.BuildRestoreDatabase("Sales", BackupFile, moves, replace: true, withChecksum: false, stats: true);

        Assert.Contains("MOVE N'x''; DROP DATABASE [master]; --' TO N'/data/it''s.mdf'", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bad\nname", "Sales_1.mdf")]
    [InlineData("Sales", "/data/a\nb.mdf")]
    [InlineData("Sa\0les", "/data/a.mdf")]
    public void ControlCharactersInALogicalNameOrAPathAreRefused(string logical, string path)
    {
        var moves = new[] { Move(logical, path) };

        Assert.Throws<ArgumentException>(() =>
            SqlServerRestoreCommandBuilder.BuildRestoreDatabase("Sales", BackupFile, moves, replace: false, withChecksum: false, stats: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\nname")]
    public void ControlCharactersAndEmptyNamesAreRefusedForTheDatabase(string name)
    {
        Assert.Throws<ArgumentException>(() => SqlServerRestoreCommandBuilder.BuildRestoreDatabase(name, BackupFile, TwoMoves, replace: false, withChecksum: false, stats: true));
        Assert.Throws<ArgumentException>(() => SqlServerRestoreCommandBuilder.BuildSetSingleUser(name));
        Assert.Throws<ArgumentException>(() => SqlServerRestoreCommandBuilder.BuildSetMultiUser(name));
    }

    [Fact]
    public void ANameOverTheSysnameLimitIsRefused() =>
        Assert.Throws<ArgumentException>(() =>
            SqlServerRestoreCommandBuilder.BuildRestoreDatabase(new string('a', 129), BackupFile, TwoMoves, replace: false, withChecksum: false, stats: true));

    [Fact]
    public void ARestoreWithoutAnyMoveIsRefusedBecauseTheFilesWouldGoBackToTheSourcePaths() =>
        Assert.Throws<ArgumentException>(() =>
            SqlServerRestoreCommandBuilder.BuildRestoreDatabase("Sales", BackupFile, [], replace: false, withChecksum: false, stats: true));

    [Fact]
    public void SetMultiUserAndSetSingleUserAreTheOnlyAccessChanges()
    {
        Assert.Equal("ALTER DATABASE [Sales] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", SqlServerRestoreCommandBuilder.BuildSetSingleUser("Sales"));
        Assert.Equal("ALTER DATABASE [Sales] SET MULTI_USER;", SqlServerRestoreCommandBuilder.BuildSetMultiUser("Sales"));
    }
}
