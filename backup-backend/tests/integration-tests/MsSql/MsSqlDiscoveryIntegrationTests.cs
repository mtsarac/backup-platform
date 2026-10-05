using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL;
using BackupPlatform.BackupProviders.MSSQL.Capabilities;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests.MsSql;

[Collection(MsSqlCollection.Name)]
public sealed class MsSqlDiscoveryIntegrationTests(MsSqlFixture fixture)
{
    private readonly SqlServerBackupProvider _provider = MsSqlCollection.CreateProvider();

    [Fact]
    public async Task ConnectsAndReadsServerMetadataThatMatchesTheServersOwnAnswer()
    {
        var discovery = await _provider.DiscoverSqlServerAsync(fixture.Settings, fixture.Admin, CancellationToken.None);
        var instance = discovery.Instance;

        var oracle = (await fixture.QueryAsync(
            "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')), CONVERT(nvarchar(128), SERVERPROPERTY('Edition')), CONVERT(int, SERVERPROPERTY('EngineEdition')), "
            + "CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')), CONVERT(nvarchar(256), SERVERPROPERTY('InstanceName')), "
            + "CONVERT(nvarchar(128), SERVERPROPERTY('ProductLevel')), CONVERT(nvarchar(128), SERVERPROPERTY('ProductUpdateLevel')), CONVERT(nvarchar(256), SERVERPROPERTY('MachineName'))")).Single();

        Assert.NotNull(instance.Version);
        Assert.Equal((string)oracle[0]!, instance.Version.Value.ToString());
        Assert.Equal((string)oracle[0]!, instance.RawProductVersion);
        Assert.Equal((string)oracle[1]!, instance.Edition.Text);
        Assert.Equal((int)oracle[2]!, instance.Edition.RawEngineEdition);
        Assert.NotEqual(SqlServerEngineEdition.Unknown, instance.Edition.Engine);
        Assert.NotEqual(SqlServerEditionFamily.Unknown, instance.Edition.Family);
        Assert.Equal((string)oracle[3]!, instance.ServerName);
        Assert.Equal((string?)oracle[4], instance.InstanceName);
        Assert.Equal((string?)oracle[5], instance.ProductLevel);
        Assert.Equal((string?)oracle[6], instance.ProductUpdateLevel);
        Assert.Equal((string?)oracle[7], instance.MachineName);
    }

    [Fact]
    public async Task ReportsTheHostPlatformTheServerReports()
    {
        var instance = (await _provider.DiscoverSqlServerAsync(fixture.Settings, fixture.Admin, CancellationToken.None)).Instance;

        var expected = instance.Version is { Major: >= 14 }
            ? Enum.Parse<SqlServerHostPlatform>((string)(await fixture.QueryAsync("SELECT host_platform FROM sys.dm_os_host_info")).Single()[0]!, ignoreCase: true)
            : SqlServerHostPlatform.Windows;
        Assert.Equal(expected, instance.HostPlatform);
        Assert.NotEqual(SqlServerHostPlatform.Unknown, instance.HostPlatform);
    }

    [Fact]
    public async Task ListsEveryDatabaseAndMarksSystemDatabasesWithoutDroppingThem()
    {
        var databases = (await _provider.DiscoverSqlServerAsync(fixture.Settings, fixture.Admin, CancellationToken.None)).Databases;

        var system = databases.Where(database => database.IsSystem).ToDictionary(database => database.SystemDatabase);
        Assert.Equal(
            [SqlServerSystemDatabase.Master, SqlServerSystemDatabase.Tempdb, SqlServerSystemDatabase.Model, SqlServerSystemDatabase.Msdb],
            system.Keys.Order());
        Assert.Equal("master", system[SqlServerSystemDatabase.Master].Name, ignoreCase: true);
        Assert.Equal("tempdb", system[SqlServerSystemDatabase.Tempdb].Name, ignoreCase: true);
        Assert.Equal("model", system[SqlServerSystemDatabase.Model].Name, ignoreCase: true);
        Assert.Equal("msdb", system[SqlServerSystemDatabase.Msdb].Name, ignoreCase: true);
        Assert.Contains(databases, database => database.Name == fixture.OnlineFull && !database.IsSystem);
    }

    [Fact]
    public async Task StateRecoveryModelAndCompatibilityLevelMatchTheCatalog()
    {
        var databases = (await _provider.DiscoverSqlServerAsync(fixture.Settings, fixture.Admin, CancellationToken.None)).Databases;

        // The catalog's own descriptions are the independent oracle for the numeric-code mapping.
        var oracle = (await fixture.QueryAsync("SELECT name, state_desc, recovery_model_desc, compatibility_level, user_access_desc FROM sys.databases"))
            .ToDictionary(row => (string)row[0]!);

        Assert.Equal(oracle.Count, databases.Count);
        foreach (var database in databases)
        {
            var row = oracle[database.Name];
            Assert.Equal(ParseDescription<SqlServerDatabaseState>((string)row[1]!), database.State);
            Assert.Equal(ParseDescription<SqlServerRecoveryModel>((string)row[2]!), database.RecoveryModel);
            Assert.Equal(ParseDescription<SqlServerUserAccess>((string)row[4]!), database.UserAccess);
            Assert.Equal(Convert.ToInt32(row[3]), database.CompatibilityLevel?.Value);
        }
    }

