using BackupPlatform.BackupProviders.MSSQL.Discovery;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerMetadataTests
{
    [Theory]
    [InlineData("16.0.4165.4", 16, 0, 4165, 4)]
    [InlineData("9.00.5000.00", 9, 0, 5000, 0)]
    [InlineData("15.0.2000.5", 15, 0, 2000, 5)]
    [InlineData("99.12.7.1", 99, 12, 7, 1)]
    public void VersionParsesFourComponents(string text, int major, int minor, int build, int revision)
    {
        Assert.True(SqlServerVersion.TryParse(text, out var version));
        Assert.Equal(new SqlServerVersion(major, minor, build, revision), version);
    }

    [Fact]
    public void VersionWithoutRevisionIsRepresentedWithoutInventingOne()
    {
        Assert.True(SqlServerVersion.TryParse("17.0.1000", out var version));
        Assert.Null(version.Revision);
        Assert.Equal("17.0.1000", version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("16")]
    [InlineData("16.0")]
    [InlineData("16.0.1.2.3")]
    [InlineData("a.b.c")]
    [InlineData("16.0.x")]
    [InlineData("-16.0.1")]
    [InlineData("16.0.+1")]
    [InlineData("16..1")]
    [InlineData("99999999999.0.1")]
    public void VersionRejectsUnrecognisedShapes(string? text)
    {
        Assert.False(SqlServerVersion.TryParse(text, out var version));
        Assert.Equal(default, version);
    }

    [Fact]
    public void VersionsOrderNumericallyNotLexically()
    {
        var older = new SqlServerVersion(9, 0, 5000);
        var newer = new SqlServerVersion(16, 0, 1000);
        var buildTen = new SqlServerVersion(16, 0, 10);
        var buildNine = new SqlServerVersion(16, 0, 9);

        Assert.True(older < newer);
        Assert.True(buildNine < buildTen);
        Assert.True(new SqlServerVersion(16, 0, 1) < new SqlServerVersion(16, 0, 1, 0));
        Assert.True(newer.IsAtLeast(16));
        Assert.True(newer.IsAtLeast(15, 5));
        Assert.False(older.IsAtLeast(10));
    }

    [Theory]
    [InlineData(10, 0, "SQL Server 2008")]
    [InlineData(10, 50, "SQL Server 2008 R2")]
    [InlineData(16, 0, "SQL Server 2022")]
    [InlineData(17, 0, "SQL Server 2025")]
    [InlineData(42, 0, null)] // future releases are representable without a name
    public void KnownReleasesHaveNames(int major, int minor, string? expected) =>
        Assert.Equal(expected, new SqlServerVersion(major, minor, 0).ReleaseName);

    [Theory]
    [InlineData("Enterprise Edition: Core-based Licensing (64-bit)", SqlServerEditionFamily.Enterprise)]
    [InlineData("Enterprise Evaluation Edition (64-bit)", SqlServerEditionFamily.Enterprise)]
    [InlineData("Developer Edition (64-bit)", SqlServerEditionFamily.Developer)]
    [InlineData("Standard Edition (64-bit)", SqlServerEditionFamily.Standard)]
    [InlineData("Web Edition (64-bit)", SqlServerEditionFamily.Web)]
    [InlineData("Business Intelligence Edition (64-bit)", SqlServerEditionFamily.BusinessIntelligence)]
    [InlineData("Express Edition with Advanced Services (64-bit)", SqlServerEditionFamily.Express)]
    [InlineData("SQL Azure", SqlServerEditionFamily.Azure)]
    [InlineData("Azure SQL Edge Developer (64-bit)", SqlServerEditionFamily.Azure)]
    [InlineData("Something Entirely New", SqlServerEditionFamily.Unknown)]
    [InlineData(null, SqlServerEditionFamily.Unknown)]
    public void EditionTextNormalisesToFamily(string? text, SqlServerEditionFamily expected) =>
        Assert.Equal(expected, SqlServerEdition.FromServerProperties(text, null).Family);

    [Theory]
    [InlineData(1, SqlServerEngineEdition.Personal)]
    [InlineData(2, SqlServerEngineEdition.Standard)]
    [InlineData(3, SqlServerEngineEdition.Enterprise)]
    [InlineData(4, SqlServerEngineEdition.Express)]
    [InlineData(5, SqlServerEngineEdition.AzureSqlDatabase)]
    [InlineData(8, SqlServerEngineEdition.AzureSqlManagedInstance)]
    [InlineData(9, SqlServerEngineEdition.AzureSqlEdge)]
    public void EngineEditionMapsKnownCodes(int code, SqlServerEngineEdition expected) =>
        Assert.Equal(expected, SqlServerEdition.FromServerProperties("x", code).Engine);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(77)]
    [InlineData(-1)]
    public void UnknownEngineEditionIsUnknownButKeepsTheRawCode(int code)
    {
        var edition = SqlServerEdition.FromServerProperties("Future Edition", code);
        Assert.Equal(SqlServerEngineEdition.Unknown, edition.Engine);
        Assert.Equal(code, edition.RawEngineEdition);
    }

    [Theory]
    [InlineData(0, SqlServerDatabaseState.Online)]
    [InlineData(1, SqlServerDatabaseState.Restoring)]
    [InlineData(2, SqlServerDatabaseState.Recovering)]
    [InlineData(3, SqlServerDatabaseState.RecoveryPending)]
    [InlineData(4, SqlServerDatabaseState.Suspect)]
    [InlineData(5, SqlServerDatabaseState.Emergency)]
    [InlineData(6, SqlServerDatabaseState.Offline)]
    public void DatabaseStateMapsDocumentedCodes(int code, SqlServerDatabaseState expected) =>
        Assert.Equal(expected, SqlServerMetadataMapper.MapState(code));

    [Theory]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(null)]
    public void NewOrMissingDatabaseStateIsUnknownNotAnException(int? code)
    {
        var database = SqlServerMetadataMapper.MapDatabase(Row(state: code));
        Assert.Equal(SqlServerDatabaseState.Unknown, database.State);
        Assert.Equal(code, database.StateCode);
    }

    [Theory]
    [InlineData(1, SqlServerRecoveryModel.Full)]
    [InlineData(2, SqlServerRecoveryModel.BulkLogged)]
    [InlineData(3, SqlServerRecoveryModel.Simple)]
    [InlineData(0, SqlServerRecoveryModel.Unknown)]
    [InlineData(4, SqlServerRecoveryModel.Unknown)]
    [InlineData(null, SqlServerRecoveryModel.Unknown)]
    public void RecoveryModelMapsCodes(int? code, SqlServerRecoveryModel expected) =>
        Assert.Equal(expected, SqlServerMetadataMapper.MapRecoveryModel(code));

    [Theory]
    [InlineData(0, SqlServerUserAccess.MultiUser)]
    [InlineData(1, SqlServerUserAccess.SingleUser)]
    [InlineData(2, SqlServerUserAccess.RestrictedUser)]
    [InlineData(3, SqlServerUserAccess.Unknown)]
    [InlineData(null, SqlServerUserAccess.Unknown)]
    public void UserAccessMapsCodes(int? code, SqlServerUserAccess expected) =>
        Assert.Equal(expected, SqlServerMetadataMapper.MapUserAccess(code));

    [Theory]
    [InlineData(1, SqlServerSystemDatabase.Master)]
    [InlineData(2, SqlServerSystemDatabase.Tempdb)]
    [InlineData(3, SqlServerSystemDatabase.Model)]
    [InlineData(4, SqlServerSystemDatabase.Msdb)]
    [InlineData(5, SqlServerSystemDatabase.None)]
    [InlineData(32767, SqlServerSystemDatabase.None)]
    public void SystemDatabasesAreIdentifiedByReservedId(int id, SqlServerSystemDatabase expected)
    {
        var database = SqlServerMetadataMapper.MapDatabase(Row(id: id));
        Assert.Equal(expected, database.SystemDatabase);
        Assert.Equal(expected != SqlServerSystemDatabase.None, database.IsSystem);
    }

    [Fact]
    public void SystemNamedUserDatabaseIsNotASystemDatabase()
    {
        // A name is not an identity: only the reserved ids mark system databases.
        Assert.False(SqlServerMetadataMapper.MapDatabase(Row(id: 9, name: "master")).IsSystem);
    }

    [Fact]
    public void DatabaseRowKeepsCompatibilityLevelSeparateFromServerVersion()
    {
        var database = SqlServerMetadataMapper.MapDatabase(Row(compatibility: 130));
        Assert.Equal(new SqlServerCompatibilityLevel(130), database.CompatibilityLevel);
        Assert.Null(SqlServerMetadataMapper.MapDatabase(Row(compatibility: null)).CompatibilityLevel);
        Assert.Null(SqlServerMetadataMapper.MapDatabase(Row(compatibility: 0)).CompatibilityLevel);
    }

    [Fact]
    public void SnapshotAndTdeFlagsAreCarried()
    {
        var database = SqlServerMetadataMapper.MapDatabase(Row(sourceDatabaseId: 5, encrypted: true, readOnly: true));
        Assert.True(database.IsSnapshot);
        Assert.True(database.IsTransparentDataEncrypted);
        Assert.True(database.IsReadOnly);
        Assert.False(SqlServerMetadataMapper.MapDatabase(Row()).IsSnapshot);
    }

    [Fact]
    public void InstanceMapsServerPropertiesIntoTypedModel()
    {
        var instance = SqlServerMetadataMapper.MapInstance(
            new RawServerProperties("HOST\\INST", "HOST", "INST", "16.0.4165.4", "RTM", "CU18", "Developer Edition (64-bit)", 3, "/var/opt/mssql/data"),
            SqlServerHostPlatform.Linux);

        Assert.Equal("HOST\\INST", instance.ServerName);
        Assert.Equal("INST", instance.InstanceName);
        Assert.Equal(new SqlServerVersion(16, 0, 4165, 4), instance.Version);
        Assert.Equal(SqlServerEditionFamily.Developer, instance.Edition.Family);
        Assert.Equal(SqlServerEngineEdition.Enterprise, instance.Edition.Engine);
        Assert.Equal(SqlServerHostPlatform.Linux, instance.HostPlatform);
        Assert.Equal("/var/opt/mssql/data", instance.DefaultBackupDirectory);
    }

    [Fact]
    public void DefaultInstanceHasNoInstanceName()
    {
        var instance = SqlServerMetadataMapper.MapInstance(
            new RawServerProperties("HOST", "HOST", null, "16.0.1000.6", null, null, "Standard Edition", 2, null), SqlServerHostPlatform.Windows);
        Assert.Null(instance.InstanceName);
    }

    [Fact]
    public void UnparseableVersionLeavesVersionNullButKeepsTheRawText()
    {
        var instance = SqlServerMetadataMapper.MapInstance(
            new RawServerProperties("S", null, null, "weird", null, null, null, null, null), SqlServerHostPlatform.Unknown);
        Assert.Null(instance.Version);
        Assert.Equal("weird", instance.RawProductVersion);
        Assert.Equal(SqlServerHostPlatform.Unknown, instance.HostPlatform);
    }

    [Theory]
    [InlineData("9.0.5000", SqlServerHostPlatform.Windows)]
    [InlineData("13.0.7000.1", SqlServerHostPlatform.Windows)]
    [InlineData("14.0.1000.1", SqlServerHostPlatform.Unknown)]
    [InlineData("16.0.1000.1", SqlServerHostPlatform.Unknown)]
    public void PlatformIsInferredOnlyWhereSqlServerCouldNotRunElsewhere(string version, SqlServerHostPlatform expected)
    {
        var instance = SqlServerMetadataMapper.MapInstance(
            new RawServerProperties("S", null, null, version, null, null, "Standard", 2, null), SqlServerHostPlatform.Unknown);
        Assert.Equal(expected, instance.HostPlatform);
    }

    [Theory]
    [InlineData("Windows", SqlServerHostPlatform.Windows)]
    [InlineData("Linux", SqlServerHostPlatform.Linux)]
    [InlineData("linux", SqlServerHostPlatform.Linux)]
    [InlineData("FreeBSD", SqlServerHostPlatform.Unknown)]
    [InlineData(null, SqlServerHostPlatform.Unknown)]
    public void HostPlatformMapping(string? text, SqlServerHostPlatform expected) =>
        Assert.Equal(expected, SqlServerMetadataMapper.MapHostPlatform(text));

    private static RawDatabaseRow Row(
        int id = 5,
        string? name = "Sales",
        int? state = 0,
        int? compatibility = 160,
        int? sourceDatabaseId = null,
        bool? encrypted = false,
        bool? readOnly = false) =>
        new(id, name, state, UserAccess: 0, RecoveryModel: 1, compatibility, readOnly, encrypted, sourceDatabaseId);
}
