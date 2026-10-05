using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL;
using BackupPlatform.BackupProviders.MSSQL.Capabilities;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests.MsSql;

[Collection(MsSqlCollection.Name)]
public sealed class MsSqlPreCheckIntegrationTests(MsSqlFixture fixture)
{
    private readonly SqlServerBackupProvider _provider = MsSqlCollection.CreateProvider();

    [Fact]
    public async Task SysadminPassesTheFullBackupPreCheckOfAnOnlineDatabase()
    {
        var report = await PreCheckAsync(fixture.Admin, fixture.OnlineFull);

        Assert.True(report.CanProceed, Describe(report));
        Assert.Equal(BackupPreCheckStatus.Passed, report.Status);
        Assert.Equal(SqlServerPreCheckReasons.PermissionGranted, Item(report, BackupPreCheckIds.BackupPermission).ReasonCode);
    }

    [Fact]
    public async Task BackupOperatorWithoutSysadminIsRecognised()
    {
        var report = await PreCheckAsync(fixture.Credential(fixture.Operator), fixture.OnlineFull);

        Assert.Equal(SqlServerPreCheckReasons.PermissionGranted, Item(report, BackupPreCheckIds.BackupPermission).ReasonCode);
        Assert.True(report.CanProceed, Describe(report));
    }

    [Fact]
    public async Task AnExplicitBackupDatabaseGrantIsRecognised()
    {
        var report = await PreCheckAsync(fixture.Credential(fixture.GrantedOnly), fixture.OnlineFull);

        Assert.Equal(SqlServerPreCheckReasons.PermissionGranted, Item(report, BackupPreCheckIds.BackupPermission).ReasonCode);
    }

    [Fact]
    public async Task ADenyOverridesBackupOperatorMembershipSoThereIsNoFalsePositive()
    {
        var report = await PreCheckAsync(fixture.Credential(fixture.Denied), fixture.OnlineFull);

        var permission = Item(report, BackupPreCheckIds.BackupPermission);
        Assert.Equal(BackupPreCheckStatus.Failed, permission.Status);
        Assert.Equal(SqlServerPreCheckReasons.PermissionDenied, permission.ReasonCode);
        Assert.False(report.CanProceed);
    }

    [Fact]
    public async Task ALoginWithOnlyReadAccessIsRefused()
    {
        var report = await PreCheckAsync(fixture.Credential(fixture.ReaderOnly), fixture.OnlineFull);

        Assert.Equal(SqlServerPreCheckReasons.PermissionDenied, Item(report, BackupPreCheckIds.BackupPermission).ReasonCode);
        Assert.False(report.CanProceed);
    }

    [Fact]
    public async Task ALoginWithNoUserInTheDatabaseIsNeverReportedAsGranted()
    {
        var report = await PreCheckAsync(fixture.Credential(fixture.NoUser), fixture.OnlineFull);

        var permission = Item(report, BackupPreCheckIds.BackupPermission);
        Assert.NotEqual(SqlServerPreCheckReasons.PermissionGranted, permission.ReasonCode);
        Assert.NotEqual(BackupPreCheckStatus.Passed, permission.Status);
    }

    [Fact]
    public async Task WrongPasswordIsReachableButNotAuthenticatedAndSkipsTheRest()
    {
        var wrong = new PasswordDatabaseCredential(fixture.Admin.UserName, new DatabaseSecret("definitely-not-the-password"));

        var report = await PreCheckAsync(wrong, fixture.OnlineFull);

        Assert.Equal(BackupPreCheckStatus.Passed, Item(report, BackupPreCheckIds.ServerReachable).Status);
        var authentication = Item(report, BackupPreCheckIds.Authentication);
        Assert.Equal(BackupPreCheckStatus.Failed, authentication.Status);
        Assert.Equal(SqlServerPreCheckReasons.AuthenticationFailed, authentication.ReasonCode);
        Assert.DoesNotContain("definitely-not-the-password", authentication.Message, StringComparison.Ordinal);
        Assert.All(
            report.Items.Where(item => item.CheckId is not (BackupPreCheckIds.ServerReachable or BackupPreCheckIds.Authentication)),
            item => Assert.Equal(BackupPreCheckStatus.Skipped, item.Status));
    }