    [Theory]
    [InlineData("OnlineFull", SqlServerDatabaseState.Online, SqlServerRecoveryModel.Full, SqlServerUserAccess.MultiUser)]
    [InlineData("OnlineSimple", SqlServerDatabaseState.Online, SqlServerRecoveryModel.Simple, SqlServerUserAccess.MultiUser)]
    [InlineData("OnlineBulkLogged", SqlServerDatabaseState.Online, SqlServerRecoveryModel.BulkLogged, SqlServerUserAccess.MultiUser)]
    [InlineData("Offline", SqlServerDatabaseState.Offline, SqlServerRecoveryModel.Full, SqlServerUserAccess.MultiUser)]
    [InlineData("Emergency", SqlServerDatabaseState.Emergency, SqlServerRecoveryModel.Full, SqlServerUserAccess.MultiUser)]
    [InlineData("SingleUser", SqlServerDatabaseState.Online, SqlServerRecoveryModel.Full, SqlServerUserAccess.SingleUser)]
    public async Task ArrangedDatabasesAreDiscoveredWithTheStateWeCreatedThemIn(
        string which, SqlServerDatabaseState state, SqlServerRecoveryModel recovery, SqlServerUserAccess access)
    {
        var name = which switch
        {
            "OnlineFull" => fixture.OnlineFull,
            "OnlineSimple" => fixture.OnlineSimple,
            "OnlineBulkLogged" => fixture.OnlineBulkLogged,
            "Offline" => fixture.Offline,
            "Emergency" => fixture.Emergency,
            _ => fixture.SingleUser
        };

        var databases = (await _provider.DiscoverSqlServerAsync(fixture.Settings, fixture.Admin, CancellationToken.None)).Databases;
        var database = Assert.Single(databases, candidate => candidate.Name == name);

        Assert.Equal(state, database.State);
        Assert.Equal(access, database.UserAccess);
        Assert.Equal(new SqlServerCompatibilityLevel(130), database.CompatibilityLevel);
        // An offline database does not expose its recovery model to every server version; when it does, it must be the one we set.
        if (state != SqlServerDatabaseState.Offline)
        {
            Assert.Equal(recovery, database.RecoveryModel);
        }
    }

    [Fact]
    public async Task NeutralDiscoveryCarriesInstanceDatabasesAndEvaluatedCapabilities()
    {
        IDatabaseBackupProvider provider = _provider;

        var discovery = await provider.DiscoverAsync(fixture.Settings, fixture.Admin, CancellationToken.None);

        Assert.Equal(DatabaseEngineId.MsSql, discovery.Instance.Engine);
        Assert.NotNull(discovery.Instance.Version);
        Assert.NotNull(discovery.Instance.HostPlatform);
        Assert.Equal(DatabaseCapabilitySupport.Supported, discovery.Capabilities.SupportOf(SqlServerCapabilityIds.FullBackup));

        var tempdb = Assert.Single(discovery.Databases, database => database.Name.Equals("tempdb", StringComparison.OrdinalIgnoreCase));
        Assert.True(tempdb.IsSystem);
        Assert.Equal(DatabaseCapabilitySupport.Unsupported, tempdb.Capabilities.SupportOf(SqlServerCapabilityIds.FullBackup));

        var user = Assert.Single(discovery.Databases, database => database.Name == fixture.OnlineSimple);
        Assert.False(user.IsSystem);
        Assert.Equal(DatabaseAvailability.Online, user.Availability);
        Assert.Equal(DatabaseCapabilitySupport.Unsupported, user.Capabilities.SupportOf(SqlServerCapabilityIds.LogBackup));
        Assert.Equal(DatabaseAvailability.Unavailable, Assert.Single(discovery.Databases, database => database.Name == fixture.Offline).Availability);
    }

    [Fact]
    public async Task LimitedLoginStillDiscoversAndSeesTheSameServerMetadata()
    {
        var admin = await _provider.DiscoverSqlServerAsync(fixture.Settings, fixture.Admin, CancellationToken.None);
        var limited = await _provider.DiscoverSqlServerAsync(fixture.Settings, fixture.Credential(fixture.NoUser), CancellationToken.None);

        Assert.NotNull(limited.Instance.Version);
        Assert.Equal(admin.Instance.Version, limited.Instance.Version);
        Assert.Equal(admin.Instance.Edition, limited.Instance.Edition);
        Assert.Contains(limited.Databases, database => database.SystemDatabase == SqlServerSystemDatabase.Master);

        // Observed on SQL Server 2025: sys.dm_os_host_info is readable by a login with no server permissions, so the platform is
        // reported. Older versions may gate it, in which case discovery must degrade to Unknown instead of failing.
        if (limited.Instance.Version is { Major: >= 17 })
        {
            Assert.Equal(admin.Instance.HostPlatform, limited.Instance.HostPlatform);
        }
        else
        {
            Assert.Contains(limited.Instance.HostPlatform, new[] { admin.Instance.HostPlatform, SqlServerHostPlatform.Unknown });
        }
    }

    [Fact]
    public async Task AMissingServerPermissionIsClassifiedAsPermissionDeniedAgainstTheRealEngine()
    {
        // sys.dm_os_sys_info needs VIEW SERVER PERFORMANCE STATE and fails with Msg 300 followed by Msg 297 for a login without it.
        var settings = fixture.Settings;
        await using var session = await SqlServerSession.OpenAsync(settings, fixture.Credential(fixture.NoUser), NullLogger.Instance, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<DatabaseProviderException>(() => session.QueryAsync(
            "gated-dmv", "SELECT TOP (1) cpu_count FROM sys.dm_os_sys_info;", bind: null, reader => reader.GetInt32(0), CancellationToken.None));

        Assert.Equal(DatabaseFailureKind.PermissionDenied, failure.Kind);
        var sql = Assert.IsType<SqlException>(failure.InnerException);
        Assert.Contains(sql.Errors.Cast<SqlError>().Select(error => error.Number), number => number is 300 or 297);
        Assert.DoesNotContain(fixture.NoUser.Name, failure.Message, StringComparison.Ordinal);
    }

    private static TEnum ParseDescription<TEnum>(string description) where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(description.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true);
}
