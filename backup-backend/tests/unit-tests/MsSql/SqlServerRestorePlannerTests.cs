using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;
using BackupPlatform.BackupProviders.MSSQL.Restore;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerRestorePlannerTests
{
    private static readonly Guid Family = Guid.Parse("426B41DB-9CE3-41F8-A9FC-2E386EA17723");
    private static readonly SqlServerRawDirectories Directories = new("/var/opt/mssql/data/", "/var/opt/mssql/log/", "/var/opt/mssql/data/master.mdf", "/var/opt/mssql/data/mastlog.ldf");

    private static readonly SqlServerVolume[] Volumes =
    [
        new("/var/opt/mssql/data/master.mdf", AvailableBytes: 100_000_000, TotalBytes: 500_000_000),
        new("/var/opt/mssql/log/x.ldf", AvailableBytes: 100_000_000, TotalBytes: 500_000_000)
    ];

    private static readonly RestoreBackupFile[] Files =
    [
        new("Sales", @"C:\Source\Sales.mdf", RestoreFileKind.Data, "PRIMARY", 30_000_000),
        new("Sales_log", @"C:\Source\Sales_log.ldf", RestoreFileKind.Log, null, 20_000_000)
    ];

    private static RestoreBackupHeader Header(
        RestoreBackupKind kind = RestoreBackupKind.Database,
        Version? version = null,
        bool encrypted = false,
        Guid? family = null) =>
        new("Sales", kind, version ?? new Version(16, 0, 4165), "SRC", 160, "SQL_Latin1_General_CP1_CI_AS", null, null, 1, 1000, false, true, false, encrypted, family ?? Family);

    private static RestoreBackupInspection Inspection(RestoreBackupHeader? header = null, IReadOnlyList<RestoreBackupFile>? files = null) =>
        new(header ?? Header(), files ?? Files, true, null, null);

    private static SqlServerRestoreTargetFacts Target(
        SqlServerDatabaseInfo? database = null,
        SqlServerInstanceInfo? instance = null,
        bool noDirectories = false,
        IReadOnlyList<SqlServerVolume>? volumes = null,
        bool noVolumes = false) =>
        new(null, null, instance ?? MsSqlTestData.Instance(), database, noDirectories ? null : Directories, false, false, noVolumes ? null : volumes ?? Volumes);

    private static SqlServerRestorePlanEvaluation Plan(
        SqlServerRestoreTargetFacts target,
        DatabaseRestoreMode mode = DatabaseRestoreMode.NewDatabase,
        RestoreBackupInspection? inspection = null,
        Guid? targetFamily = null,
        IReadOnlyList<SqlServerMasterFile>? masterFiles = null,
        string name = "Restored") =>
        SqlServerRestorePlanner.Evaluate(
            new SqlServerRestorePlanFacts(target, targetFamily, masterFiles ?? []),
            new RestorePlanRequest(name, mode, false, inspection ?? Inspection()));

    private static BackupPreCheckItem Item(SqlServerRestorePlanEvaluation evaluation, string checkId) => Assert.Single(evaluation.Items, item => item.CheckId == checkId);

    private static SqlServerMasterFile TargetFile(string logical, string path, RestoreFileKind kind, long size) =>
        new("Sales", 7, 1, kind, logical, path, size);

    [Fact]
    public void AHealthyNewDatabasePlanPassesEverythingAndMapsIntoTheDefaultDirectories()
    {
        var evaluation = Plan(Target());

        Assert.All(evaluation.Items, item => Assert.Equal(BackupPreCheckStatus.Passed, item.Status));
        var plan = evaluation.Plan!;
        Assert.Equal(["/var/opt/mssql/data/Restored_Sales_1.mdf", "/var/opt/mssql/log/Restored_Sales_log_1.ldf"], plan.Mappings.Select(mapping => mapping.TargetPath.Value));
        Assert.NotNull(plan.Target.DefaultDataDirectory);
        Assert.NotNull(plan.Target.DefaultLogDirectory);
        Assert.Equal("/var/opt/mssql/data", plan.Target.DefaultDataDirectory.Value);
        Assert.Equal("/var/opt/mssql/log", plan.Target.DefaultLogDirectory.Value);
        Assert.False(plan.Target.DatabaseExists);
        Assert.Null(plan.Target.DatabaseState);
        Assert.Equal(DatabaseEngineId.MsSql, plan.Target.Instance.Engine);
        Assert.Empty(plan.Warnings);
        Assert.Equal(
            [
                BackupPreCheckIds.ServerReachable, BackupPreCheckIds.Authentication, SqlServerRestoreCheckIds.TargetServer,
                SqlServerRestoreCheckIds.BackupType, SqlServerRestoreCheckIds.VersionCompatibility, SqlServerRestoreCheckIds.EditionCompatibility,
                SqlServerRestoreCheckIds.DatabaseName, BackupPreCheckIds.DatabaseExists, SqlServerRestoreCheckIds.FileTypes,
                SqlServerRestoreCheckIds.Directories, SqlServerRestoreCheckIds.FileMapping, SqlServerRestoreCheckIds.DiskSpace
            ],
            evaluation.Items.Select(item => item.CheckId));
    }

    [Theory]
    [InlineData(RestoreBackupKind.Differential, false, RestoreReasonCodes.BackupTypeUnsupported)]
    [InlineData(RestoreBackupKind.TransactionLog, false, RestoreReasonCodes.BackupTypeUnsupported)]
    [InlineData(RestoreBackupKind.Other, false, RestoreReasonCodes.BackupTypeUnsupported)]
    [InlineData(RestoreBackupKind.Database, true, SqlServerRestoreReasons.BackupEncryptedUnsupported)]
    public void AnUnsupportedBackupFailsThePlanWithTheBackupTypeReasonAndYieldsNoPlan(RestoreBackupKind kind, bool encrypted, string reason)
    {
        var evaluation = Plan(Target(), inspection: Inspection(Header(kind, encrypted: encrypted)));

        var item = Item(evaluation, SqlServerRestoreCheckIds.BackupType);
        Assert.Equal((BackupPreCheckStatus.Failed, reason), (item.Status, item.ReasonCode));
        Assert.Null(evaluation.Plan);
    }

    [Fact]
    public void ABackupFromANewerMajorVersionFailsTheVersionCheckAndYieldsNoPlan()
    {
        var evaluation = Plan(Target(instance: MsSqlTestData.Instance("15.0.2000.5")), inspection: Inspection(Header(version: new Version(16, 0, 1))));

        var item = Item(evaluation, SqlServerRestoreCheckIds.VersionCompatibility);
        Assert.Equal((BackupPreCheckStatus.Failed, RestoreReasonCodes.TargetVersionTooOld), (item.Status, item.ReasonCode));
        Assert.Null(evaluation.Plan);
    }

    [Fact]
    public void ANewerBuildOfTheSameMajorVersionIsAWarningAndStillYieldsAPlan()
    {
        var evaluation = Plan(Target(instance: MsSqlTestData.Instance("16.0.1000.0")), inspection: Inspection(Header(version: new Version(16, 0, 4000))));

        Assert.Equal(BackupPreCheckStatus.Warning, Item(evaluation, SqlServerRestoreCheckIds.VersionCompatibility).Status);
        Assert.NotNull(evaluation.Plan);
    }

    [Fact]
    public void ANonEnterpriseTargetWarnsAboutEditionFeaturesButStillPlans()
    {
        var evaluation = Plan(Target(instance: MsSqlTestData.Instance(edition: "Standard Edition (64-bit)", engineEdition: 2)));

        Assert.Equal(SqlServerRestoreReasons.EditionUnknown, Item(evaluation, SqlServerRestoreCheckIds.EditionCompatibility).ReasonCode);
        Assert.NotNull(evaluation.Plan);
    }

    [Theory]
    [InlineData(DatabaseRestoreMode.NewDatabase)]
    [InlineData(DatabaseRestoreMode.IsolatedVerification)]
    public void ANewOrVerificationDatabaseWhoseNameIsTakenNowYieldsNoPlan(DatabaseRestoreMode mode)
    {
        var name = mode == DatabaseRestoreMode.IsolatedVerification ? RestoreDatabaseNames.NewVerificationName() : "Restored";

        var evaluation = Plan(Target(MsSqlTestData.Database(name)), mode, name: name);

        Assert.Equal(RestoreReasonCodes.TargetDatabaseExists, Item(evaluation, BackupPreCheckIds.DatabaseExists).ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(evaluation, SqlServerRestoreCheckIds.FileMapping).Status);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(evaluation, SqlServerRestoreCheckIds.DiskSpace).Status);
        Assert.Null(evaluation.Plan);
    }

    [Fact]
    public void AReplaceWithoutATargetYieldsNoPlan()
    {
        var evaluation = Plan(Target(), DatabaseRestoreMode.ReplaceExistingDatabase, name: "Sales");

        Assert.Equal(RestoreReasonCodes.TargetDatabaseMissing, Item(evaluation, BackupPreCheckIds.DatabaseExists).ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(evaluation, SqlServerRestoreCheckIds.TargetRelation).Status);
        Assert.Null(evaluation.Plan);
    }

    [Fact]
    public void ASystemDatabaseIsNeverPlannedOver()
    {
        var evaluation = Plan(Target(MsSqlTestData.Database("master", 1)), DatabaseRestoreMode.ReplaceExistingDatabase, name: "master");

        Assert.Equal(RestoreReasonCodes.TargetProtected, Item(evaluation, SqlServerRestoreCheckIds.TargetProtected).ReasonCode);
        Assert.Null(evaluation.Plan);
    }

    [Fact]
    public void AnIsolatedVerificationPlansLikeANewDatabaseWithoutTheReplaceBranch()
    {
        var name = RestoreDatabaseNames.NewVerificationName();

        var evaluation = Plan(Target(), DatabaseRestoreMode.IsolatedVerification, name: name);

        Assert.All(evaluation.Items, item => Assert.Equal(BackupPreCheckStatus.Passed, item.Status));
        Assert.DoesNotContain(evaluation.Items, item => item.CheckId == SqlServerRestoreCheckIds.TargetRelation);
        Assert.NotNull(evaluation.Plan);
        Assert.False(evaluation.Plan.Target.DatabaseExists);
        Assert.All(evaluation.Plan.Mappings, mapping => Assert.StartsWith(RestoreDatabaseNames.VerificationPrefix, Path.GetFileName(mapping.TargetPath.Value), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DatabaseRestoreMode.NewDatabase, "bp_verify_0123456789abcdef0123456789abcdef")]
    [InlineData(DatabaseRestoreMode.IsolatedVerification, "Sales")]
    public void AReservedNameMismatchIsNeverPlanned(DatabaseRestoreMode mode, string name)
    {
        var evaluation = Plan(Target(), mode, name: name);

        Assert.Equal(RestoreReasonCodes.TargetProtected, Item(evaluation, SqlServerRestoreCheckIds.TargetProtected).ReasonCode);
        Assert.Null(evaluation.Plan);
    }

    [Fact]
    public void ReplacingARelatedDatabaseRestoresItsFilesInPlaceAndSaysSo()
    {
        var masterFiles = new[]
        {
            TargetFile("Sales", "/mnt/disk2/Sales.mdf", RestoreFileKind.Data, 30_000_000),
            TargetFile("Sales_log", "/mnt/disk3/Sales_log.ldf", RestoreFileKind.Log, 20_000_000)
        };

        var evaluation = Plan(Target(MsSqlTestData.Database("Sales")), DatabaseRestoreMode.ReplaceExistingDatabase, targetFamily: Family, masterFiles: masterFiles, name: "Sales");

        Assert.Equal(BackupPreCheckStatus.Passed, Item(evaluation, SqlServerRestoreCheckIds.TargetRelation).Status);
        var plan = evaluation.Plan!;
        Assert.Equal(["/mnt/disk2/Sales.mdf", "/mnt/disk3/Sales_log.ldf"], plan.Mappings.Select(mapping => mapping.TargetPath.Value));
        Assert.True(plan.Target.DatabaseExists);
        Assert.Equal("ONLINE", plan.Target.DatabaseState);
        Assert.Equal(SqlServerRestoreWarningCodes.ExistingFilesReused, Assert.Single(plan.Warnings).Code);
    }

    [Theory]
    [InlineData(true, SqlServerRestoreReasons.TargetUnrelated)]
    [InlineData(false, SqlServerRestoreReasons.TargetRelationUnknown)]
    public void ReplacingAnUnrelatedOrUnknownDatabaseWarnsButStillPlans(bool known, string reason)
    {
        var evaluation = Plan(
            Target(MsSqlTestData.Database("Sales")), DatabaseRestoreMode.ReplaceExistingDatabase,
            targetFamily: known ? Guid.NewGuid() : null, name: "Sales");

        var item = Item(evaluation, SqlServerRestoreCheckIds.TargetRelation);
        Assert.Equal((BackupPreCheckStatus.Warning, reason), (item.Status, item.ReasonCode));
        Assert.NotNull(evaluation.Plan);
    }

    [Fact]
    public void ABackupWithFilestreamFilesFailsFileTypesAndSkipsTheMapping()
    {
        var files = new[] { Files[0], new RestoreBackupFile("Stream", "/x", RestoreFileKind.FileStream, "FS", 1), Files[1] };

        var evaluation = Plan(Target(), inspection: Inspection(files: files));

        var types = Item(evaluation, SqlServerRestoreCheckIds.FileTypes);
        Assert.Equal((BackupPreCheckStatus.Failed, RestoreReasonCodes.FileMappingFailed), (types.Status, types.ReasonCode));
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(evaluation, SqlServerRestoreCheckIds.FileMapping).Status);
        Assert.Null(evaluation.Plan);
    }

    [Fact]
    public void UnreadableDirectoriesFailThePlanBecauseNoFileCanBePlaced()
    {
        var evaluation = Plan(Target(noDirectories: true));

        var item = Item(evaluation, SqlServerRestoreCheckIds.Directories);
        Assert.Equal((BackupPreCheckStatus.Failed, RestoreReasonCodes.FileMappingFailed), (item.Status, item.ReasonCode));
        Assert.Null(evaluation.Plan);
    }

    [Fact]
    public void MastersDirectoryAsAFallbackIsAWarningAndStillPlans()
    {
        var target = Target() with { Directories = new SqlServerRawDirectories(null, null, "/var/opt/mssql/data/master.mdf", "/var/opt/mssql/data/mastlog.ldf") };

        var evaluation = Plan(target);

        Assert.Equal(SqlServerRestoreReasons.DirectoriesFallback, Item(evaluation, SqlServerRestoreCheckIds.Directories).ReasonCode);
        Assert.All(evaluation.Plan!.Mappings, mapping => Assert.StartsWith("/var/opt/mssql/data/", mapping.TargetPath.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreadableFileCatalogIsAWarningAndStillPlans()
    {
        var facts = new SqlServerRestorePlanFacts(Target(), null, null);

        var evaluation = SqlServerRestorePlanner.Evaluate(facts, new RestorePlanRequest("Restored", DatabaseRestoreMode.NewDatabase, false, Inspection()));

        var item = Item(evaluation, SqlServerRestoreCheckIds.FileMapping);
        Assert.Equal((BackupPreCheckStatus.Warning, SqlServerRestoreReasons.ExistingFilesUnknown), (item.Status, item.ReasonCode));
        Assert.NotNull(evaluation.Plan);
    }

    [Fact]
    public void ADestinationAnotherDatabaseOwnsIsNeverChosen()
    {
        var taken = new[] { new SqlServerMasterFile("Other", 9, 1, RestoreFileKind.Data, "x", "/var/opt/mssql/data/Restored_Sales_1.mdf", 1) };

        var evaluation = Plan(Target(), masterFiles: taken);

        Assert.Equal("/var/opt/mssql/data/Restored_Sales_2.mdf", evaluation.Plan!.Mappings[0].TargetPath.Value);
    }

    [Fact]
    public void TheExactDiskSpaceCheckFailsWhenTheFilesDoNotFit()
    {
        var volumes = new[]
        {
            new SqlServerVolume("/var/opt/mssql/data/master.mdf", AvailableBytes: 10_000_000, TotalBytes: 500_000_000),
            new SqlServerVolume("/var/opt/mssql/log/x.ldf", AvailableBytes: 100_000_000, TotalBytes: 900_000_000)
        };

        var evaluation = Plan(Target(volumes: volumes));

        var item = Item(evaluation, SqlServerRestoreCheckIds.DiskSpace);
        Assert.Equal((BackupPreCheckStatus.Failed, RestoreReasonCodes.InsufficientDiskSpace), (item.Status, item.ReasonCode));
        Assert.Null(evaluation.Plan);
    }

    [Fact]
    public void DataAndLogOnTheSameVolumeAreSummedAgainstItsFreeSpace()
    {
        // 30 MB of data + 20 MB of log need 50 MB; 45 MB are free on the one volume both directories live on.
        var volumes = new[]
        {
            new SqlServerVolume("/var/opt/mssql/data/master.mdf", AvailableBytes: 45_000_000, TotalBytes: 500_000_000),
            new SqlServerVolume("/var/opt/mssql/log/x.ldf", AvailableBytes: 45_000_000, TotalBytes: 500_000_000)
        };

        var evaluation = Plan(Target(volumes: volumes));

        Assert.Equal(BackupPreCheckStatus.Failed, Item(evaluation, SqlServerRestoreCheckIds.DiskSpace).Status);
    }

    [Fact]
    public void DataAndLogOnDifferentVolumesAreCheckedSeparately()
    {
        var volumes = new[]
        {
            new SqlServerVolume("/var/opt/mssql/data/master.mdf", AvailableBytes: 35_000_000, TotalBytes: 500_000_000),
            new SqlServerVolume("/var/opt/mssql/log/x.ldf", AvailableBytes: 25_000_000, TotalBytes: 100_000_000)
        };

        var evaluation = Plan(Target(volumes: volumes));

        Assert.Equal(BackupPreCheckStatus.Passed, Item(evaluation, SqlServerRestoreCheckIds.DiskSpace).Status);
        Assert.NotNull(evaluation.Plan);
    }

    [Fact]
    public void UnreadableVolumesAreAWarningNeverAPass()
    {
        var evaluation = Plan(Target(noVolumes: true));

        var item = Item(evaluation, SqlServerRestoreCheckIds.DiskSpace);
        Assert.Equal((BackupPreCheckStatus.Warning, SqlServerRestoreReasons.DiskSpaceUnknown), (item.Status, item.ReasonCode));
        Assert.NotNull(evaluation.Plan);
    }

    [Fact]
    public void ADirectoryWithoutAKnownVolumeIsAWarning()
    {
        var evaluation = Plan(Target(volumes: [new SqlServerVolume("/elsewhere/x.mdf", 1, 1)]));

        Assert.Equal(BackupPreCheckStatus.Warning, Item(evaluation, SqlServerRestoreCheckIds.DiskSpace).Status);
        Assert.NotNull(evaluation.Plan);
    }

    [Fact]
    public void ReplacingInPlaceOnlyNeedsTheGrowthOfTheReusedFiles()
    {
        // The existing files already occupy 29.9 MB of the 30 MB data file and 20 MB of the log: only the data file's 100 KB growth is new.
        var volumes = new[]
        {
            new SqlServerVolume("/mnt/disk2/other.mdf", AvailableBytes: 150_000, TotalBytes: 500_000_000),
            new SqlServerVolume("/mnt/disk3/other.ldf", AvailableBytes: 1, TotalBytes: 900_000_000)
        };
        var masterFiles = new[]
        {
            TargetFile("Sales", "/mnt/disk2/Sales.mdf", RestoreFileKind.Data, 29_900_000),
            TargetFile("Sales_log", "/mnt/disk3/Sales_log.ldf", RestoreFileKind.Log, 20_000_000)
        };

        var evaluation = Plan(Target(MsSqlTestData.Database("Sales"), volumes: volumes), DatabaseRestoreMode.ReplaceExistingDatabase, targetFamily: Family, masterFiles: masterFiles, name: "Sales");

        Assert.Equal(BackupPreCheckStatus.Passed, Item(evaluation, SqlServerRestoreCheckIds.DiskSpace).Status);
        Assert.NotNull(evaluation.Plan);
    }

    [Fact]
    public void AConnectionFailureYieldsTheConnectionItemsAndNoPlan()
    {
        var target = new SqlServerRestoreTargetFacts(new DatabaseProviderException(DatabaseFailureKind.Unreachable, "down"), null, null, null, null, null, null, null);

        var evaluation = Plan(target);

        Assert.Equal(BackupPreCheckStatus.Failed, Item(evaluation, BackupPreCheckIds.ServerReachable).Status);
        Assert.All(evaluation.Items.Where(item => item.CheckId != BackupPreCheckIds.ServerReachable), item => Assert.Equal(BackupPreCheckStatus.Skipped, item.Status));
        Assert.Null(evaluation.Plan);
    }

    [Fact]
    public void UnreadableMetadataYieldsAFailedServerItemAndNoPlan()
    {
        var target = new SqlServerRestoreTargetFacts(null, new DatabaseProviderException(DatabaseFailureKind.Unexpected, "meta"), null, null, null, null, null, null);

        var evaluation = Plan(target);

        Assert.Equal(SqlServerPreCheckReasons.MetadataUnavailable, Item(evaluation, SqlServerRestoreCheckIds.TargetServer).ReasonCode);
        Assert.Null(evaluation.Plan);
    }

    [Fact]
    public void ThePlanExistsExactlyWhenNoItemFailedSoItAlwaysFitsTheResultContract()
    {
        var scenarios = new[]
        {
            Plan(Target()),
            Plan(Target(MsSqlTestData.Database("Restored"))),
            Plan(Target(), inspection: Inspection(Header(RestoreBackupKind.Differential))),
            Plan(Target(noDirectories: true)),
            Plan(Target(noVolumes: true)),
            Plan(Target(), name: "bad\nname")
        };

        foreach (var evaluation in scenarios)
        {
            var report = new BackupPreCheckReport(evaluation.Items);
            var result = new RestorePlanResult(report, evaluation.Plan);

            Assert.Equal(report.CanProceed, result.Plan is not null);
        }
    }

    [Fact]
    public void AnInvalidNameFailsThePlanWithoutMapping()
    {
        var evaluation = Plan(Target(), name: "bad\nname");

        Assert.Equal(SqlServerRestoreReasons.DatabaseNameInvalid, Item(evaluation, SqlServerRestoreCheckIds.DatabaseName).ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(evaluation, SqlServerRestoreCheckIds.FileMapping).Status);
        Assert.Null(evaluation.Plan);
    }

    [Fact]
    public void FormatsOfTheDiskSpaceMessagesNameBothSizes()
    {
        var volumes = new[] { new SqlServerVolume("/var/opt/mssql/data/master.mdf", AvailableBytes: 1024 * 1024, TotalBytes: 1L << 40), new SqlServerVolume("/var/opt/mssql/log/x.ldf", AvailableBytes: 1L << 40, TotalBytes: 1L << 41) };

        var evaluation = Plan(Target(volumes: volumes));

        var message = Item(evaluation, SqlServerRestoreCheckIds.DiskSpace).Message;
        Assert.Contains("MiB", message, StringComparison.Ordinal);
        Assert.Contains("1 MiB free", message, StringComparison.Ordinal);
    }
}
