using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Capabilities;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerPreCheckTests
{
    private static readonly FullBackupPreCheckRequest Sales = new("Sales");

    [Fact]
    public void HealthyOnlineDatabasePassesEverything()
    {
        var report = Run(Healthy(), Sales);

        Assert.Equal(BackupPreCheckStatus.Passed, report.Status);
        Assert.True(report.CanProceed);
        Assert.Equal(
            [
                BackupPreCheckIds.ServerReachable,
                BackupPreCheckIds.Authentication,
                BackupPreCheckIds.DatabaseExists,
                BackupPreCheckIds.DatabaseState,
                SqlServerFullBackupPreCheck.RecoveryModelCheckId,
                BackupPreCheckIds.BackupCapability,
                BackupPreCheckIds.BackupPermission
            ],
            report.Items.Select(item => item.CheckId));
        Assert.All(report.Items, item => Assert.Equal(BackupPreCheckStatus.Passed, item.Status));
    }

    [Theory]
    [InlineData(DatabaseFailureKind.Unreachable, SqlServerPreCheckReasons.ServerUnreachable)]
    [InlineData(DatabaseFailureKind.Timeout, SqlServerPreCheckReasons.ConnectionTimeout)]
    [InlineData(DatabaseFailureKind.InvalidConfiguration, SqlServerPreCheckReasons.ConnectionSettingsInvalid)]
    [InlineData(DatabaseFailureKind.Unexpected, SqlServerPreCheckReasons.ConnectionFailed)]
    public void UnreachableServerFailsReachabilityAndSkipsEverythingElse(DatabaseFailureKind kind, string reason)
    {
        var failure = new DatabaseProviderException(kind, "user safe text");
        var report = Run(new SqlServerPreCheckFacts(failure, null, null, null, SqlServerBackupPermission.Unknown), Sales);

        Assert.Equal(BackupPreCheckStatus.Failed, report.Status);
        Assert.False(report.CanProceed);
        var reachable = Assert.Single(report.Items, item => item.CheckId == BackupPreCheckIds.ServerReachable);
        Assert.Equal(BackupPreCheckStatus.Failed, reachable.Status);
        Assert.Equal(reason, reachable.ReasonCode);
        Assert.Equal("user safe text", reachable.Message);
        Assert.All(report.Items.Where(item => item.CheckId != BackupPreCheckIds.ServerReachable),
            item => Assert.Equal(BackupPreCheckStatus.Skipped, item.Status));
    }

    [Fact]
    public void RejectedLoginMeansReachableButNotAuthenticated()
    {
        var failure = new DatabaseProviderException(DatabaseFailureKind.AuthenticationFailed, "credentials rejected");
        var report = Run(new SqlServerPreCheckFacts(failure, null, null, null, SqlServerBackupPermission.Unknown), Sales);

        Assert.Equal(BackupPreCheckStatus.Passed, report.Items.Single(i => i.CheckId == BackupPreCheckIds.ServerReachable).Status);
        var authentication = report.Items.Single(i => i.CheckId == BackupPreCheckIds.Authentication);
        Assert.Equal(BackupPreCheckStatus.Failed, authentication.Status);
        Assert.Equal(SqlServerPreCheckReasons.AuthenticationFailed, authentication.ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Failed, report.Status);
    }

    [Fact]
    public void MissingDatabaseFailsExistenceAndSkipsDependentChecks()
    {
        var report = Run(Healthy() with { Database = null }, new FullBackupPreCheckRequest(
            "Missing", [SqlServerCapabilityIds.BackupChecksum], new DatabaseServerPath("/var/opt/mssql/backup")));

        var exists = report.Items.Single(i => i.CheckId == BackupPreCheckIds.DatabaseExists);
        Assert.Equal(BackupPreCheckStatus.Failed, exists.Status);
        Assert.Equal(SqlServerPreCheckReasons.DatabaseNotFound, exists.ReasonCode);
        Assert.Equal(
            [
                BackupPreCheckIds.DatabaseState,
                SqlServerFullBackupPreCheck.RecoveryModelCheckId,
                BackupPreCheckIds.BackupCapability,
                BackupPreCheckIds.BackupPermission,
                BackupPreCheckIds.StagingPathFormat,
                BackupPreCheckIds.RequestedCapability(SqlServerCapabilityIds.BackupChecksum)
            ],
            report.Items.Where(i => i.Status == BackupPreCheckStatus.Skipped).Select(i => i.CheckId));
    }

    [Fact]
    public void MetadataFailureAfterConnectingIsReportedWithoutPretendingTheDatabaseIsMissing()
    {
        var failure = new DatabaseProviderException(DatabaseFailureKind.PermissionDenied, "permission text");
        var report = Run(new SqlServerPreCheckFacts(null, failure, null, null, SqlServerBackupPermission.Unknown), Sales);

        var exists = report.Items.Single(i => i.CheckId == BackupPreCheckIds.DatabaseExists);
        Assert.Equal(SqlServerPreCheckReasons.MetadataUnavailable, exists.ReasonCode);
        Assert.Equal("permission text", exists.Message);
        Assert.Equal(BackupPreCheckStatus.Failed, report.Status);
    }

    [Theory]
    [InlineData(SqlServerDatabaseState.Offline, SqlServerPreCheckReasons.DatabaseOffline)]
    [InlineData(SqlServerDatabaseState.Restoring, SqlServerPreCheckReasons.DatabaseRestoring)]
    [InlineData(SqlServerDatabaseState.Recovering, SqlServerPreCheckReasons.DatabaseRecovering)]
    [InlineData(SqlServerDatabaseState.RecoveryPending, SqlServerPreCheckReasons.DatabaseRecoveryPending)]
    [InlineData(SqlServerDatabaseState.Suspect, SqlServerPreCheckReasons.DatabaseSuspect)]
    [InlineData(SqlServerDatabaseState.Emergency, SqlServerPreCheckReasons.DatabaseEmergency)]
    [InlineData(SqlServerDatabaseState.Unknown, SqlServerPreCheckReasons.DatabaseStateUnknown)]
    public void OnlyAnOnlineDatabaseIsEligibleForAFullBackup(SqlServerDatabaseState state, string reason)
    {
        var report = Run(Healthy(MsSqlTestData.Database(state: state)), Sales);

        var item = report.Items.Single(i => i.CheckId == BackupPreCheckIds.DatabaseState);
        Assert.Equal(BackupPreCheckStatus.Failed, item.Status);
        Assert.Equal(reason, item.ReasonCode);
        Assert.False(report.CanProceed);
    }

    [Theory]
    [InlineData(SqlServerDatabaseState.Offline)]
    [InlineData(SqlServerDatabaseState.Restoring)]
    [InlineData(SqlServerDatabaseState.Suspect)]
    [InlineData(SqlServerDatabaseState.Unknown)]
    public void PermissionIsNotEvaluatedForADatabaseThatIsNotOnlineEvenWhenTheServerAnsweredDenied(SqlServerDatabaseState state)
    {
        // Live finding: HAS_PERMS_BY_NAME answers 0 for an OFFLINE database even for sysadmin, so a Denied answer there is not a fact.
        var report = Run(Healthy(MsSqlTestData.Database(state: state)) with { Permission = SqlServerBackupPermission.Denied }, Sales);

        Assert.Equal(BackupPreCheckStatus.Skipped, report.Items.Single(i => i.CheckId == BackupPreCheckIds.BackupPermission).Status);
        Assert.DoesNotContain(report.Items, i => i.ReasonCode == SqlServerPreCheckReasons.PermissionDenied);
        Assert.Equal(BackupPreCheckStatus.Failed, report.Items.Single(i => i.CheckId == BackupPreCheckIds.DatabaseState).Status);
    }

    [Theory]
    [InlineData(SqlServerUserAccess.SingleUser, SqlServerPreCheckReasons.DatabaseSingleUser)]
    [InlineData(SqlServerUserAccess.RestrictedUser, SqlServerPreCheckReasons.DatabaseRestrictedUser)]
    public void LimitedAccessModesWarnButDoNotBlock(SqlServerUserAccess access, string reason)
    {
        var report = Run(Healthy(MsSqlTestData.Database(userAccess: access)), Sales);

        var item = report.Items.Single(i => i.CheckId == BackupPreCheckIds.DatabaseState);
        Assert.Equal(BackupPreCheckStatus.Warning, item.Status);
        Assert.Equal(reason, item.ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Warning, report.Status);
        Assert.True(report.CanProceed);
    }

    [Fact]
    public void UnknownRecoveryModelWarns()
    {
        var report = Run(Healthy(MsSqlTestData.Database(recoveryModel: SqlServerRecoveryModel.Unknown)), Sales);

        var item = report.Items.Single(i => i.CheckId == SqlServerFullBackupPreCheck.RecoveryModelCheckId);
        Assert.Equal(BackupPreCheckStatus.Warning, item.Status);
        Assert.Equal(SqlServerPreCheckReasons.RecoveryModelUnknown, item.ReasonCode);
        Assert.True(report.CanProceed);
    }

    [Fact]
    public void SimpleRecoveryDoesNotBlockAFullBackup()
    {
        var report = Run(Healthy(MsSqlTestData.Database(recoveryModel: SqlServerRecoveryModel.Simple)), Sales);
        Assert.Equal(BackupPreCheckStatus.Passed, report.Status);
    }

    [Fact]
    public void TempdbFailsTheFullBackupCapability()
    {
        var report = Run(Healthy(MsSqlTestData.Database("tempdb", id: 2)), new FullBackupPreCheckRequest("tempdb"));

        var item = report.Items.Single(i => i.CheckId == BackupPreCheckIds.BackupCapability);
        Assert.Equal(BackupPreCheckStatus.Failed, item.Status);
        Assert.Equal(SqlServerPreCheckReasons.CapabilityUnsupported, item.ReasonCode);
        Assert.False(report.CanProceed);
    }

    [Fact]
    public void AzureSqlDatabaseFailsTheFullBackupCapability()
    {
        var facts = Healthy() with { Instance = MsSqlTestData.Instance("12.0.2000.8", "SQL Azure", 5) };
        var item = Run(facts, Sales).Items.Single(i => i.CheckId == BackupPreCheckIds.BackupCapability);
        Assert.Equal(BackupPreCheckStatus.Failed, item.Status);
    }

    [Fact]
    public void UnknownFullBackupSupportWarnsInsteadOfPassing()
    {
        var facts = Healthy() with { Instance = MsSqlTestData.Instance("16.0.1000.6", "Managed", 8) };
        var report = Run(facts, Sales);

        var item = report.Items.Single(i => i.CheckId == BackupPreCheckIds.BackupCapability);
        Assert.Equal(BackupPreCheckStatus.Warning, item.Status);
        Assert.Equal(SqlServerPreCheckReasons.CapabilityUnknown, item.ReasonCode);
        Assert.True(report.CanProceed);
    }

    [Theory]
    [InlineData(SqlServerBackupPermission.Granted, BackupPreCheckStatus.Passed, SqlServerPreCheckReasons.PermissionGranted)]
    [InlineData(SqlServerBackupPermission.Denied, BackupPreCheckStatus.Failed, SqlServerPreCheckReasons.PermissionDenied)]
    [InlineData(SqlServerBackupPermission.Unknown, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.PermissionUnknown)]
    public void PermissionResultIsNeverCollapsedToABoolean(SqlServerBackupPermission permission, BackupPreCheckStatus status, string reason)
    {
        var report = Run(Healthy() with { Permission = permission }, Sales);

        var item = report.Items.Single(i => i.CheckId == BackupPreCheckIds.BackupPermission);
        Assert.Equal(status, item.Status);
        Assert.Equal(reason, item.ReasonCode);
    }

    [Fact]
    public void TdeDatabasesWarnAboutTheCertificate()
    {
        var report = Run(Healthy(MsSqlTestData.Database(tde: true)), Sales);

        var item = report.Items.Single(i => i.CheckId == SqlServerFullBackupPreCheck.EncryptionCheckId);
        Assert.Equal(BackupPreCheckStatus.Warning, item.Status);
        Assert.Equal(SqlServerPreCheckReasons.DatabaseTdeEnabled, item.ReasonCode);
        Assert.True(report.CanProceed);
    }

    [Fact]
    public void RequestedOptionsAreCheckedAgainstTheDatabaseCapabilities()
    {
        var request = new FullBackupPreCheckRequest(
            "Sales",
            [SqlServerCapabilityIds.BackupChecksum, SqlServerCapabilityIds.CopyOnly, SqlServerCapabilityIds.BackupCompression]);

        var enterprise = Run(Healthy(), request);
        Assert.Equal(BackupPreCheckStatus.Passed, enterprise.Status);
        Assert.Equal(3, enterprise.Items.Count(i => i.CheckId.StartsWith("requested.", StringComparison.Ordinal)));

        var express = Run(Healthy() with { Instance = MsSqlTestData.Instance("16.0.1000.6", "Express Edition (64-bit)", 4) }, request);
        var compression = express.Items.Single(i => i.CheckId == BackupPreCheckIds.RequestedCapability(SqlServerCapabilityIds.BackupCompression));
        Assert.Equal(BackupPreCheckStatus.Failed, compression.Status);
        Assert.Equal(SqlServerPreCheckReasons.CapabilityUnsupported, compression.ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Failed, express.Status);
    }

    [Fact]
    public void UnverifiableRequestedOptionWarns()
    {
        var request = new FullBackupPreCheckRequest("Sales", [SqlServerCapabilityIds.BackupEncryption]);
        var facts = Healthy() with { Instance = MsSqlTestData.Instance("16.0.1000.6", "Standard Edition (64-bit)", 2) };

        var item = Run(facts, request).Items.Single(i => i.CheckId.StartsWith("requested.", StringComparison.Ordinal));
        Assert.Equal(BackupPreCheckStatus.Warning, item.Status);
    }

    [Fact]
    public void UnrecognisedOrForeignCapabilityRequestsFail()
    {
        var request = new FullBackupPreCheckRequest("Sales", [new DatabaseCapabilityId("postgresql.backup.base")]);

        var item = Run(Healthy(), request).Items.Single(i => i.CheckId.StartsWith("requested.", StringComparison.Ordinal));
        Assert.Equal(BackupPreCheckStatus.Failed, item.Status);
        Assert.Equal(SqlServerPreCheckReasons.CapabilityNotRecognized, item.ReasonCode);
    }

    [Fact]
    public void LogBackupRequestOnASimpleRecoveryDatabaseFails()
    {
        var request = new FullBackupPreCheckRequest("Sales", [SqlServerCapabilityIds.LogBackup]);
        var report = Run(Healthy(MsSqlTestData.Database(recoveryModel: SqlServerRecoveryModel.Simple)), request);

        Assert.Equal(BackupPreCheckStatus.Failed, report.Items.Single(i => i.CheckId.StartsWith("requested.", StringComparison.Ordinal)).Status);
    }

    [Theory]
    [InlineData(SqlServerHostPlatform.Windows, @"D:\Backups\Staging", BackupPreCheckStatus.Passed)]
    [InlineData(SqlServerHostPlatform.Windows, @"\\fileserver\backups", BackupPreCheckStatus.Passed)]
    [InlineData(SqlServerHostPlatform.Windows, "E:/backups", BackupPreCheckStatus.Passed)]
    [InlineData(SqlServerHostPlatform.Linux, "/var/opt/mssql/backup", BackupPreCheckStatus.Passed)]
    [InlineData(SqlServerHostPlatform.Windows, "/var/opt/mssql/backup", BackupPreCheckStatus.Failed)]
    [InlineData(SqlServerHostPlatform.Linux, @"D:\Backups", BackupPreCheckStatus.Failed)]
    [InlineData(SqlServerHostPlatform.Linux, "backups/relative", BackupPreCheckStatus.Failed)]
    [InlineData(SqlServerHostPlatform.Windows, @"D:\Backups\..\Windows", BackupPreCheckStatus.Failed)]
    [InlineData(SqlServerHostPlatform.Linux, "/var/opt/../../etc", BackupPreCheckStatus.Failed)]
    [InlineData(SqlServerHostPlatform.Windows, "D:", BackupPreCheckStatus.Failed)]
    [InlineData(SqlServerHostPlatform.Windows, @"\\", BackupPreCheckStatus.Failed)]
    [InlineData(SqlServerHostPlatform.Linux, "/var/opt/mssql\nbackup", BackupPreCheckStatus.Failed)]
    [InlineData(SqlServerHostPlatform.Unknown, "/var/opt/mssql/backup", BackupPreCheckStatus.Warning)]
    [InlineData(SqlServerHostPlatform.Unknown, @"D:\Backups", BackupPreCheckStatus.Warning)]
    public void StagingPathShapeIsCheckedAgainstTheServerPlatform(SqlServerHostPlatform platform, string path, BackupPreCheckStatus expected)
    {
        var item = SqlServerFullBackupPreCheck.CheckStagingPathFormat(new DatabaseServerPath(path), platform);

        Assert.Equal(BackupPreCheckIds.StagingPathFormat, item.CheckId);
        Assert.Equal(expected, item.Status);
    }

    [Fact]
    public void PassedStagingPathStatesThatServerSideWritabilityIsNotChecked()
    {
        var item = SqlServerFullBackupPreCheck.CheckStagingPathFormat(new DatabaseServerPath("/var/opt/mssql/backup"), SqlServerHostPlatform.Linux);
        Assert.Contains("not checked", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoStagingItemIsReportedWhenNoStagingPathWasRequested()
    {
        Assert.DoesNotContain(Run(Healthy(), Sales).Items, i => i.CheckId == BackupPreCheckIds.StagingPathFormat);
    }

    [Fact]
    public void ReportAggregationPrefersFailedOverWarningOverPassedAndIgnoresSkipped()
    {
        BackupPreCheckItem Item(BackupPreCheckStatus status) => new("check", status, "reason", "message");

        Assert.Equal(BackupPreCheckStatus.Passed, new BackupPreCheckReport([]).Status);
        Assert.Equal(BackupPreCheckStatus.Passed, new BackupPreCheckReport([Item(BackupPreCheckStatus.Passed), Item(BackupPreCheckStatus.Skipped)]).Status);
        Assert.Equal(BackupPreCheckStatus.Warning, new BackupPreCheckReport([Item(BackupPreCheckStatus.Passed), Item(BackupPreCheckStatus.Warning), Item(BackupPreCheckStatus.Skipped)]).Status);

        var failed = new BackupPreCheckReport([Item(BackupPreCheckStatus.Warning), Item(BackupPreCheckStatus.Failed), Item(BackupPreCheckStatus.Passed)]);
        Assert.Equal(BackupPreCheckStatus.Failed, failed.Status);
        Assert.False(failed.CanProceed);
    }

    [Fact]
    public void ItemsRequireMachineReadableReasonAndMessage()
    {
        Assert.Throws<ArgumentException>(() => new BackupPreCheckItem("check", BackupPreCheckStatus.Passed, "", "message"));
        Assert.Throws<ArgumentException>(() => new BackupPreCheckItem("check", BackupPreCheckStatus.Passed, "reason", " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupPreCheckItem("check", (BackupPreCheckStatus)99, "reason", "message"));
    }

    private static SqlServerPreCheckFacts Healthy(SqlServerDatabaseInfo? database = null) =>
        new(null, null, MsSqlTestData.Instance(), database ?? MsSqlTestData.Database(), SqlServerBackupPermission.Granted);

    private static BackupPreCheckReport Run(SqlServerPreCheckFacts facts, FullBackupPreCheckRequest request) =>
        new(SqlServerFullBackupPreCheck.Evaluate(facts, request));
}
