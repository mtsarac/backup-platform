using BackupPlatform.BackupProviders.MSSQL.Backup;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerBackupFailureClassifierTests
{
    [Theory]
    [InlineData(-2, SqlServerBackupReasons.BackupTimeout)]
    [InlineData(3201, SqlServerBackupReasons.StagingWriteFailed)]
    [InlineData(3202, SqlServerBackupReasons.StagingWriteFailed)]
    [InlineData(262, SqlServerBackupReasons.PermissionDenied)]
    [InlineData(229, SqlServerBackupReasons.PermissionDenied)]
    [InlineData(911, SqlServerBackupReasons.DatabaseNotFound)]
    [InlineData(3043, SqlServerBackupReasons.DatabaseCorruptionDetected)]
    [InlineData(824, SqlServerBackupReasons.DatabaseCorruptionDetected)]
    [InlineData(10054, SqlServerBackupReasons.ConnectionLost)]
    [InlineData(99999, SqlServerBackupReasons.BackupFailed)]
    public void BackupErrorNumbersMapToStableReasons(int number, string reason) =>
        Assert.Equal(reason, SqlServerBackupFailureClassifier.Classify(SqlServerStatement.Backup, [number]).Reason);

    [Theory]
    [InlineData(-2, SqlServerBackupReasons.VerifyTimeout)]
    [InlineData(3241, SqlServerBackupReasons.BackupMediaInvalid)]
    [InlineData(3242, SqlServerBackupReasons.BackupMediaInvalid)]
    [InlineData(3203, SqlServerBackupReasons.BackupFileUnreadable)]
    [InlineData(3201, SqlServerBackupReasons.BackupFileUnreadable)]
    [InlineData(3187, SqlServerBackupReasons.BackupChecksumMismatch)]
    [InlineData(3169, SqlServerBackupReasons.BackupVersionIncompatible)]
    [InlineData(262, SqlServerBackupReasons.PermissionDenied)]
    [InlineData(1, SqlServerBackupReasons.VerifyFailed)]
    public void VerifyErrorNumbersMapToStableReasons(int number, string reason) =>
        Assert.Equal(reason, SqlServerBackupFailureClassifier.Classify(SqlServerStatement.Verify, [number]).Reason);

    [Fact]
    public void TheCauseDecidesNotTheGenericTerminatingAbnormallyThatFollowsIt()
    {
        // Observed shape of a real failure: the cause (3203 or 3201) first, then 3013 "terminating abnormally".
        Assert.Equal(SqlServerBackupReasons.BackupFileUnreadable, SqlServerBackupFailureClassifier.Classify(SqlServerStatement.Verify, [3203, 3013]).Reason);
        Assert.Equal(SqlServerBackupReasons.StagingWriteFailed, SqlServerBackupFailureClassifier.Classify(SqlServerStatement.Backup, [3201, 3013]).Reason);
        Assert.Equal(SqlServerBackupReasons.BackupFailed, SqlServerBackupFailureClassifier.Classify(SqlServerStatement.Backup, [3013]).Reason);
    }

    [Fact]
    public void NoErrorNumbersIsTheGenericFailure() =>
        Assert.Equal(SqlServerBackupReasons.VerifyFailed, SqlServerBackupFailureClassifier.Classify(SqlServerStatement.Verify, []).Reason);

    [Theory]
    [InlineData("Backup")]
    [InlineData("Verify")]
    public void MessagesAreFixedSentencesWithoutPathsOrEngineText(string statementName)
    {
        var statement = Enum.Parse<SqlServerStatement>(statementName);
        foreach (var number in new[] { -2, 3201, 3202, 3203, 3241, 3187, 3169, 262, 911, 3043, 10054, 12345 })
        {
            var (reason, message) = SqlServerBackupFailureClassifier.Classify(statement, [number]);

            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.DoesNotContain("/", message, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", message, StringComparison.Ordinal);
            Assert.EndsWith(".", message, StringComparison.Ordinal);
        }
    }
}