    [Fact]
    public async Task DiscoveryWithAWrongPasswordThrowsAUserSafeAuthenticationFailure()
    {
        var wrong = new PasswordDatabaseCredential(fixture.Admin.UserName, new DatabaseSecret("definitely-not-the-password"));

        var failure = await Assert.ThrowsAsync<DatabaseProviderException>(
            () => _provider.DiscoverAsync(fixture.Settings, wrong, CancellationToken.None));

        Assert.Equal(DatabaseFailureKind.AuthenticationFailed, failure.Kind);
        Assert.DoesNotContain("definitely-not-the-password", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Admin.UserName, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Offline", SqlServerPreCheckReasons.DatabaseOffline)]
    [InlineData("Emergency", SqlServerPreCheckReasons.DatabaseEmergency)]
    public async Task DatabasesThatAreNotOnlineAreRefused(string which, string reason)
    {
        var name = which == "Offline" ? fixture.Offline : fixture.Emergency;

        var report = await PreCheckAsync(fixture.Admin, name);

        var state = Item(report, BackupPreCheckIds.DatabaseState);
        Assert.Equal(BackupPreCheckStatus.Failed, state.Status);
        Assert.Equal(reason, state.ReasonCode);
        Assert.False(report.CanProceed);

        // Live finding: HAS_PERMS_BY_NAME says 0 for an OFFLINE database even for sysadmin. That must never surface as "denied".
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(report, BackupPreCheckIds.BackupPermission).Status);
        Assert.DoesNotContain(report.Items, item => item.ReasonCode == SqlServerPreCheckReasons.PermissionDenied);
    }

    [Fact]
    public async Task SingleUserDatabaseWarnsButMayProceed()
    {
        var report = await PreCheckAsync(fixture.Admin, fixture.SingleUser);

        Assert.Equal(SqlServerPreCheckReasons.DatabaseSingleUser, Item(report, BackupPreCheckIds.DatabaseState).ReasonCode);
        Assert.True(report.CanProceed, Describe(report));
        Assert.Equal(BackupPreCheckStatus.Warning, report.Status);
    }

    [Fact]
    public async Task MissingDatabaseFails()
    {
        var report = await PreCheckAsync(fixture.Admin, $"bp_it_missing_{Guid.NewGuid():N}");

        Assert.Equal(SqlServerPreCheckReasons.DatabaseNotFound, Item(report, BackupPreCheckIds.DatabaseExists).ReasonCode);
        Assert.False(report.CanProceed);
    }

    [Fact]
    public async Task TempdbIsRefusedByTheCapabilityCheck()
    {
        var report = await PreCheckAsync(fixture.Admin, "tempdb");

        var capability = Item(report, BackupPreCheckIds.BackupCapability);
        Assert.Equal(BackupPreCheckStatus.Failed, capability.Status);
        Assert.Equal(SqlServerPreCheckReasons.CapabilityUnsupported, capability.ReasonCode);
    }

    [Fact]
    public async Task MasterIsAcceptableForAFullBackup()
    {
        var report = await PreCheckAsync(fixture.Admin, "master");

        Assert.True(report.CanProceed, Describe(report));
    }

    [Theory]
    [InlineData("x'; DROP DATABASE [BPITMARKER]; --")]
    [InlineData("x] ; DROP DATABASE [BPITMARKER]; --")]
    [InlineData("Robert'); DROP TABLE Students;--")]
    public async Task HostileDatabaseNamesAreDataNotSql(string name)
    {
        var report = await PreCheckAsync(fixture.Admin, name.Replace("BPITMARKER", fixture.OnlineFull, StringComparison.Ordinal));

        Assert.Equal(SqlServerPreCheckReasons.DatabaseNotFound, Item(report, BackupPreCheckIds.DatabaseExists).ReasonCode);
        var survivors = await fixture.QueryAsync($"SELECT COUNT(*) FROM sys.databases WHERE name = N'{fixture.OnlineFull}'");
        Assert.Equal(1, Convert.ToInt32(survivors.Single()[0]));
    }

    [Fact]
    public async Task OverlongDatabaseNameIsNotFoundAndIsNotTruncatedToAnExistingOne()
    {
        var report = await PreCheckAsync(fixture.Admin, fixture.OnlineFull + new string('x', 200));

        Assert.Equal(SqlServerPreCheckReasons.DatabaseNotFound, Item(report, BackupPreCheckIds.DatabaseExists).ReasonCode);
    }

    [Fact]
    public async Task RequestedOptionsAreCheckedAgainstTheLiveServerCapabilities()
    {
        var discovery = await _provider.DiscoverAsync(fixture.Settings, fixture.Admin, CancellationToken.None);
        var request = new FullBackupPreCheckRequest(
            fixture.OnlineFull,
            [SqlServerCapabilityIds.BackupChecksum, SqlServerCapabilityIds.CopyOnly, SqlServerCapabilityIds.BackupCompression]);

        var report = await _provider.PreCheckFullBackupAsync(fixture.Settings, fixture.Admin, request, CancellationToken.None);

        foreach (var requested in request.RequestedCapabilities)
        {
            var item = Item(report, BackupPreCheckIds.RequestedCapability(requested));
            var expected = discovery.Capabilities.SupportOf(requested) switch
            {
                DatabaseCapabilitySupport.Supported => BackupPreCheckStatus.Passed,
                DatabaseCapabilitySupport.Unsupported => BackupPreCheckStatus.Failed,
                _ => BackupPreCheckStatus.Warning
            };
            Assert.Equal(expected, item.Status);
        }
    }

    [Fact]
    public async Task StagingPathShapeIsCheckedAgainstTheServersPlatform()
    {
        var instance = (await _provider.DiscoverSqlServerAsync(fixture.Settings, fixture.Admin, CancellationToken.None)).Instance;
        var native = new DatabaseServerPath(instance.DefaultBackupDirectory ?? (instance.HostPlatform == SqlServerHostPlatform.Linux ? "/var/opt/mssql/backup" : @"C:\Backups"));
        var foreign = new DatabaseServerPath(instance.HostPlatform == SqlServerHostPlatform.Linux ? @"C:\Backups" : "/var/opt/mssql/backup");

        var good = await _provider.PreCheckFullBackupAsync(fixture.Settings, fixture.Admin, new FullBackupPreCheckRequest(fixture.OnlineFull, stagingPath: native), CancellationToken.None);
        var bad = await _provider.PreCheckFullBackupAsync(fixture.Settings, fixture.Admin, new FullBackupPreCheckRequest(fixture.OnlineFull, stagingPath: foreign), CancellationToken.None);

        Assert.Equal(BackupPreCheckStatus.Passed, Item(good, BackupPreCheckIds.StagingPathFormat).Status);
        Assert.Equal(SqlServerPreCheckReasons.StagingPathWrongStyle, Item(bad, BackupPreCheckIds.StagingPathFormat).ReasonCode);
        Assert.False(bad.CanProceed);
    }

    [Fact]
    public async Task ExtensionsRunAfterTheBuiltInChecksAndTheirItemsAreAggregated()
    {
        var extension = new RecordingExtension();
        var provider = new SqlServerBackupProvider(NullLogger<SqlServerBackupProvider>.Instance, [extension]);
        var request = new FullBackupPreCheckRequest(fixture.OnlineFull, stagingPath: new DatabaseServerPath("/var/opt/mssql/backup"));

        var report = await provider.PreCheckFullBackupAsync(fixture.Settings, fixture.Admin, request, CancellationToken.None);

        Assert.Equal(request, extension.Context?.Request);
        Assert.Equal(fixture.OnlineFull, extension.Context?.Database.Name);
        Assert.Equal("staging.path-server-writable", report.Items[^1].CheckId);
        Assert.Equal(BackupPreCheckStatus.Warning, report.Items[^1].Status);
    }

    [Fact]
    public async Task AnExtensionThatThrowsBecomesAFailedItemNotAnException()
    {
        var provider = new SqlServerBackupProvider(NullLogger<SqlServerBackupProvider>.Instance, [new ThrowingExtension()]);

        var report = await provider.PreCheckFullBackupAsync(fixture.Settings, fixture.Admin, new FullBackupPreCheckRequest(fixture.OnlineFull), CancellationToken.None);

        var item = report.Items[^1];
        Assert.Equal(BackupPreCheckStatus.Failed, item.Status);
        Assert.Equal(SqlServerPreCheckReasons.ExtensionFailed, item.ReasonCode);
        Assert.DoesNotContain("password=hunter2", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtensionsDoNotRunWhenTheServerCannotBeAuthenticated()
    {
        var extension = new RecordingExtension();
        var provider = new SqlServerBackupProvider(NullLogger<SqlServerBackupProvider>.Instance, [extension]);
        var wrong = new PasswordDatabaseCredential(fixture.Admin.UserName, new DatabaseSecret("nope"));

        await provider.PreCheckFullBackupAsync(fixture.Settings, wrong, new FullBackupPreCheckRequest(fixture.OnlineFull), CancellationToken.None);

        Assert.Null(extension.Context);
    }

    private Task<BackupPreCheckReport> PreCheckAsync(DatabaseCredential credential, string database) =>
        _provider.PreCheckFullBackupAsync(fixture.Settings, credential, new FullBackupPreCheckRequest(database), CancellationToken.None);

    private static BackupPreCheckItem Item(BackupPreCheckReport report, string checkId) =>
        Assert.Single(report.Items, item => item.CheckId == checkId);

    private static string Describe(BackupPreCheckReport report) =>
        string.Join("; ", report.Items.Select(item => $"{item.CheckId}={item.Status}:{item.ReasonCode}"));

    private sealed class RecordingExtension : ISqlServerPreCheckExtension
    {
        public SqlServerPreCheckContext? Context { get; private set; }

        public Task<IReadOnlyList<BackupPreCheckItem>> CheckAsync(SqlServerPreCheckContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return Task.FromResult<IReadOnlyList<BackupPreCheckItem>>(
            [
                new BackupPreCheckItem("staging.path-server-writable", BackupPreCheckStatus.Warning, "not-probed", "Not probed by this test extension.")
            ]);
        }
    }

    private sealed class ThrowingExtension : ISqlServerPreCheckExtension
    {
        public Task<IReadOnlyList<BackupPreCheckItem>> CheckAsync(SqlServerPreCheckContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("password=hunter2");
    }
}
