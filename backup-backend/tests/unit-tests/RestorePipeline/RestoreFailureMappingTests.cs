using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.UnitTests.RestorePipeline;

public sealed class RestoreFailureMappingTests
{
    [Theory]
    [InlineData(RestoreReasonCodes.TargetDatabaseExists, "TargetDatabaseExists")]
    [InlineData(RestoreReasonCodes.TargetDatabaseMissing, "TargetDatabaseNotFound")]
    [InlineData(RestoreReasonCodes.TargetInUse, "TargetInUse")]
    [InlineData(RestoreReasonCodes.TargetProtected, "TargetProtected")]
    [InlineData(RestoreReasonCodes.BackupTypeUnsupported, "BackupTypeUnsupported")]
    [InlineData(RestoreReasonCodes.TargetVersionTooOld, "IncompatibleVersion")]
    [InlineData(RestoreReasonCodes.InsufficientDiskSpace, "InsufficientDiskSpace")]
    [InlineData(RestoreReasonCodes.FileMappingFailed, "FileMappingFailed")]
    public void EveryEngineIndependentReasonHasItsPlatformCode(string reason, string expected)
    {
        Assert.Equal(expected, RestoreFailureMapping.FromReason(reason));
    }

    [Theory]
    [InlineData("target-database-exists ")]
    [InlineData("Target-Database-Exists")]
    [InlineData("")]
    [InlineData("something-else")]
    public void AReasonThatIsNotExactlyOneOfTheKnownCodesIsNotMappedByReason(string reason) =>
        Assert.Null(RestoreFailureMapping.FromReason(reason));

    [Theory]
    [InlineData(BackupFailureCause.DatabaseUnavailable, "DatabaseUnavailable")]
    [InlineData(BackupFailureCause.Timeout, "OperationTimedOut")]
    [InlineData(BackupFailureCause.DatabaseNotFound, "TargetDatabaseNotFound")]
    [InlineData(BackupFailureCause.InvalidConfiguration, "InvalidConfiguration")]
    [InlineData(BackupFailureCause.Unsupported, "UnsupportedCapability")]
    [InlineData(BackupFailureCause.AuthenticationFailed, "AuthenticationFailed")]
    [InlineData(BackupFailureCause.PermissionDenied, "PermissionDenied")]
    [InlineData(BackupFailureCause.StagingInvalid, "StagingInvalid")]
    [InlineData(BackupFailureCause.StorageUnavailable, "StorageUnavailable")]
    [InlineData(BackupFailureCause.Unclassified, null)]
    public void EveryClassifiedCauseHasItsPlatformCodeAndAnUnclassifiedOneSaysNothing(BackupFailureCause cause, string? expected)
    {
        Assert.Equal(expected, RestoreFailureMapping.FromCause(cause));
    }

    [Theory]
    [InlineData(RestoreFailureKind.PreCheckFailed, "PreCheckFailed")]
    [InlineData(RestoreFailureKind.BackupInvalid, "BackupInvalid")]
    [InlineData(RestoreFailureKind.SqlRestoreFailed, "SqlRestoreFailed")]
    [InlineData(RestoreFailureKind.Cancelled, "InternalError")]
    [InlineData(null, "InternalError")]
    public void TheKindIsTheLastResortBeforeAnInternalError(RestoreFailureKind? kind, string expected) =>
        Assert.Equal(expected, RestoreFailureMapping.Map("unknown-reason", BackupFailureCause.Unclassified, kind));

    [Fact]
    public void ReasonBeatsCauseBeatsKind()
    {
        Assert.Equal("TargetInUse", RestoreFailureMapping.Map(RestoreReasonCodes.TargetInUse, BackupFailureCause.PermissionDenied, RestoreFailureKind.SqlRestoreFailed));
        Assert.Equal("PermissionDenied", RestoreFailureMapping.Map("x", BackupFailureCause.PermissionDenied, RestoreFailureKind.SqlRestoreFailed));
        Assert.Equal("SqlRestoreFailed", RestoreFailureMapping.Map("x", BackupFailureCause.Unclassified, RestoreFailureKind.SqlRestoreFailed));
    }

    [Theory]
    [InlineData(StorageFailureKind.NotFound, "ReplicaUnavailable")]
    [InlineData(StorageFailureKind.InsufficientSpace, "InsufficientDiskSpace")]
    [InlineData(StorageFailureKind.IntegrityMismatch, "ChecksumMismatch")]
    [InlineData(StorageFailureKind.NotSupported, "UnsupportedCapability")]
    [InlineData(StorageFailureKind.InvalidConfiguration, "InvalidConfiguration")]
    [InlineData(StorageFailureKind.AccessDenied, "StorageUnavailable")]
    [InlineData(StorageFailureKind.Io, "StorageUnavailable")]
    [InlineData(StorageFailureKind.AlreadyExists, "StorageUnavailable")]
    [InlineData(StorageFailureKind.Unexpected, "StorageUnavailable")]
    public void EveryStorageFailureKindHasAReportablePlatformCode(StorageFailureKind kind, string expected)
    {
        Assert.Equal(expected, RestoreFailureMapping.FromStorage(kind));
    }

}
