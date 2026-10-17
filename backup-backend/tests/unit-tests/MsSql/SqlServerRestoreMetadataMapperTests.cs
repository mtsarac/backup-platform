using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Restore;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerRestoreMetadataMapperTests
{
    private static readonly Guid Family = Guid.Parse("426B41DB-9CE3-41F8-A9FC-2E386EA17723");

    /// <summary>A header row shaped like SQL Server 2025's (column names as the server sends them, types as the driver hands them over).</summary>
    private static Dictionary<string, object?> HeaderRow() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["BackupName"] = null,
        ["BackupType"] = (short)1,
        ["Compressed"] = (byte)0,
        ["Position"] = (short)1,
        ["ServerName"] = "db4c56422e5e",
        ["DatabaseName"] = "Sales",
        ["BackupSize"] = 3039232m,
        ["BackupStartDate"] = new DateTime(2026, 10, 7, 8, 58, 58, DateTimeKind.Unspecified),
        ["BackupFinishDate"] = new DateTime(2026, 10, 7, 8, 59, 3, DateTimeKind.Unspecified),
        ["CompatibilityLevel"] = (byte)170,
        ["SoftwareVersionMajor"] = 17,
        ["SoftwareVersionMinor"] = 0,
        ["SoftwareVersionBuild"] = 5005,
        ["Collation"] = "SQL_Latin1_General_CP1_CI_AS",
        ["FamilyGUID"] = Family,
        ["IsCopyOnly"] = false,
        ["HasBackupChecksums"] = true,
        ["KeyAlgorithm"] = null,
        ["EncryptorThumbprint"] = null,
        ["EncryptorType"] = null
    };

    [Fact]
    public void AFullBackupHeaderMapsEveryField()
    {
        var header = SqlServerBackupMetadataMapper.MapHeader(HeaderRow())!;

        Assert.Equal("Sales", header.DatabaseName);
        Assert.Equal(RestoreBackupKind.Database, header.Kind);
        Assert.Equal(new Version(17, 0, 5005), header.SourceServerVersion);
        Assert.Equal("db4c56422e5e", header.SourceServerName);
        Assert.Equal(170, header.CompatibilityLevel);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", header.Collation);
        Assert.Equal(new DateTime(2026, 10, 7, 8, 58, 58, DateTimeKind.Unspecified), header.BackupStartedAt);
        Assert.Equal(new DateTime(2026, 10, 7, 8, 59, 3, DateTimeKind.Unspecified), header.BackupFinishedAt);
        Assert.Equal(1, header.Position);
        Assert.Equal(3039232, header.BackupSizeBytes);
        Assert.False(header.IsCopyOnly);
        Assert.True(header.HasChecksums);
        Assert.False(header.IsCompressed);
        Assert.False(header.IsEncrypted);
        Assert.Equal(Family, header.FamilyGuid);
    }

    [Theory]
    [InlineData(1, RestoreBackupKind.Database)]
    [InlineData(2, RestoreBackupKind.TransactionLog)]
    [InlineData(5, RestoreBackupKind.Differential)]
    [InlineData(4, RestoreBackupKind.Other)]
    [InlineData(6, RestoreBackupKind.Other)]
    [InlineData(7, RestoreBackupKind.Other)]
    [InlineData(8, RestoreBackupKind.Other)]
    [InlineData(0, RestoreBackupKind.Other)]
    [InlineData(99, RestoreBackupKind.Other)]
    public void BackupTypeCodesMapToKinds(int code, RestoreBackupKind kind)
    {
        var row = HeaderRow();
        row["BackupType"] = (short)code;

        Assert.Equal(kind, SqlServerBackupMetadataMapper.MapHeader(row)!.Kind);
    }

    [Fact]
    public void ColumnNamesAreMatchedIgnoringCase()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["databasename"] = "Sales",
            ["BACKUPTYPE"] = 1,
            ["hasbackupchecksums"] = 1
        };

        var header = SqlServerBackupMetadataMapper.MapHeader(row)!;

        Assert.Equal("Sales", header.DatabaseName);
        Assert.True(header.HasChecksums);
    }

    [Fact]
    public void AnOldServersHeaderWithoutTheNewerColumnsIsToleratedAndTheMissingValuesAreUnknown()
    {
        // SQL Server 2008 R2: no KeyAlgorithm, EncryptorThumbprint, EncryptorType, Containment, CompressionAlgorithm, TimeZone.
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DatabaseName"] = "Legacy",
            ["BackupType"] = (short)1,
            ["Position"] = (short)1,
            ["SoftwareVersionMajor"] = 10,
            ["SoftwareVersionMinor"] = 50,
            ["SoftwareVersionBuild"] = 1600
        };

        var header = SqlServerBackupMetadataMapper.MapHeader(row)!;

        Assert.Equal(new Version(10, 50, 1600), header.SourceServerVersion);
        Assert.False(header.IsEncrypted);
        Assert.False(header.HasChecksums);
        Assert.False(header.IsCompressed);
        Assert.False(header.IsCopyOnly);
        Assert.Null(header.FamilyGuid);
        Assert.Null(header.Collation);
        Assert.Null(header.CompatibilityLevel);
        Assert.Null(header.BackupStartedAt);
        Assert.Equal(0, header.BackupSizeBytes);
    }

    [Theory]
    [InlineData("KeyAlgorithm", "aes_256", true)]
    [InlineData("EncryptorType", "CERTIFICATE", true)]
    [InlineData("KeyAlgorithm", "NO_Encryption", false)]
    [InlineData("KeyAlgorithm", "  ", false)]
    public void BackupEncryptionIsRecognizedFromTheAlgorithmOrTheEncryptorType(string column, string value, bool encrypted)
    {
        var row = HeaderRow();
        row[column] = value;

        Assert.Equal(encrypted, SqlServerBackupMetadataMapper.MapHeader(row)!.IsEncrypted);
    }

    [Fact]
    public void SourceHeaderDatesDoNotAcquireTheRestoreTargetsTimeZone()
    {
        var header = SqlServerBackupMetadataMapper.MapHeader(HeaderRow())!;
        Assert.Equal("\"2026-10-07T08:58:58\"", System.Text.Json.JsonSerializer.Serialize(header.BackupStartedAt));
    }

    [Fact]
    public void AnEncryptorThumbprintAloneMarksTheBackupAsEncryptedAndAnEmptyOneDoesNot()
    {
        var withThumbprint = HeaderRow();
        withThumbprint["EncryptorThumbprint"] = new byte[] { 1, 2, 3 };
        var empty = HeaderRow();
        empty["EncryptorThumbprint"] = Array.Empty<byte>();

        Assert.True(SqlServerBackupMetadataMapper.MapHeader(withThumbprint)!.IsEncrypted);
        Assert.False(SqlServerBackupMetadataMapper.MapHeader(empty)!.IsEncrypted);
    }

    [Theory]
    [InlineData("Compressed", (byte)1)]
    [InlineData("Compressed", true)]
    [InlineData("IsCompressed", true)]
    [InlineData("IsCompressed", 1)]
    public void CompressionIsReadFromEitherColumnSpelling(string column, object value)
    {
        var row = HeaderRow();
        row[column] = value;

        Assert.True(SqlServerBackupMetadataMapper.MapHeader(row)!.IsCompressed);
    }

    [Fact]
    public void FlagsAcceptBitAndIntegerTypes()
    {
        var row = HeaderRow();
        row["IsCopyOnly"] = (byte)1;
        row["HasBackupChecksums"] = 0;

        var header = SqlServerBackupMetadataMapper.MapHeader(row)!;

        Assert.True(header.IsCopyOnly);
        Assert.False(header.HasChecksums);
    }

    [Fact]
    public void ARowWithoutADatabaseNameOrABackupTypeIsNotABackupSet()
    {
        var noName = HeaderRow();
        noName.Remove("DatabaseName");
        var blankName = HeaderRow();
        blankName["DatabaseName"] = "   ";
        var noType = HeaderRow();
        noType.Remove("BackupType");
        var nullType = HeaderRow();
        nullType["BackupType"] = null;

        Assert.Null(SqlServerBackupMetadataMapper.MapHeader(noName));
        Assert.Null(SqlServerBackupMetadataMapper.MapHeader(blankName));
        Assert.Null(SqlServerBackupMetadataMapper.MapHeader(noType));
        Assert.Null(SqlServerBackupMetadataMapper.MapHeader(nullType));
    }

    [Fact]
    public void AnUnparseableVersionOrAnEmptyFamilyGuidIsUnknownNotAnError()
    {
        var row = HeaderRow();
        row["SoftwareVersionMajor"] = null;
        row["FamilyGUID"] = Guid.Empty;
        row["CompatibilityLevel"] = "not a number";

        var header = SqlServerBackupMetadataMapper.MapHeader(row)!;

        Assert.Null(header.SourceServerVersion);
        Assert.Null(header.FamilyGuid);
        Assert.Null(header.CompatibilityLevel);
    }

    [Fact]
    public void ANegativeOrHugeSizeIsClamped()
    {
        var negative = HeaderRow();
        negative["BackupSize"] = -5m;
        var huge = HeaderRow();
        huge["BackupSize"] = 99999999999999999999m;

        Assert.Equal(0, SqlServerBackupMetadataMapper.MapHeader(negative)!.BackupSizeBytes);
        Assert.Equal(long.MaxValue, SqlServerBackupMetadataMapper.MapHeader(huge)!.BackupSizeBytes);
    }

    [Fact]
    public void AMissingPositionDefaultsToTheFirstBackupSet()
    {
        var row = HeaderRow();
        row.Remove("Position");

        Assert.Equal(1, SqlServerBackupMetadataMapper.MapHeader(row)!.Position);
    }

    private static Dictionary<string, object?> FileRow(string logical, string type, string? group = "PRIMARY", decimal size = 8388608m) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["LogicalName"] = logical,
        ["PhysicalName"] = "/var/opt/mssql/data/" + logical + ".mdf",
        ["Type"] = type,
        ["FileGroupName"] = group,
        ["Size"] = size,
        ["MaxSize"] = 35184372080640m,
        ["FileId"] = 1,
        ["IsPresent"] = true
    };

    [Fact]
    public void AFileRowMapsEveryField()
    {
        var file = SqlServerBackupMetadataMapper.MapFile(FileRow("Sales", "D"))!;

        Assert.Equal("Sales", file.LogicalName);
        Assert.Equal("/var/opt/mssql/data/Sales.mdf", file.SourcePhysicalName);
        Assert.Equal(RestoreFileKind.Data, file.Kind);
        Assert.Equal("PRIMARY", file.FileGroupName);
        Assert.Equal(8388608, file.SizeBytes);
    }

    [Theory]
    [InlineData("D", RestoreFileKind.Data)]
    [InlineData("d", RestoreFileKind.Data)]
    [InlineData("L", RestoreFileKind.Log)]
    [InlineData("F", RestoreFileKind.FullText)]
    [InlineData("S", RestoreFileKind.FileStream)]
    [InlineData("X", RestoreFileKind.Other)]
    [InlineData("", RestoreFileKind.Other)]
    [InlineData(null, RestoreFileKind.Other)]
    public void FileTypesMapToKinds(string? type, RestoreFileKind kind) => Assert.Equal(kind, SqlServerBackupMetadataMapper.MapFileKind(type));

    [Fact]
    public void ALogFileHasNoFileGroup()
    {
        var file = SqlServerBackupMetadataMapper.MapFile(FileRow("Sales_log", "L", group: null))!;

        Assert.Null(file.FileGroupName);
        Assert.Equal(RestoreFileKind.Log, file.Kind);
    }

    [Fact]
    public void AFileRowWithoutALogicalNameIsRejected()
    {
        var row = FileRow("x", "D");
        row.Remove("LogicalName");
        var empty = FileRow("", "D");

        Assert.Null(SqlServerBackupMetadataMapper.MapFile(row));
        Assert.Null(SqlServerBackupMetadataMapper.MapFile(empty));
    }

    [Fact]
    public void AFileRowWithoutAPhysicalNameOrSizeIsToleratedWithEmptyValues()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["LogicalName"] = "Sales", ["Type"] = "D" };

        var file = SqlServerBackupMetadataMapper.MapFile(row)!;

        Assert.Equal(string.Empty, file.SourcePhysicalName);
        Assert.Equal(0, file.SizeBytes);
    }
}
