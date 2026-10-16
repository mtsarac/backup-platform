using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.UnitTests.MsSql;

/// <summary>The invariants of the typed restore models a provider returns: a result cannot claim a state its status contradicts.</summary>
public sealed class RestoreModelsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private static RestoreFileMapping Mapping(string logical = "Sales", string target = "/data/Sales_1.mdf") =>
        new(logical, RestoreFileKind.Data, "/source/Sales.mdf", new DatabaseServerPath(target), 1024);

    private static RestoreTargetInfo Target() =>
        new(new DatabaseInstanceInfo(DatabaseEngineId.MsSql, "SRV", null, "16.0", null, "Linux"), new DatabaseServerPath("/data"), new DatabaseServerPath("/log"), false, null);

    private static RestoreFailure Failure(RestoreFailureKind kind = RestoreFailureKind.SqlRestoreFailed) =>
        new(kind, RestoreExecutionStage.Restoring, "restore-failed", "It failed.");

    private static DatabaseRestoreResult Result(DatabaseRestoreStatus status, RestoreFailure? failure, DatabaseRestoreTargetState state) =>
        new(status, failure, state, "ONLINE", Now, Now.AddMinutes(1), Now, Now.AddMinutes(1), [Mapping()], []);

    [Fact]
    public void ASuccessfulResultNeedsNoFailureAndAnOnlineTarget()
    {
        var result = Result(DatabaseRestoreStatus.Succeeded, null, DatabaseRestoreTargetState.Online);

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.Null(result.Failure);
        Assert.Equal("ONLINE", result.DatabaseState);
        Assert.Single(result.FileMappings);
    }

    [Theory]
    [InlineData(DatabaseRestoreTargetState.NotModified)]
    [InlineData(DatabaseRestoreTargetState.PartiallyRestored)]
    [InlineData(DatabaseRestoreTargetState.Unknown)]
    public void ASuccessCannotClaimATargetThatIsNotOnline(DatabaseRestoreTargetState state) =>
        Assert.Throws<ArgumentException>(() => Result(DatabaseRestoreStatus.Succeeded, null, state));

    [Fact]
    public void ASuccessCannotCarryAFailure() =>
        Assert.Throws<ArgumentException>(() => Result(DatabaseRestoreStatus.Succeeded, Failure(), DatabaseRestoreTargetState.Online));

    [Fact]
    public void AFailedResultNeedsAFailureAndCannotBeOnline()
    {
        Assert.Throws<ArgumentException>(() => Result(DatabaseRestoreStatus.Failed, null, DatabaseRestoreTargetState.NotModified));
        Assert.Throws<ArgumentException>(() => Result(DatabaseRestoreStatus.Failed, Failure(), DatabaseRestoreTargetState.Online));

        var result = Result(DatabaseRestoreStatus.Failed, Failure(), DatabaseRestoreTargetState.PartiallyRestored);

        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, result.TargetState);
    }

    [Fact]
    public void ACancelledResultNeedsACancelledFailureAndAFailedOneCannotHaveOne()
    {
        var cancelled = Result(DatabaseRestoreStatus.Cancelled, Failure(RestoreFailureKind.Cancelled), DatabaseRestoreTargetState.PartiallyRestored);

        Assert.Equal(DatabaseRestoreStatus.Cancelled, cancelled.Status);
        Assert.Throws<ArgumentException>(() => Result(DatabaseRestoreStatus.Cancelled, Failure(), DatabaseRestoreTargetState.PartiallyRestored));
        Assert.Throws<ArgumentException>(() => Result(DatabaseRestoreStatus.Failed, Failure(RestoreFailureKind.Cancelled), DatabaseRestoreTargetState.PartiallyRestored));
    }

    [Fact]
    public void AResultRejectsUndefinedEnumsAndNullCollections()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Result((DatabaseRestoreStatus)99, null, DatabaseRestoreTargetState.Online));
        Assert.Throws<ArgumentOutOfRangeException>(() => Result(DatabaseRestoreStatus.Failed, Failure(), (DatabaseRestoreTargetState)99));
        Assert.Throws<ArgumentNullException>(() => new DatabaseRestoreResult(DatabaseRestoreStatus.Failed, Failure(), DatabaseRestoreTargetState.Unknown, null, Now, Now, null, null, null!, []));
        Assert.Throws<ArgumentNullException>(() => new DatabaseRestoreResult(DatabaseRestoreStatus.Failed, Failure(), DatabaseRestoreTargetState.Unknown, null, Now, Now, null, null, [], null!));
    }

    [Fact]
    public void ARestorePlanAllowsLogicalPlacementButRequiresUniquePhysicalDestinationsAndNames()
    {
        Assert.Empty(new RestorePlan([], Target()).Mappings);
        Assert.Throws<ArgumentException>(() => new RestorePlan([Mapping("A", "/d/x.mdf"), Mapping("B", "/D/X.MDF")], Target()));
        Assert.Throws<ArgumentException>(() => new RestorePlan([Mapping("A", "/d/x.mdf"), Mapping("a", "/d/y.mdf")], Target()));
        Assert.Throws<ArgumentNullException>(() => new RestorePlan(null!, Target()));
        Assert.Throws<ArgumentNullException>(() => new RestorePlan([Mapping()], null!));

        var plan = new RestorePlan([Mapping("A", "/d/x.mdf"), Mapping("B", "/d/y.mdf")], Target(), [new BackupWarning("w", "message")]);

        Assert.Equal(2, plan.Mappings.Count);
        Assert.Equal("w", Assert.Single(plan.Warnings).Code);
        Assert.Empty(new RestorePlan([Mapping()], Target()).Warnings);
    }

    [Fact]
    public void AnInspectionResultIsExactlyOneOfInspectionOrFailure()
    {
        var header = new RestoreBackupHeader("Sales", RestoreBackupKind.Database, new Version(16, 0, 1), "SRC", 160, null, null, null, 1, 10, false, true, false, false, null);
        var inspection = new RestoreBackupInspection(header, [new RestoreBackupFile("Sales", "/x.mdf", RestoreFileKind.Data, "PRIMARY", 1)], true, Now, Now);
        var failure = new RestoreFailure(RestoreFailureKind.BackupInvalid, RestoreExecutionStage.InspectingBackup, "backup-media-invalid", "Invalid.", BackupFailureCause.Unclassified);

        var ok = new RestoreInspectionResult(inspection, null, [new BackupWarning("w", "m")]);
        var bad = new RestoreInspectionResult(null, failure);

        Assert.Same(inspection, ok.Inspection);
        Assert.Single(ok.Warnings);
        Assert.Same(failure, bad.Failure);
        Assert.Throws<ArgumentException>(() => new RestoreInspectionResult(null, null));
        Assert.Throws<ArgumentException>(() => new RestoreInspectionResult(inspection, failure));
    }

    [Fact]
    public void AFailureNeedsAStableReasonAndAMessageAndKnownEnums()
    {
        Assert.Throws<ArgumentException>(() => new RestoreFailure(RestoreFailureKind.BackupInvalid, RestoreExecutionStage.Restoring, " ", "m"));
        Assert.Throws<ArgumentException>(() => new RestoreFailure(RestoreFailureKind.BackupInvalid, RestoreExecutionStage.Restoring, "r", " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestoreFailure((RestoreFailureKind)99, RestoreExecutionStage.Restoring, "r", "m"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestoreFailure(RestoreFailureKind.BackupInvalid, (RestoreExecutionStage)99, "r", "m"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestoreFailure(RestoreFailureKind.BackupInvalid, RestoreExecutionStage.Restoring, "r", "m", (BackupFailureCause)99));
        Assert.Equal(BackupFailureCause.Unclassified, new RestoreFailure(RestoreFailureKind.BackupInvalid, RestoreExecutionStage.Restoring, "r", "m").Cause);
    }

    [Fact]
    public void ARestoreRequestRefusesUnknownModesAndTimeoutsOutsideItsBounds()
    {
        var header = new RestoreBackupHeader("Sales", RestoreBackupKind.Database, null, null, null, null, null, null, 1, 10, false, true, false, false, null);
        var inspection = new RestoreBackupInspection(header, [new RestoreBackupFile("Sales", "/x.mdf", RestoreFileKind.Data, null, 1)], false, null, null);
        var plan = new RestorePlan([Mapping()], Target());
        var file = new DatabaseServerPath("/staging/x.bak");

        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseRestoreRequest("Sales", file, (DatabaseRestoreMode)9, false, inspection, plan));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseRestoreRequest("Sales", file, DatabaseRestoreMode.NewDatabase, false, inspection, plan, TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseRestoreRequest("Sales", file, DatabaseRestoreMode.NewDatabase, false, inspection, plan, TimeSpan.FromDays(8)));
        Assert.Throws<ArgumentException>(() => new DatabaseRestoreRequest(" ", file, DatabaseRestoreMode.NewDatabase, false, inspection, plan));

        var request = new DatabaseRestoreRequest("Sales", file, DatabaseRestoreMode.ReplaceExistingDatabase, true, inspection, plan, TimeSpan.FromMilliseconds(1500));

        Assert.Equal(TimeSpan.FromSeconds(2), request.RestoreTimeout);
        Assert.Equal(DatabaseRestoreRequest.DefaultRestoreTimeout, new DatabaseRestoreRequest("Sales", file, DatabaseRestoreMode.NewDatabase, false, inspection, plan).RestoreTimeout);
    }

    [Fact]
    public void ARestoreProgressValidatesStageAndPercent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestoreProgress((RestoreExecutionStage)99, null, null, null, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestoreProgress(RestoreExecutionStage.Restoring, -1, null, null, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestoreProgress(RestoreExecutionStage.Restoring, 100.5, null, null, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestoreProgress(RestoreExecutionStage.Restoring, double.NaN, null, null, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestoreProgress(RestoreExecutionStage.Restoring, 5, -1, null, Now));

        var progress = new RestoreProgress(RestoreExecutionStage.Restoring, 42, null, null, Now);

        Assert.Equal(42, progress.Percent);
    }

    [Fact]
    public void APreCheckRequestRefusesBlankNamesUnknownModesAndNegativeSizes()
    {
        Assert.Throws<ArgumentException>(() => new RestorePreCheckRequest(" ", DatabaseRestoreMode.NewDatabase, false, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestorePreCheckRequest("Sales", (DatabaseRestoreMode)0, false, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestorePreCheckRequest("Sales", DatabaseRestoreMode.NewDatabase, false, -1));

        var request = new RestorePreCheckRequest("Sales", DatabaseRestoreMode.ReplaceExistingDatabase, true, 10);

        Assert.True(request.TerminateActiveConnections);
        Assert.Equal(10, request.BackupSizeBytes);
    }

    [Fact]
    public void AnInspectionRequestRoundsItsTimeoutUpToWholeSecondsAndBoundsIt()
    {
        var file = new DatabaseServerPath("/staging/x.bak");

        Assert.Equal(TimeSpan.FromSeconds(2), new RestoreInspectionRequest(file, true, TimeSpan.FromMilliseconds(1100)).VerifyTimeout);
        Assert.Equal(RestoreInspectionRequest.DefaultVerifyTimeout, new RestoreInspectionRequest(file, false).VerifyTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestoreInspectionRequest(file, true, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestoreInspectionRequest(file, true, TimeSpan.FromDays(8)));
    }
}
