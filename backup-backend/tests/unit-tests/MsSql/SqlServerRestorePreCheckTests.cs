using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;
using BackupPlatform.BackupProviders.MSSQL.Restore;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerRestorePreCheckTests
{
    private static readonly SqlServerRawDirectories LinuxDirectories = new("/var/opt/mssql/data/", "/var/opt/mssql/log/", "/var/opt/mssql/data/master.mdf", "/var/opt/mssql/data/mastlog.ldf");

    private static readonly SqlServerVolume[] Volumes =
    [
        new("/var/opt/mssql/data/master.mdf", AvailableBytes: 50_000_000, TotalBytes: 100_000_000),
        new("/var/opt/mssql/log/other.ldf", AvailableBytes: 10_000_000, TotalBytes: 20_000_000)
    ];

    private static SqlServerRestoreTargetFacts Facts(
        SqlServerDatabaseInfo? database = null,
        SqlServerInstanceInfo? instance = null,
        SqlServerRawDirectories? directories = null,
        bool? inAvailabilityGroup = false,
        bool? mirrored = false,
        IReadOnlyList<SqlServerVolume>? volumes = null,
        bool noDirectories = false) =>
        new(null, null, instance ?? MsSqlTestData.Instance(), database, noDirectories ? null : directories ?? LinuxDirectories, inAvailabilityGroup, mirrored, volumes ?? Volumes);

    private static BackupPreCheckReport Run(
        SqlServerRestoreTargetFacts target,
        RestorePreCheckRequest request,
        SqlServerBackupPermission permission = SqlServerBackupPermission.Granted,
        int? sessions = 0) =>
        new(SqlServerRestorePreCheck.Evaluate(new SqlServerRestorePreCheckFacts(target, permission, sessions), request));

    private static RestorePreCheckRequest NewDb(string name = "Restored", long? size = null) =>
        new(name, DatabaseRestoreMode.NewDatabase, false, size);

    private static RestorePreCheckRequest Replace(string name = "Sales", bool terminate = false, long? size = null) =>
        new(name, DatabaseRestoreMode.ReplaceExistingDatabase, terminate, size);

    private static BackupPreCheckItem Item(BackupPreCheckReport report, string checkId) => Assert.Single(report.Items, item => item.CheckId == checkId);

    [Fact]
    public void ANewDatabaseOnAHealthyServerPassesEverythingInDependencyOrder()
    {
        var report = Run(Facts(), NewDb());

        Assert.Equal(BackupPreCheckStatus.Passed, report.Status);
        Assert.Equal(
            [
                BackupPreCheckIds.ServerReachable,
                BackupPreCheckIds.Authentication,
                SqlServerRestoreCheckIds.TargetServer,
                SqlServerRestoreCheckIds.DatabaseName,
                BackupPreCheckIds.DatabaseExists,
                SqlServerRestoreCheckIds.RestorePermission,
                SqlServerRestoreCheckIds.Directories
            ],
            report.Items.Select(item => item.CheckId));
        Assert.All(report.Items, item => Assert.Equal(BackupPreCheckStatus.Passed, item.Status));
    }

    [Fact]
    public void ReplacingAnOnlineDatabaseWithoutSessionsPassesEverythingIncludingStateSessionsAndProtection()
    {
        var report = Run(Facts(MsSqlTestData.Database()), Replace(size: 1000), sessions: 0);

        Assert.Equal(BackupPreCheckStatus.Passed, report.Status);
        Assert.Equal(
            [
                BackupPreCheckIds.ServerReachable,
                BackupPreCheckIds.Authentication,
                SqlServerRestoreCheckIds.TargetServer,
                SqlServerRestoreCheckIds.DatabaseName,
                BackupPreCheckIds.DatabaseExists,
                SqlServerRestoreCheckIds.TargetProtected,
                BackupPreCheckIds.DatabaseState,
                SqlServerRestoreCheckIds.ActiveSessions,
                SqlServerRestoreCheckIds.RestorePermission,
                SqlServerRestoreCheckIds.Directories,
                SqlServerRestoreCheckIds.DiskSpace
            ],
            report.Items.Select(item => item.CheckId));
    }

    [Theory]
    [InlineData(DatabaseFailureKind.Unreachable, SqlServerPreCheckReasons.ServerUnreachable)]
    [InlineData(DatabaseFailureKind.Timeout, SqlServerPreCheckReasons.ConnectionTimeout)]
    [InlineData(DatabaseFailureKind.InvalidConfiguration, SqlServerPreCheckReasons.ConnectionSettingsInvalid)]
    [InlineData(DatabaseFailureKind.Unexpected, SqlServerPreCheckReasons.ConnectionFailed)]
    public void AnUnreachableServerFailsReachabilityAndSkipsEverythingElse(DatabaseFailureKind kind, string reason)
    {
        var facts = new SqlServerRestoreTargetFacts(new DatabaseProviderException(kind, "user safe text"), null, null, null, null, null, null, null);

        var report = Run(facts, Replace(size: 5));

        Assert.Equal(BackupPreCheckStatus.Failed, report.Status);
        var reachable = Item(report, BackupPreCheckIds.ServerReachable);
        Assert.Equal((BackupPreCheckStatus.Failed, reason, "user safe text"), (reachable.Status, reachable.ReasonCode, reachable.Message));
        Assert.All(report.Items.Where(item => item.CheckId != BackupPreCheckIds.ServerReachable), item => Assert.Equal(BackupPreCheckStatus.Skipped, item.Status));
        Assert.Contains(report.Items, item => item.CheckId == SqlServerRestoreCheckIds.DiskSpace);
    }

    [Fact]
    public void ARejectedLoginMeansReachableButNotAuthenticated()
    {
        var facts = new SqlServerRestoreTargetFacts(new DatabaseProviderException(DatabaseFailureKind.AuthenticationFailed, "credentials rejected"), null, null, null, null, null, null, null);

        var report = Run(facts, NewDb());

        Assert.Equal(BackupPreCheckStatus.Passed, Item(report, BackupPreCheckIds.ServerReachable).Status);
        var authentication = Item(report, BackupPreCheckIds.Authentication);
        Assert.Equal(BackupPreCheckStatus.Failed, authentication.Status);
        Assert.Equal(BackupFailureCause.AuthenticationFailed, authentication.Cause);
        Assert.Equal(BackupPreCheckStatus.Failed, report.Status);
    }

    [Fact]
    public void UnreadableServerMetadataFailsAndSkipsTheRest()
    {
        var facts = new SqlServerRestoreTargetFacts(null, new DatabaseProviderException(DatabaseFailureKind.Unexpected, "metadata message"), null, null, null, null, null, null);

        var report = Run(facts, NewDb());

        var server = Item(report, SqlServerRestoreCheckIds.TargetServer);
        Assert.Equal((BackupPreCheckStatus.Failed, SqlServerPreCheckReasons.MetadataUnavailable, BackupFailureCause.DatabaseUnavailable), (server.Status, server.ReasonCode, server.Cause));
        Assert.Equal("metadata message", server.Message);
        Assert.All(report.Items.Skip(3), item => Assert.Equal(BackupPreCheckStatus.Skipped, item.Status));
    }

    [Fact]
    public void TheServerItemCarriesTheVersionAndPlatformAndWarnsWhenTheVersionIsUnknown()
    {
        var known = Item(Run(Facts(), NewDb()), SqlServerRestoreCheckIds.TargetServer);
        var unknown = Item(Run(Facts(instance: MsSqlTestData.Instance(version: "weird")), NewDb()), SqlServerRestoreCheckIds.TargetServer);

        Assert.Equal(BackupPreCheckStatus.Passed, known.Status);
        Assert.Contains("16.0.4165.4", known.Message, StringComparison.Ordinal);
        Assert.Contains("Linux", known.Message, StringComparison.Ordinal);
        Assert.Equal((BackupPreCheckStatus.Warning, SqlServerRestoreReasons.ServerVersionUnknown), (unknown.Status, unknown.ReasonCode));
    }

    [Theory]
    [InlineData("bad\nname")]
    [InlineData("tab\tname")]
    public void ANameWithControlCharactersFailsAndTheNameDependentChecksAreSkipped(string name)
    {
        var report = Run(Facts(), NewDb(name));

        var item = Item(report, SqlServerRestoreCheckIds.DatabaseName);
        Assert.Equal((BackupPreCheckStatus.Failed, SqlServerRestoreReasons.DatabaseNameInvalid, BackupFailureCause.InvalidConfiguration), (item.Status, item.ReasonCode, item.Cause));
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(report, BackupPreCheckIds.DatabaseExists).Status);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(report, SqlServerRestoreCheckIds.TargetProtected).Status);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(report, SqlServerRestoreCheckIds.RestorePermission).Status);
    }

    [Fact]
    public void ANameLongerThanSysnameFails()
    {
        var report = Run(Facts(), NewDb(new string('a', 129)));

        Assert.Equal(BackupPreCheckStatus.Failed, Item(report, SqlServerRestoreCheckIds.DatabaseName).Status);
    }

    [Fact]
    public void ANewDatabaseWhoseNameIsTakenFailsWithTheEngineIndependentReason()
    {
        var report = Run(Facts(MsSqlTestData.Database("Restored")), NewDb());

        var exists = Item(report, BackupPreCheckIds.DatabaseExists);
        Assert.Equal((BackupPreCheckStatus.Failed, RestoreReasonCodes.TargetDatabaseExists), (exists.Status, exists.ReasonCode));
        Assert.False(report.CanProceed);
    }

    [Fact]
    public void AReplaceWithoutATargetFailsAndSkipsStateSessionsAndPermission()
    {
        var report = Run(Facts(null), Replace());

        var exists = Item(report, BackupPreCheckIds.DatabaseExists);
        Assert.Equal((BackupPreCheckStatus.Failed, RestoreReasonCodes.TargetDatabaseMissing, BackupFailureCause.DatabaseNotFound), (exists.Status, exists.ReasonCode, exists.Cause));
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(report, BackupPreCheckIds.DatabaseState).Status);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(report, SqlServerRestoreCheckIds.ActiveSessions).Status);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(report, SqlServerRestoreCheckIds.RestorePermission).Status);
        Assert.DoesNotContain(report.Items, item => item.CheckId == SqlServerRestoreCheckIds.TargetProtected);
    }

    [Theory]
    [InlineData("master", 1)]
    [InlineData("MODEL", 3)]
    [InlineData("msdb", 4)]
    [InlineData("tempdb", 2)]
    public void SystemDatabasesAreProtectedInBothModes(string name, int id)
    {
        foreach (var request in new[] { NewDb(name), Replace(name) })
        {
            var report = Run(Facts(MsSqlTestData.Database(name, id)), request);

            var protection = Item(report, SqlServerRestoreCheckIds.TargetProtected);
            Assert.Equal((BackupPreCheckStatus.Failed, RestoreReasonCodes.TargetProtected), (protection.Status, protection.ReasonCode));
        }
    }

    [Fact]
    public void AReservedNameIsProtectedEvenWhenTheCatalogDoesNotListIt()
    {
        var report = Run(Facts(null), NewDb("mssqlsystemresource"));

        Assert.Equal(RestoreReasonCodes.TargetProtected, Item(report, SqlServerRestoreCheckIds.TargetProtected).ReasonCode);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AvailabilityGroupAndMirroredTargetsAreProtected(bool group, bool mirror)
    {
        var report = Run(Facts(MsSqlTestData.Database(), inAvailabilityGroup: group, mirrored: mirror), Replace());

        var protection = Item(report, SqlServerRestoreCheckIds.TargetProtected);
        Assert.Equal((BackupPreCheckStatus.Failed, RestoreReasonCodes.TargetProtected), (protection.Status, protection.ReasonCode));
    }

    [Fact]
    public void ASnapshotTargetIsProtected()
    {
        var report = Run(Facts(MsSqlTestData.Database(snapshot: true)), Replace());

        Assert.Equal(RestoreReasonCodes.TargetProtected, Item(report, SqlServerRestoreCheckIds.TargetProtected).ReasonCode);
    }

    [Fact]
    public void UnknownMembershipIsAWarningAndKnownAbsenceIsAPass()
    {
        var unknown = Item(Run(Facts(MsSqlTestData.Database(), inAvailabilityGroup: null), Replace()), SqlServerRestoreCheckIds.TargetProtected);
        var unknownMirror = Item(Run(Facts(MsSqlTestData.Database(), mirrored: null), Replace()), SqlServerRestoreCheckIds.TargetProtected);
        var clear = Item(Run(Facts(MsSqlTestData.Database()), Replace()), SqlServerRestoreCheckIds.TargetProtected);

        Assert.Equal((BackupPreCheckStatus.Warning, SqlServerRestoreReasons.TargetProtectionUnknown), (unknown.Status, unknown.ReasonCode));
        Assert.Equal(BackupPreCheckStatus.Warning, unknownMirror.Status);
        Assert.Equal((BackupPreCheckStatus.Passed, SqlServerRestoreReasons.TargetNotProtected), (clear.Status, clear.ReasonCode));
    }

    [Theory]
    [InlineData(SqlServerDatabaseState.Online, SqlServerUserAccess.MultiUser, BackupPreCheckStatus.Passed, SqlServerPreCheckReasons.DatabaseOnline)]
    [InlineData(SqlServerDatabaseState.Online, SqlServerUserAccess.SingleUser, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DatabaseSingleUser)]
    [InlineData(SqlServerDatabaseState.Online, SqlServerUserAccess.RestrictedUser, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DatabaseRestrictedUser)]
    [InlineData(SqlServerDatabaseState.Restoring, SqlServerUserAccess.MultiUser, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DatabaseRestoring)]
    [InlineData(SqlServerDatabaseState.Offline, SqlServerUserAccess.MultiUser, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DatabaseOffline)]
    [InlineData(SqlServerDatabaseState.Suspect, SqlServerUserAccess.MultiUser, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DatabaseSuspect)]
    [InlineData(SqlServerDatabaseState.RecoveryPending, SqlServerUserAccess.MultiUser, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DatabaseRecoveryPending)]
    [InlineData(SqlServerDatabaseState.Emergency, SqlServerUserAccess.MultiUser, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DatabaseEmergency)]
    [InlineData(SqlServerDatabaseState.Recovering, SqlServerUserAccess.MultiUser, BackupPreCheckStatus.Failed, SqlServerPreCheckReasons.DatabaseRecovering)]
    [InlineData(SqlServerDatabaseState.Unknown, SqlServerUserAccess.MultiUser, BackupPreCheckStatus.Failed, SqlServerPreCheckReasons.DatabaseStateUnknown)]
    public void TheStateOfADatabaseToReplaceOnlyFailsWhenItIsTransientOrUnrecognised(
        SqlServerDatabaseState state, SqlServerUserAccess access, BackupPreCheckStatus status, string reason)
    {
        var report = Run(Facts(MsSqlTestData.Database(state: state, userAccess: access)), Replace());

        var item = Item(report, BackupPreCheckIds.DatabaseState);
        Assert.Equal((status, reason), (item.Status, item.ReasonCode));
    }

    [Fact]
    public void SessionsFailTheCheckUnlessDisconnectingThemWasAllowed()
    {
        var blocked = Item(Run(Facts(MsSqlTestData.Database()), Replace(terminate: false), sessions: 3), SqlServerRestoreCheckIds.ActiveSessions);
        var allowed = Item(Run(Facts(MsSqlTestData.Database()), Replace(terminate: true), sessions: 3), SqlServerRestoreCheckIds.ActiveSessions);
        var none = Item(Run(Facts(MsSqlTestData.Database()), Replace(terminate: false), sessions: 0), SqlServerRestoreCheckIds.ActiveSessions);

        Assert.Equal((BackupPreCheckStatus.Failed, RestoreReasonCodes.TargetInUse), (blocked.Status, blocked.ReasonCode));
        Assert.Contains("3 other session", blocked.Message, StringComparison.Ordinal);
        Assert.Equal((BackupPreCheckStatus.Warning, SqlServerRestoreReasons.SessionsWillBeTerminated), (allowed.Status, allowed.ReasonCode));
        Assert.Equal((BackupPreCheckStatus.Passed, SqlServerRestoreReasons.SessionsNone), (none.Status, none.ReasonCode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableSessionsAreAWarningNeverAPass(bool terminate)
    {
        var item = Item(Run(Facts(MsSqlTestData.Database()), Replace(terminate: terminate), sessions: null), SqlServerRestoreCheckIds.ActiveSessions);

        Assert.Equal((BackupPreCheckStatus.Warning, SqlServerRestoreReasons.SessionsUnknown), (item.Status, item.ReasonCode));
        Assert.Contains("VIEW SERVER STATE", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewDatabaseHasNoSessionsOrStateCheck()
    {
        var report = Run(Facts(), NewDb(), sessions: 5);

        Assert.DoesNotContain(report.Items, item => item.CheckId is SqlServerRestoreCheckIds.ActiveSessions or BackupPreCheckIds.DatabaseState);
    }

    [Theory]
    [InlineData(SqlServerBackupPermission.Granted, BackupPreCheckStatus.Passed, SqlServerRestoreReasons.RestorePermissionGranted)]
    [InlineData(SqlServerBackupPermission.Denied, BackupPreCheckStatus.Failed, SqlServerRestoreReasons.RestorePermissionDenied)]
    [InlineData(SqlServerBackupPermission.Unknown, BackupPreCheckStatus.Warning, SqlServerRestoreReasons.RestorePermissionUnknown)]
    public void ThePermissionAnswerIsReportedForBothModes(SqlServerBackupPermission permission, BackupPreCheckStatus status, string reason)
    {
        var fresh = Item(Run(Facts(), NewDb(), permission), SqlServerRestoreCheckIds.RestorePermission);
        var replace = Item(Run(Facts(MsSqlTestData.Database()), Replace(), permission), SqlServerRestoreCheckIds.RestorePermission);

        Assert.Equal((status, reason), (fresh.Status, fresh.ReasonCode));
        Assert.Equal((status, reason), (replace.Status, replace.ReasonCode));
    }

    [Fact]
    public void ADeniedPermissionNamesWhatEachModeNeedsAndCarriesThePermissionCause()
    {
        var fresh = Item(Run(Facts(), NewDb(), SqlServerBackupPermission.Denied), SqlServerRestoreCheckIds.RestorePermission);
        var replace = Item(Run(Facts(MsSqlTestData.Database()), Replace(), SqlServerBackupPermission.Denied), SqlServerRestoreCheckIds.RestorePermission);

        Assert.Contains("CREATE ANY DATABASE", fresh.Message, StringComparison.Ordinal);
        Assert.Contains("dbcreator", replace.Message, StringComparison.Ordinal);
        Assert.Equal(BackupFailureCause.PermissionDenied, fresh.Cause);
        Assert.Equal(BackupFailureCause.PermissionDenied, replace.Cause);
    }

    [Fact]
    public void UnreadableDirectoriesAreUnknownNotFailedAtPreCheckTime()
    {
        var item = Item(Run(Facts(noDirectories: true), NewDb()), SqlServerRestoreCheckIds.Directories);

        Assert.Equal((BackupPreCheckStatus.Warning, SqlServerRestoreReasons.DirectoriesUnknown), (item.Status, item.ReasonCode));
    }

    [Fact]
    public void NoUsableDirectoryAtAllFailsWithTheFileMappingReason()
    {
        var item = Item(Run(Facts(directories: new SqlServerRawDirectories(null, null, null, null)), NewDb()), SqlServerRestoreCheckIds.Directories);

        Assert.Equal((BackupPreCheckStatus.Failed, RestoreReasonCodes.FileMappingFailed), (item.Status, item.ReasonCode));
    }

    [Fact]
    public void MastersDirectoryInPlaceOfTheDefaultsIsAWarning()
    {
        var item = Item(Run(Facts(directories: new SqlServerRawDirectories(null, null, "/var/opt/mssql/data/master.mdf", "/var/opt/mssql/data/mastlog.ldf")), NewDb()), SqlServerRestoreCheckIds.Directories);

        Assert.Equal((BackupPreCheckStatus.Warning, SqlServerRestoreReasons.DirectoriesFallback), (item.Status, item.ReasonCode));
    }

    [Theory]
    [InlineData(SqlServerHostPlatform.Windows, "/var/opt/mssql/data", "/var/opt/mssql/log")]
    [InlineData(SqlServerHostPlatform.Linux, @"D:\Data", @"E:\Log")]
    public void DefaultDirectoriesInTheWrongStyleForThePlatformFail(SqlServerHostPlatform platform, string data, string log)
    {
        var report = Run(Facts(instance: MsSqlTestData.Instance(platform: platform), directories: new SqlServerRawDirectories(data, log, null, null)), NewDb());

        var item = Item(report, SqlServerRestoreCheckIds.Directories);
        Assert.Equal((BackupPreCheckStatus.Failed, RestoreReasonCodes.FileMappingFailed), (item.Status, item.ReasonCode));
    }

    [Fact]
    public void WithoutABackupSizeThereIsNoDiskSpaceItem()
    {
        Assert.DoesNotContain(Run(Facts(), NewDb(size: null)).Items, item => item.CheckId == SqlServerRestoreCheckIds.DiskSpace);
    }

    [Fact]
    public void TheBackupFilesSizeAgainstTheDataVolumeFailsOnAShortageAndPassesOtherwise()
    {
        var tooSmall = Item(Run(Facts(), NewDb(size: 50_000_001)), SqlServerRestoreCheckIds.DiskSpace);
        var exact = Item(Run(Facts(), NewDb(size: 50_000_000)), SqlServerRestoreCheckIds.DiskSpace);
        var ample = Item(Run(Facts(), NewDb(size: 1)), SqlServerRestoreCheckIds.DiskSpace);

        Assert.Equal((BackupPreCheckStatus.Failed, RestoreReasonCodes.InsufficientDiskSpace), (tooSmall.Status, tooSmall.ReasonCode));
        Assert.Equal(BackupPreCheckStatus.Passed, exact.Status);
        Assert.Equal(SqlServerRestoreReasons.DiskSpaceSufficient, ample.ReasonCode);
    }

    [Fact]
    public void UnreadableVolumesOrADirectoryWithoutAKnownVolumeAreUnknownNeverPassed()
    {
        var noVolumes = Item(Run(Facts() with { Volumes = null }, NewDb(size: 10)), SqlServerRestoreCheckIds.DiskSpace);
        var noMatch = Item(Run(Facts(volumes: [new SqlServerVolume("/elsewhere/x.mdf", 1, 1)]), NewDb(size: 10)), SqlServerRestoreCheckIds.DiskSpace);

        Assert.Equal((BackupPreCheckStatus.Warning, SqlServerRestoreReasons.DiskSpaceUnknown), (noVolumes.Status, noVolumes.ReasonCode));
        Assert.Equal(BackupPreCheckStatus.Warning, noMatch.Status);
    }

    [Fact]
    public void WithoutUsableDirectoriesTheDiskSpaceCheckIsSkipped()
    {
        var report = Run(Facts(noDirectories: true), NewDb(size: 10));

        Assert.Equal(BackupPreCheckStatus.Skipped, Item(report, SqlServerRestoreCheckIds.DiskSpace).Status);
        Assert.True(report.CanProceed);
    }

    [Fact]
    public void SkippedItemsNeverMakeTheReportFail()
    {
        var report = Run(Facts(null), Replace());

        Assert.Equal(BackupPreCheckStatus.Failed, report.Status);
        var withoutFailure = new BackupPreCheckReport(report.Items.Where(item => item.Status != BackupPreCheckStatus.Failed));
        Assert.True(withoutFailure.CanProceed);
    }
}
