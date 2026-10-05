using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerFailureCausesTests
{
    [Theory]
    [InlineData(SqlServerPreCheckReasons.ServerUnreachable, BackupFailureCause.DatabaseUnavailable)]
    [InlineData(SqlServerPreCheckReasons.ConnectionFailed, BackupFailureCause.DatabaseUnavailable)]
    [InlineData(SqlServerPreCheckReasons.MetadataUnavailable, BackupFailureCause.DatabaseUnavailable)]
    [InlineData(SqlServerPreCheckReasons.ConnectionTimeout, BackupFailureCause.Timeout)]
    [InlineData(SqlServerPreCheckReasons.ConnectionSettingsInvalid, BackupFailureCause.InvalidConfiguration)]
    [InlineData(SqlServerPreCheckReasons.AuthenticationFailed, BackupFailureCause.AuthenticationFailed)]
    [InlineData(SqlServerPreCheckReasons.DatabaseNotFound, BackupFailureCause.DatabaseNotFound)]
    [InlineData(SqlServerPreCheckReasons.PermissionDenied, BackupFailureCause.PermissionDenied)]
    [InlineData(SqlServerPreCheckReasons.VerifyPermissionDenied, BackupFailureCause.PermissionDenied)]
    [InlineData(SqlServerPreCheckReasons.CapabilityUnsupported, BackupFailureCause.Unsupported)]
    [InlineData(SqlServerPreCheckReasons.CapabilityNotRecognized, BackupFailureCause.Unsupported)]
    [InlineData(SqlServerPreCheckReasons.StagingPathInvalid, BackupFailureCause.StagingInvalid)]
    [InlineData(SqlServerPreCheckReasons.StagingPathWrongStyle, BackupFailureCause.StagingInvalid)]
    [InlineData(SqlServerPreCheckReasons.StagingAgentDirectoryMissing, BackupFailureCause.StagingInvalid)]
    [InlineData(SqlServerPreCheckReasons.StagingAgentNotListable, BackupFailureCause.StagingInvalid)]
    [InlineData(SqlServerPreCheckReasons.StagingAgentNotWritable, BackupFailureCause.StagingInvalid)]
    [InlineData(SqlServerPreCheckReasons.StagingServerCannotWrite, BackupFailureCause.StagingInvalid)]
    [InlineData(SqlServerPreCheckReasons.StagingServerDirectoryMissing, BackupFailureCause.StagingInvalid)]
    [InlineData(SqlServerPreCheckReasons.StagingMappingMismatch, BackupFailureCause.StagingInvalid)]
    [InlineData(SqlServerPreCheckReasons.DiskSpaceInsufficient, BackupFailureCause.StagingInvalid)]
    [InlineData(SqlServerPreCheckReasons.DatabaseOffline, BackupFailureCause.Unclassified)]
    [InlineData(SqlServerPreCheckReasons.DatabaseRestoring, BackupFailureCause.Unclassified)]
    [InlineData(SqlServerPreCheckReasons.DatabaseSuspect, BackupFailureCause.Unclassified)]
    [InlineData(SqlServerPreCheckReasons.ExtensionFailed, BackupFailureCause.Unclassified)]
    [InlineData("a-reason-nobody-knows", BackupFailureCause.Unclassified)]
    public void PreCheckReasonsMapToCauses(string reason, BackupFailureCause cause) =>
        Assert.Equal(cause, SqlServerFailureCauses.ForPreCheckReason(reason));

    [Theory]
    [InlineData(SqlServerBackupReasons.ServerUnavailable, BackupFailureCause.DatabaseUnavailable)]
    [InlineData(SqlServerBackupReasons.ConnectionLost, BackupFailureCause.DatabaseUnavailable)]
    [InlineData(SqlServerBackupReasons.BackupTimeout, BackupFailureCause.Timeout)]
    [InlineData(SqlServerBackupReasons.VerifyTimeout, BackupFailureCause.Timeout)]
    [InlineData(SqlServerBackupReasons.AuthenticationFailed, BackupFailureCause.AuthenticationFailed)]
    [InlineData(SqlServerBackupReasons.PermissionDenied, BackupFailureCause.PermissionDenied)]
    [InlineData(SqlServerBackupReasons.VerifyPermissionDenied, BackupFailureCause.PermissionDenied)]
    [InlineData(SqlServerBackupReasons.DatabaseNotFound, BackupFailureCause.DatabaseNotFound)]
    [InlineData(SqlServerBackupReasons.BackupNotSupported, BackupFailureCause.Unsupported)]
    [InlineData(SqlServerBackupReasons.RequiredOptionUnsupported, BackupFailureCause.Unsupported)]
    [InlineData(SqlServerBackupReasons.StagingAgentDirectoryMissing, BackupFailureCause.StagingInvalid)]
    [InlineData(SqlServerBackupReasons.StagingMappingMismatch, BackupFailureCause.StagingInvalid)]
    [InlineData(SqlServerBackupReasons.StagingWriteFailed, BackupFailureCause.StagingInvalid)]
    [InlineData(SqlServerBackupReasons.StagedFileExists, BackupFailureCause.StagingInvalid)]
    [InlineData(SqlServerBackupReasons.DatabaseNotOnline, BackupFailureCause.Unclassified)]
    [InlineData(SqlServerBackupReasons.DatabaseCorruptionDetected, BackupFailureCause.Unclassified)]
    [InlineData(SqlServerBackupReasons.BackupFailed, BackupFailureCause.Unclassified)]
    [InlineData(SqlServerBackupReasons.VerifyFailed, BackupFailureCause.Unclassified)]
    [InlineData(SqlServerBackupReasons.UnexpectedError, BackupFailureCause.Unclassified)]
    public void BackupReasonsMapToCauses(string reason, BackupFailureCause cause) =>
        Assert.Equal(cause, SqlServerFailureCauses.ForBackupReason(reason));

    [Theory]
    [InlineData(DatabaseFailureKind.Unreachable, BackupFailureCause.DatabaseUnavailable)]
    [InlineData(DatabaseFailureKind.Timeout, BackupFailureCause.Timeout)]
    [InlineData(DatabaseFailureKind.InvalidConfiguration, BackupFailureCause.InvalidConfiguration)]
    [InlineData(DatabaseFailureKind.Unexpected, BackupFailureCause.DatabaseUnavailable)]
    public void AFailedConnectionCheckCarriesTheCause(DatabaseFailureKind kind, BackupFailureCause cause)
    {
        var failure = new DatabaseProviderException(kind, "user safe text");
        var items = SqlServerFullBackupPreCheck.Evaluate(
            new SqlServerPreCheckFacts(failure, null, null, null, SqlServerBackupPermission.Unknown), new FullBackupPreCheckRequest("Sales"));

        Assert.Equal(cause, Assert.Single(items, item => item.Status == BackupPreCheckStatus.Failed).Cause);
        Assert.All(items.Where(item => item.Status != BackupPreCheckStatus.Failed), item => Assert.Equal(BackupFailureCause.Unclassified, item.Cause));
    }

    [Fact]
    public void ARejectedLoginCarriesTheAuthenticationCause()
    {
        var failure = new DatabaseProviderException(DatabaseFailureKind.AuthenticationFailed, "credentials rejected");
        var items = SqlServerFullBackupPreCheck.Evaluate(
            new SqlServerPreCheckFacts(failure, null, null, null, SqlServerBackupPermission.Unknown), new FullBackupPreCheckRequest("Sales"));

        Assert.Equal(BackupFailureCause.AuthenticationFailed, Assert.Single(items, item => item.Status == BackupPreCheckStatus.Failed).Cause);
    }

    [Theory]
    [InlineData(false, false, false, BackupFailureCause.StagingInvalid)]
    [InlineData(true, true, false, BackupFailureCause.StagingInvalid)]
    public void AFailedStagingCheckCarriesTheStagingCause(bool exists, bool canList, bool canWrite, BackupFailureCause cause)
    {
        var item = SqlServerStagingPreCheck.CheckAgentAccess(new StagingAgentAccess(exists, canList, canWrite, null));

        Assert.Equal(BackupPreCheckStatus.Failed, item.Status);
        Assert.Equal(cause, item.Cause);
    }

    [Fact]
    public void ExecutionFailuresDeriveTheirCauseFromTheReasonExceptForCancellation()
    {
        var state = new FullBackupExecutionState();
        state.Fail(BackupFailureKind.SqlBackupFailed, BackupExecutionStage.BackingUp, SqlServerBackupReasons.BackupTimeout, "The backup timed out.");
        Assert.Equal(BackupFailureCause.Timeout, state.Failure!.Cause);

        var cancelled = new FullBackupExecutionState();
        cancelled.Cancel(BackupExecutionStage.BackingUp);
        Assert.Equal(BackupFailureCause.Unclassified, cancelled.Failure!.Cause);
    }
}
