using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Restore;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerRestoreFailureClassifierTests
{
    private static SqlServerRestoreStatement Statement(string name) => Enum.Parse<SqlServerRestoreStatement>(name);

    private static SqlServerRestoreFailureInfo Classify(SqlServerRestoreStatement statement, params int[] numbers) =>
        SqlServerRestoreFailureClassifier.Classify(statement, numbers);

    [Theory]
    [InlineData("HeaderOnly", 3201)]
    [InlineData("FileListOnly", 3201)]
    [InlineData("VerifyOnly", 3203)]
    [InlineData("Restore", 3201)]
    [InlineData("Restore", 3203)]
    public void AnUnopenableOrUnreadableFileIsAStagingProblemOfTheServersAccess(string statementName, int number)
    {
        var info = Classify(Statement(statementName), number, 3013);

        Assert.Equal(SqlServerRestoreReasons.BackupFileUnreadable, info.ReasonCode);
        Assert.Equal(BackupFailureCause.StagingInvalid, info.Cause);
        Assert.Equal(RestoreFailureKind.BackupInvalid, info.Kind);
        Assert.Contains("service account", info.Message, StringComparison.Ordinal);
        Assert.Contains("staging", info.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3241)]
    [InlineData(3242)]
    [InlineData(3243)]
    [InlineData(3254)]
    public void MediaThatIsNotABackupIsInvalid(int number)
    {
        foreach (var statement in Enum.GetValues<SqlServerRestoreStatement>().Where(s => s != SqlServerRestoreStatement.AccessChange))
        {
            var info = Classify(statement, number, 3013);

            Assert.Equal(SqlServerRestoreReasons.BackupMediaInvalid, info.ReasonCode);
            Assert.Equal(RestoreFailureKind.BackupInvalid, info.Kind);
            Assert.Equal(BackupFailureCause.Unclassified, info.Cause);
        }
    }

    [Theory]
    [InlineData(3183)]
    [InlineData(3189)]
    [InlineData(3043)]
    public void DamagedDataInTheBackupIsAChecksumMismatch(int number)
    {
        var info = Classify(SqlServerRestoreStatement.VerifyOnly, number);

        Assert.Equal(SqlServerRestoreReasons.BackupChecksumMismatch, info.ReasonCode);
        Assert.Equal(RestoreFailureKind.BackupInvalid, info.Kind);
    }

    [Fact]
    public void RequestingChecksumOnABackupWithoutThemIsNotReportedAsDamage()
    {
        // 3187 means "WITH CHECKSUM cannot be specified because the backup set does not contain checksum information".
        var info = Classify(SqlServerRestoreStatement.Restore, 3187);

        Assert.Equal(SqlServerRestoreReasons.RestoreFailed, info.ReasonCode);
    }

    [Fact]
    public void ANewerBackupVersionIsTheTargetVersionTooOldWithAnUnsupportedCause()
    {
        foreach (var statement in new[] { SqlServerRestoreStatement.FileListOnly, SqlServerRestoreStatement.VerifyOnly, SqlServerRestoreStatement.Restore })
        {
            var info = Classify(statement, 3169);

            Assert.Equal(RestoreReasonCodes.TargetVersionTooOld, info.ReasonCode);
            Assert.Equal(BackupFailureCause.Unsupported, info.Cause);
        }
    }

    [Theory]
    [InlineData(33111)]
    [InlineData(33049)]
    public void AMissingEncryptionKeyIsUnsupportedHere(int number)
    {
        var info = Classify(SqlServerRestoreStatement.Restore, number);

        Assert.Equal(SqlServerRestoreReasons.BackupEncryptionKeyMissing, info.ReasonCode);
        Assert.Equal(BackupFailureCause.Unsupported, info.Cause);
    }

    [Theory]
    [InlineData(229)]
    [InlineData(230)]
    [InlineData(262)]
    [InlineData(297)]
    [InlineData(300)]
    [InlineData(916)]
    public void PermissionErrorsAreAPermissionCauseForEveryStatement(int number)
    {
        foreach (var statement in Enum.GetValues<SqlServerRestoreStatement>())
        {
            var info = Classify(statement, number);

            Assert.Equal(SqlServerRestoreReasons.PermissionDenied, info.ReasonCode);
            Assert.Equal(BackupFailureCause.PermissionDenied, info.Cause);
        }

        Assert.Equal(RestoreFailureKind.SqlRestoreFailed, Classify(SqlServerRestoreStatement.Restore, number).Kind);
        Assert.Equal(RestoreFailureKind.PreCheckFailed, Classify(SqlServerRestoreStatement.HeaderOnly, number).Kind);
    }

    [Fact]
    public void AnAccessChangeThatTheLoginMayNotMakeIsAPermissionProblem()
    {
        var info = Classify(SqlServerRestoreStatement.AccessChange, 5011, 5069);

        Assert.Equal(SqlServerRestoreReasons.PermissionDenied, info.ReasonCode);
        Assert.Equal(BackupFailureCause.PermissionDenied, info.Cause);
    }

    [Theory]
    [InlineData(5052)]
    [InlineData(5070)]
    public void AnAccessChangeThatTheStateRefusesIsATargetAccessChangeFailure(int number)
    {
        var info = Classify(SqlServerRestoreStatement.AccessChange, number, 5069);

        Assert.Equal(SqlServerRestoreReasons.TargetAccessChangeFailed, info.ReasonCode);
        Assert.Equal(RestoreFailureKind.SqlRestoreFailed, info.Kind);
    }

    [Theory]
    [InlineData(-2, "Restore", SqlServerRestoreReasons.RestoreTimeout)]
    [InlineData(-2, "VerifyOnly", SqlServerRestoreReasons.VerifyTimeout)]
    [InlineData(-2, "HeaderOnly", SqlServerRestoreReasons.InspectTimeout)]
    [InlineData(-2, "FileListOnly", SqlServerRestoreReasons.InspectTimeout)]
    [InlineData(-2, "AccessChange", SqlServerRestoreReasons.RestoreTimeout)]
    public void ATimeoutIsATimeoutCauseWithAStatementSpecificReason(int number, string statementName, string reason)
    {
        var info = Classify(Statement(statementName), number);

        Assert.Equal(reason, info.ReasonCode);
        Assert.Equal(BackupFailureCause.Timeout, info.Cause);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    [InlineData(121)]
    [InlineData(233)]
    [InlineData(10054)]
    [InlineData(10060)]
    public void ALostConnectionIsDatabaseUnavailable(int number)
    {
        var info = Classify(SqlServerRestoreStatement.Restore, number);

        Assert.Equal(SqlServerRestoreReasons.ConnectionLost, info.ReasonCode);
        Assert.Equal(BackupFailureCause.DatabaseUnavailable, info.Cause);
    }

    [Theory]
    [InlineData(3101, RestoreReasonCodes.TargetInUse)]
    [InlineData(50001, RestoreReasonCodes.TargetDatabaseExists)]
    [InlineData(3154, RestoreReasonCodes.TargetDatabaseExists)]
    [InlineData(3159, RestoreReasonCodes.TargetDatabaseExists)]
    [InlineData(3257, RestoreReasonCodes.InsufficientDiskSpace)]
    [InlineData(3156, SqlServerRestoreReasons.RestoreDestinationInvalid)]
    [InlineData(5133, SqlServerRestoreReasons.RestoreDestinationInvalid)]
    [InlineData(1834, SqlServerRestoreReasons.RestoreDestinationExists)]
    [InlineData(3142, SqlServerRestoreReasons.RestoreDestinationExists)]
    public void RestoreErrorsMapToTheirStableReasons(int number, string reason) =>
        Assert.Equal(reason, Classify(SqlServerRestoreStatement.Restore, number, 3119, 3013).ReasonCode);

    [Theory]
    [InlineData(3101)]
    [InlineData(3156)]
    [InlineData(3257)]
    [InlineData(1834)]
    public void RestoreOnlyErrorsAreNotInventedForTheInspectionStatements(int number) =>
        Assert.Equal(SqlServerRestoreReasons.InspectFailed, Classify(SqlServerRestoreStatement.HeaderOnly, number).ReasonCode);

    [Fact]
    public void TheTargetExistsGuardIsAPreCheckKindFailureNotARestoreFailure()
    {
        var info = Classify(SqlServerRestoreStatement.Restore, SqlServerRestoreCommandBuilder.TargetExistsErrorNumber);

        Assert.Equal(RestoreFailureKind.PreCheckFailed, info.Kind);
        Assert.Equal(RestoreReasonCodes.TargetDatabaseExists, info.ReasonCode);
    }

    [Fact]
    public void TheCauseDecidesNotTheGenericErrorsThatFollowIt()
    {
        // Observed shapes of real failures: the cause first, then 3119 / 3013.
        Assert.Equal(SqlServerRestoreReasons.RestoreDestinationInvalid, Classify(SqlServerRestoreStatement.Restore, 3634, 3156, 3634, 3156, 3119, 3013).ReasonCode);
        Assert.Equal(SqlServerRestoreReasons.RestoreDestinationExists, Classify(SqlServerRestoreStatement.Restore, 1834, 3156, 3119, 3013).ReasonCode);
        Assert.Equal(SqlServerRestoreReasons.BackupMediaInvalid, Classify(SqlServerRestoreStatement.HeaderOnly, 3254, 3013).ReasonCode);
        Assert.Equal(SqlServerRestoreReasons.BackupFileUnreadable, Classify(SqlServerRestoreStatement.HeaderOnly, 3201, 3013).ReasonCode);
    }

    [Theory]
    [InlineData("HeaderOnly", SqlServerRestoreReasons.InspectFailed, RestoreFailureKind.BackupInvalid)]
    [InlineData("FileListOnly", SqlServerRestoreReasons.InspectFailed, RestoreFailureKind.BackupInvalid)]
    [InlineData("VerifyOnly", SqlServerRestoreReasons.VerifyFailed, RestoreFailureKind.BackupInvalid)]
    [InlineData("Restore", SqlServerRestoreReasons.RestoreFailed, RestoreFailureKind.SqlRestoreFailed)]
    [InlineData("AccessChange", SqlServerRestoreReasons.TargetAccessChangeFailed, RestoreFailureKind.SqlRestoreFailed)]
    public void UnknownOrNoErrorNumbersAreTheGenericFailureOfTheStatement(string statementName, string reason, RestoreFailureKind kind)
    {
        var statement = Statement(statementName);
        foreach (var numbers in new[] { Array.Empty<int>(), [3013], [99999, 3119] })
        {
            var info = SqlServerRestoreFailureClassifier.Classify(statement, numbers);

            Assert.Equal(reason, info.ReasonCode);
            Assert.Equal(kind, info.Kind);
            Assert.Equal(BackupFailureCause.Unclassified, info.Cause);
        }
    }

    [Fact]
    public void MessagesAreFixedSentencesWithoutPathsOrEngineText()
    {
        foreach (var statement in Enum.GetValues<SqlServerRestoreStatement>())
        {
            foreach (var number in new[] { -2, -1, 262, 3201, 3203, 3241, 3254, 3183, 3169, 33111, 3101, 50001, 3156, 1834, 3257, 5011, 5052, 12345 })
            {
                var info = Classify(statement, number);

                Assert.False(string.IsNullOrWhiteSpace(info.ReasonCode));
                Assert.DoesNotContain("/", info.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("\\", info.Message, StringComparison.Ordinal);
                Assert.EndsWith(".", info.Message, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(DatabaseFailureKind.AuthenticationFailed, SqlServerRestoreReasons.AuthenticationFailed, BackupFailureCause.AuthenticationFailed)]
    [InlineData(DatabaseFailureKind.PermissionDenied, SqlServerRestoreReasons.PermissionDenied, BackupFailureCause.PermissionDenied)]
    [InlineData(DatabaseFailureKind.Timeout, SqlServerRestoreReasons.ConnectionTimeout, BackupFailureCause.Timeout)]
    [InlineData(DatabaseFailureKind.InvalidConfiguration, SqlServerRestoreReasons.ConnectionSettingsInvalid, BackupFailureCause.InvalidConfiguration)]
    [InlineData(DatabaseFailureKind.Unreachable, SqlServerRestoreReasons.ServerUnavailable, BackupFailureCause.DatabaseUnavailable)]
    [InlineData(DatabaseFailureKind.Unexpected, SqlServerRestoreReasons.UnexpectedError, BackupFailureCause.Unclassified)]
    public void AConnectionFailureKeepsItsUserSafeMessageAndMapsItsKindToAReasonAndCause(DatabaseFailureKind kind, string reason, BackupFailureCause cause)
    {
        var info = SqlServerRestoreFailureClassifier.FromConnection(new DatabaseProviderException(kind, "user safe text."));

        Assert.Equal(reason, info.ReasonCode);
        Assert.Equal(cause, info.Cause);
        Assert.Equal("user safe text.", info.Message);
        Assert.Equal(RestoreFailureKind.PreCheckFailed, info.Kind);
    }

    [Theory]
    [InlineData(DatabaseFailureKind.Timeout, SqlServerRestoreReasons.RestoreTimeout)]
    [InlineData(DatabaseFailureKind.PermissionDenied, SqlServerRestoreReasons.PermissionDenied)]
    [InlineData(DatabaseFailureKind.Unreachable, SqlServerRestoreReasons.ConnectionLost)]
    [InlineData(DatabaseFailureKind.Unexpected, SqlServerRestoreReasons.RestoreFailed)]
    public void AProviderExceptionWithoutAnEngineErrorFallsBackToItsKind(DatabaseFailureKind kind, string reason) =>
        Assert.Equal(reason, SqlServerRestoreFailureClassifier.Classify(SqlServerRestoreStatement.Restore, new DatabaseProviderException(kind, "m.")).ReasonCode);
}
