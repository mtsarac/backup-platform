using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.Restore;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerRestoreOutcomeRulesTests
{
    private static readonly DateTime Created = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);

    private static SqlServerObservedDatabase Db(
        SqlServerDatabaseState state = SqlServerDatabaseState.Online,
        SqlServerUserAccess access = SqlServerUserAccess.MultiUser,
        int id = 7,
        DateTime? created = null) =>
        new(id, created ?? Created, state, access, SqlServerRestoreOutcomeRules.StateText(state));

    private static SqlServerRestoreOutcome Determine(
        DatabaseRestoreMode mode,
        bool issued,
        bool succeeded,
        bool accessChanged,
        SqlServerObservedDatabase? before,
        SqlServerObservedDatabase? after,
        bool observable = true) =>
        SqlServerRestoreOutcomeRules.Determine(new SqlServerRestoreOutcomeInput(mode, issued, succeeded, accessChanged, before, observable, after));

    [Fact]
    public void ASuccessfulRestoreOfAnOnlineMultiUserDatabaseIsOnline()
    {
        var outcome = Determine(DatabaseRestoreMode.NewDatabase, true, true, false, null, Db());

        Assert.Equal(DatabaseRestoreTargetState.Online, outcome.TargetState);
        Assert.Equal("ONLINE", outcome.DatabaseState);
        Assert.Empty(outcome.Warnings);
    }

    [Theory]
    [InlineData(SqlServerDatabaseState.Restoring, SqlServerUserAccess.MultiUser)]
    [InlineData(SqlServerDatabaseState.Online, SqlServerUserAccess.SingleUser)]
    [InlineData(SqlServerDatabaseState.Online, SqlServerUserAccess.RestrictedUser)]
    [InlineData(SqlServerDatabaseState.Offline, SqlServerUserAccess.MultiUser)]
    public void ASuccessfulStatementIsOnlineOnlyForAnOnlineMultiUserDatabase(SqlServerDatabaseState state, SqlServerUserAccess access)
    {
        var outcome = Determine(DatabaseRestoreMode.NewDatabase, true, true, false, null, Db(state, access));

        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, outcome.TargetState);
        Assert.Contains(outcome.Warnings, warning => warning.Code == SqlServerRestoreWarningCodes.TargetStateUnexpected);
    }

    [Fact]
    public void ASuccessfulStatementWhoseDatabaseCannotBeFoundIsNotOnline()
    {
        var outcome = Determine(DatabaseRestoreMode.NewDatabase, true, true, false, null, null);

        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, outcome.TargetState);
        Assert.Null(outcome.DatabaseState);
    }

    [Fact]
    public void NothingIssuedIsNotModifiedWhateverTheTargetLooksLike()
    {
        Assert.Equal(DatabaseRestoreTargetState.NotModified, Determine(DatabaseRestoreMode.NewDatabase, false, false, false, null, null).TargetState);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, Determine(DatabaseRestoreMode.ReplaceExistingDatabase, false, false, false, Db(), Db()).TargetState);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, Determine(DatabaseRestoreMode.ReplaceExistingDatabase, false, false, false, Db(), null, observable: false).TargetState);
    }

    [Fact]
    public void AnUnreadableTargetAfterSomethingWasIssuedIsUnknown()
    {
        var restored = Determine(DatabaseRestoreMode.NewDatabase, true, false, false, null, null, observable: false);
        var altered = Determine(DatabaseRestoreMode.ReplaceExistingDatabase, false, false, true, Db(), null, observable: false);

        Assert.Equal(DatabaseRestoreTargetState.Unknown, restored.TargetState);
        Assert.Equal(DatabaseRestoreTargetState.Unknown, altered.TargetState);
        Assert.Null(restored.DatabaseState);
        Assert.Contains(restored.Warnings, warning => warning.Code == SqlServerRestoreWarningCodes.TargetStateUnreadable);
    }

    [Fact]
    public void AFailedRestoreOfANewDatabaseThatWasNeverCreatedIsNotModified()
    {
        var outcome = Determine(DatabaseRestoreMode.NewDatabase, true, false, false, null, null);

        Assert.Equal(DatabaseRestoreTargetState.NotModified, outcome.TargetState);
        Assert.Null(outcome.DatabaseState);
    }

    [Fact]
    public void ACancelledRestoreOfANewDatabaseThatExistsInRestoringStateIsPartiallyRestoredAndSaysSo()
    {
        var outcome = Determine(DatabaseRestoreMode.NewDatabase, true, false, false, null, Db(SqlServerDatabaseState.Restoring));

        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, outcome.TargetState);
        Assert.Equal("RESTORING", outcome.DatabaseState);
        var warning = Assert.Single(outcome.Warnings);
        Assert.Equal(SqlServerRestoreWarningCodes.DatabaseLeftRestoring, warning.Code);
        Assert.Contains("RESTORING", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedReplaceWhoseDatabaseIsStillTheSameOnlineDatabaseWasRejectedBeforeItWasTouched()
    {
        var outcome = Determine(DatabaseRestoreMode.ReplaceExistingDatabase, true, false, true, Db(), Db());

        Assert.Equal(DatabaseRestoreTargetState.NotModified, outcome.TargetState);
        Assert.Equal("ONLINE", outcome.DatabaseState);
        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public void AFailedReplaceThatLeftTheDatabaseSingleUserIsPartiallyRestoredAndSaysWhy()
    {
        var outcome = Determine(DatabaseRestoreMode.ReplaceExistingDatabase, true, false, true, Db(), Db(access: SqlServerUserAccess.SingleUser));

        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, outcome.TargetState);
        Assert.Contains(outcome.Warnings, warning => warning.Code == SqlServerRestoreWarningCodes.DatabaseLeftSingleUser);
    }

    [Theory]
    [InlineData(SqlServerDatabaseState.Restoring)]
    [InlineData(SqlServerDatabaseState.Offline)]
    [InlineData(SqlServerDatabaseState.Suspect)]
    [InlineData(SqlServerDatabaseState.RecoveryPending)]
    public void AFailedReplaceThatLeftTheDatabaseInAnyOtherStateIsPartiallyRestored(SqlServerDatabaseState state)
    {
        var outcome = Determine(DatabaseRestoreMode.ReplaceExistingDatabase, true, false, false, Db(), Db(state));

        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, outcome.TargetState);
        Assert.Equal(SqlServerRestoreOutcomeRules.StateText(state), outcome.DatabaseState);
        Assert.Equal(state == SqlServerDatabaseState.Restoring, outcome.Warnings.Any(warning => warning.Code == SqlServerRestoreWarningCodes.DatabaseLeftRestoring));
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(7, 2)]
    public void AFailedReplaceWhoseDatabaseChangedIdentityIsPartiallyRestored(int newId, int createdDaysLater)
    {
        var after = Db(id: newId, created: Created.AddDays(createdDaysLater - 1));

        var outcome = Determine(DatabaseRestoreMode.ReplaceExistingDatabase, true, false, false, Db(), after);

        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, outcome.TargetState);
    }

    [Fact]
    public void AFailedReplaceWhoseDatabaseIsGoneIsPartiallyRestoredBecauseTheOldContentsAreGone()
    {
        var outcome = Determine(DatabaseRestoreMode.ReplaceExistingDatabase, true, false, false, Db(), null);

        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, outcome.TargetState);
        Assert.Null(outcome.DatabaseState);
        Assert.Contains(outcome.Warnings, warning => warning.Code == SqlServerRestoreWarningCodes.DatabaseMissingAfterRestore);
    }

    [Fact]
    public void OnlyTheAccessChangeWasMadeAndUndoneIsNotModified()
    {
        var outcome = Determine(DatabaseRestoreMode.ReplaceExistingDatabase, false, false, true, Db(), Db());

        Assert.Equal(DatabaseRestoreTargetState.NotModified, outcome.TargetState);
        Assert.Equal("ONLINE", outcome.DatabaseState);
    }

    [Fact]
    public void OnlyTheAccessChangeWasMadeAndNotUndoneIsReportedNotHidden()
    {
        var outcome = Determine(DatabaseRestoreMode.ReplaceExistingDatabase, false, false, true, Db(), Db(access: SqlServerUserAccess.SingleUser));

        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, outcome.TargetState);
        Assert.Contains(outcome.Warnings, warning => warning.Code == SqlServerRestoreWarningCodes.DatabaseLeftSingleUser);
    }

    [Fact]
    public void ACancelledReplaceMidRestoreThatLeftTheDatabaseRestoringIsNeverNotModified()
    {
        var outcome = Determine(DatabaseRestoreMode.ReplaceExistingDatabase, true, false, true, Db(), Db(SqlServerDatabaseState.Restoring));

        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, outcome.TargetState);
        Assert.Equal("RESTORING", outcome.DatabaseState);
    }

    [Theory]
    [InlineData(SqlServerDatabaseState.Online, "ONLINE")]
    [InlineData(SqlServerDatabaseState.Restoring, "RESTORING")]
    [InlineData(SqlServerDatabaseState.Recovering, "RECOVERING")]
    [InlineData(SqlServerDatabaseState.RecoveryPending, "RECOVERY_PENDING")]
    [InlineData(SqlServerDatabaseState.Suspect, "SUSPECT")]
    [InlineData(SqlServerDatabaseState.Emergency, "EMERGENCY")]
    [InlineData(SqlServerDatabaseState.Offline, "OFFLINE")]
    [InlineData(SqlServerDatabaseState.Unknown, null)]
    public void StateTextMatchesTheCatalogsStateDescriptions(SqlServerDatabaseState state, string? text) =>
        Assert.Equal(text, SqlServerRestoreOutcomeRules.StateText(state));
}
