using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerBackupCommandBuilderTests
{
    private static readonly DatabaseServerPath File = new("/var/opt/mssql/bp-staging/a.bak");

    [Theory]
    [InlineData("Sales", "[Sales]")]
    [InlineData("my db", "[my db]")]
    [InlineData("x]; DROP DATABASE y;--", "[x]]; DROP DATABASE y;--]")]
    [InlineData("a]]b", "[a]]]]b]")]
    [InlineData("it's", "[it's]")]
    [InlineData("]", "[]]]")]
    [InlineData("Ünïcødé 数据库", "[Ünïcødé 数据库]")]
    public void IdentifiersAreBracketQuotedWithClosingBracketsDoubled(string name, string expected) =>
        Assert.Equal(expected, SqlServerBackupCommandBuilder.QuoteIdentifier(name));

    [Fact]
    public void AnIdentifierOfExactlyTheMaximumLengthIsAccepted() =>
        Assert.Equal(130, SqlServerBackupCommandBuilder.QuoteIdentifier(new string('a', 128)).Length);

    [Theory]
    [InlineData("")]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb")]
    [InlineData("a\u007fb")]
    public void IdentifiersRejectEmptyAndControlCharacters(string name) =>
        Assert.Throws<ArgumentException>(() => SqlServerBackupCommandBuilder.QuoteIdentifier(name));

    [Fact]
    public void IdentifiersLongerThanSysnameAreRejected() =>
        Assert.Throws<ArgumentException>(() => SqlServerBackupCommandBuilder.QuoteIdentifier(new string('a', 129)));

    [Theory]
    [InlineData("/var/opt/mssql/a.bak", "N'/var/opt/mssql/a.bak'")]
    [InlineData("it's", "N'it''s'")]
    [InlineData("''", "N''''''")]
    [InlineData("';--", "N''';--'")]
    [InlineData("'; DROP DATABASE y;--", "N'''; DROP DATABASE y;--'")]
    [InlineData("D:\\Back ups\\数据.bak", "N'D:\\Back ups\\数据.bak'")]
    [InlineData("", "N''")]
    public void StringLiteralsAreUnicodeQuotedWithSingleQuotesDoubled(string value, string expected) =>
        Assert.Equal(expected, SqlServerBackupCommandBuilder.QuoteStringLiteral(value));

    [Fact]
    public void AVeryLongStringLiteralIsQuotedWithoutTruncation()
    {
        var value = new string('x', 100_000) + "'";

        var literal = SqlServerBackupCommandBuilder.QuoteStringLiteral(value);

        Assert.Equal(100_000 + 2 + 3, literal.Length);
        Assert.EndsWith("x'''", literal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\u0085b")]
    public void StringLiteralsRejectControlCharactersSoAPathCanNeverBreakOutOrBeTruncated(string value) =>
        Assert.Throws<ArgumentException>(() => SqlServerBackupCommandBuilder.QuoteStringLiteral(value));

    [Theory]
    [InlineData(false, false, false, false, false, "BACKUP DATABASE [Sales] TO DISK = N'/var/opt/mssql/bp-staging/a.bak' WITH INIT, NAME = N'BackupPlatform full backup', NO_CHECKSUM;")]
    [InlineData(false, false, false, false, true, "BACKUP DATABASE [Sales] TO DISK = N'/var/opt/mssql/bp-staging/a.bak' WITH INIT, NAME = N'BackupPlatform full backup', NO_CHECKSUM, STATS = 1;")]
    [InlineData(true, false, false, false, false, "BACKUP DATABASE [Sales] TO DISK = N'/var/opt/mssql/bp-staging/a.bak' WITH INIT, NAME = N'BackupPlatform full backup', COPY_ONLY, NO_CHECKSUM;")]
    [InlineData(false, true, false, false, false, "BACKUP DATABASE [Sales] TO DISK = N'/var/opt/mssql/bp-staging/a.bak' WITH INIT, NAME = N'BackupPlatform full backup', CHECKSUM;")]
    [InlineData(false, false, false, true, false, "BACKUP DATABASE [Sales] TO DISK = N'/var/opt/mssql/bp-staging/a.bak' WITH INIT, NAME = N'BackupPlatform full backup', NO_CHECKSUM, NO_COMPRESSION;")]
    [InlineData(false, false, true, true, false, "BACKUP DATABASE [Sales] TO DISK = N'/var/opt/mssql/bp-staging/a.bak' WITH INIT, NAME = N'BackupPlatform full backup', NO_CHECKSUM, COMPRESSION;")]
    [InlineData(true, true, true, true, true, "BACKUP DATABASE [Sales] TO DISK = N'/var/opt/mssql/bp-staging/a.bak' WITH INIT, NAME = N'BackupPlatform full backup', COPY_ONLY, CHECKSUM, COMPRESSION, STATS = 1;")]
    [InlineData(true, true, false, false, true, "BACKUP DATABASE [Sales] TO DISK = N'/var/opt/mssql/bp-staging/a.bak' WITH INIT, NAME = N'BackupPlatform full backup', COPY_ONLY, CHECKSUM, STATS = 1;")]
    public void FullBackupSpellsOutEveryOptionDecision(bool copyOnly, bool checksum, bool compression, bool compressionSupported, bool stats, string expected) =>
        Assert.Equal(expected, SqlServerBackupCommandBuilder.BuildFullBackup("Sales", File, new AppliedFullBackupOptions(copyOnly, checksum, compression), compressionSupported, stats));

    [Fact]
    public void ADatabaseNameAndPathThatTryToInjectStayInsideTheirQuotes()
    {
        var path = new DatabaseServerPath("/tmp/x'; DROP DATABASE y;--.bak");

        var sql = SqlServerBackupCommandBuilder.BuildFullBackup("x]; DROP DATABASE y;--", path, new AppliedFullBackupOptions(false, false, false), false, false);

        Assert.StartsWith("BACKUP DATABASE [x]]; DROP DATABASE y;--] TO DISK = N'/tmp/x''; DROP DATABASE y;--.bak' WITH INIT", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/bp-staging/a.bak' WITH CHECKSUM;")]
    [InlineData(false, "RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/bp-staging/a.bak';")]
    public void VerifyOnlyUsesChecksumsOnlyWhenTheBackupHasThem(bool checksum, string expected) =>
        Assert.Equal(expected, SqlServerBackupCommandBuilder.BuildVerifyOnly(File, checksum));

    [Theory]
    [InlineData("/var/opt/mssql/bp-staging", "x.bak", "/var/opt/mssql/bp-staging/x.bak")]
    [InlineData("/var/opt/mssql/bp-staging/", "x.bak", "/var/opt/mssql/bp-staging/x.bak")]
    [InlineData(@"D:\Staging", "x.bak", @"D:\Staging\x.bak")]
    [InlineData(@"D:\Staging\", "x.bak", @"D:\Staging\x.bak")]
    [InlineData("E:/Staging", "x.bak", @"E:/Staging\x.bak")]
    [InlineData(@"\\fileserver\share\dir", "x.bak", @"\\fileserver\share\dir\x.bak")]
    public void ServerPathsAreCombinedWithTheSeparatorOfTheirOwnStyle(string directory, string file, string expected) =>
        Assert.Equal(expected, new DatabaseServerPath(directory).Combine(file).Value);

    [Theory]
    [InlineData(@"D:\Staging", true)]
    [InlineData("d:/Staging", true)]
    [InlineData(@"\\fileserver\share", true)]
    [InlineData("/var/opt/mssql", false)]
    [InlineData("relative", false)]
    [InlineData("D:", false)]
    public void WindowsStyleIsDrivePathsAndUncOnly(string path, bool expected) =>
        Assert.Equal(expected, new DatabaseServerPath(path).IsWindowsStyle);

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("c:x")]
    [InlineData("..")]
    [InlineData("a\0b")]
    public void CombineRefusesNamesThatAreNotSimple(string name) =>
        Assert.Throws<ArgumentException>(() => new DatabaseServerPath("/var/opt/mssql").Combine(name));

    [Fact]
    public void TheProbeCommandQuotesItsDirectoryAsALiteral() =>
        Assert.Equal(
            "EXEC master.sys.xp_create_subdir N'/var/it''s/bp_probe_1';",
            SqlServerBackupCommandBuilder.BuildCreateSubdirectory(new DatabaseServerPath("/var/it's/bp_probe_1")));
}
