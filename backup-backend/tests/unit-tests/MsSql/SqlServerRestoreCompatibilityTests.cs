using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;
using BackupPlatform.BackupProviders.MSSQL.Restore;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerRestoreCompatibilityTests
{
    private static RestoreBackupHeader Header(RestoreBackupKind kind = RestoreBackupKind.Database, bool encrypted = false) =>
        new("Sales", kind, new Version(16, 0, 4165), "SRC", 160, "SQL_Latin1_General_CP1_CI_AS", null, null, 1, 1000, false, true, false, encrypted, Guid.NewGuid());

    [Fact]
    public void AFullUnencryptedBackupPassesTheTypeCheck()
    {
        var item = SqlServerRestoreCompatibility.CheckBackupType(Header());

        Assert.Equal(BackupPreCheckStatus.Passed, item.Status);
        Assert.Equal(SqlServerRestoreCheckIds.BackupType, item.CheckId);
        Assert.Equal(SqlServerRestoreReasons.BackupTypeSupported, item.ReasonCode);
    }

    [Theory]
    [InlineData(RestoreBackupKind.Differential, "differential")]
    [InlineData(RestoreBackupKind.TransactionLog, "transaction log")]
    [InlineData(RestoreBackupKind.Other, "file, partial")]
    public void OtherBackupTypesFailWithTheEngineIndependentReasonAndAnUnsupportedCause(RestoreBackupKind kind, string fragment)
    {
        var item = SqlServerRestoreCompatibility.CheckBackupType(Header(kind));

        Assert.Equal(BackupPreCheckStatus.Failed, item.Status);
        Assert.Equal(RestoreReasonCodes.BackupTypeUnsupported, item.ReasonCode);
        Assert.Equal(BackupFailureCause.Unsupported, item.Cause);
        Assert.Contains(fragment, item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEncryptedBackupFailsAsUnsupported()
    {
        var item = SqlServerRestoreCompatibility.CheckBackupType(Header(encrypted: true));

        Assert.Equal(BackupPreCheckStatus.Failed, item.Status);
        Assert.Equal(SqlServerRestoreReasons.BackupEncryptedUnsupported, item.ReasonCode);
        Assert.Equal(BackupFailureCause.Unsupported, item.Cause);
    }

    [Theory]
    [InlineData("17.0.1000", "16.0.4165", BackupPreCheckStatus.Failed, RestoreReasonCodes.TargetVersionTooOld)]
    [InlineData("16.0.4165", "16.0.4165", BackupPreCheckStatus.Passed, SqlServerRestoreReasons.VersionCompatible)]
    [InlineData("16.0.1000", "16.0.4165", BackupPreCheckStatus.Passed, SqlServerRestoreReasons.VersionCompatible)]
    [InlineData("15.0.2000", "16.0.4165", BackupPreCheckStatus.Passed, SqlServerRestoreReasons.VersionCompatible)]
    [InlineData("16.0.4200", "16.0.4165", BackupPreCheckStatus.Warning, SqlServerRestoreReasons.VersionNewerBuild)]
    [InlineData("16.1.0", "16.0.4165", BackupPreCheckStatus.Warning, SqlServerRestoreReasons.VersionNewerBuild)]
    [InlineData("9.0.5000", "16.0.4165", BackupPreCheckStatus.Warning, SqlServerRestoreReasons.VersionOld)]
    [InlineData("10.0.1600", "16.0.4165", BackupPreCheckStatus.Passed, SqlServerRestoreReasons.VersionCompatible)]
    [InlineData("8.0.760", "16.0.4165", BackupPreCheckStatus.Warning, SqlServerRestoreReasons.VersionOld)]
    public void VersionDirectionDecidesBetweenFailWarningAndPass(string source, string target, BackupPreCheckStatus status, string reason)
    {
        Assert.True(SqlServerVersion.TryParse(target, out var targetVersion));

        var item = SqlServerRestoreCompatibility.CheckVersion(Version.Parse(source), targetVersion);

        Assert.Equal(status, item.Status);
        Assert.Equal(reason, item.ReasonCode);
        Assert.Equal(SqlServerRestoreCheckIds.VersionCompatibility, item.CheckId);
    }

    [Fact]
    public void ANewerMajorVersionIsAFailureWithoutACauseTheCallerMustTranslate()
    {
        var item = SqlServerRestoreCompatibility.CheckVersion(new Version(17, 0, 1), new SqlServerVersion(15, 0, 2000));

        Assert.Equal(BackupPreCheckStatus.Failed, item.Status);
        Assert.Equal(RestoreReasonCodes.TargetVersionTooOld, item.ReasonCode);
        Assert.Equal(BackupFailureCause.Unsupported, item.Cause);
    }

    [Fact]
    public void AnUnknownVersionOnEitherSideIsAWarningNeverAPass()
    {
        Assert.Equal(SqlServerRestoreReasons.VersionUnknown, SqlServerRestoreCompatibility.CheckVersion(null, new SqlServerVersion(16, 0, 1)).ReasonCode);
        Assert.Equal(SqlServerRestoreReasons.VersionUnknown, SqlServerRestoreCompatibility.CheckVersion(new Version(16, 0, 1), null).ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Warning, SqlServerRestoreCompatibility.CheckVersion(null, null).Status);
    }

    [Theory]
    [InlineData("Enterprise Edition (64-bit)", 3, BackupPreCheckStatus.Passed)]
    [InlineData("Developer Edition (64-bit)", 3, BackupPreCheckStatus.Passed)]
    [InlineData("Standard Edition (64-bit)", 2, BackupPreCheckStatus.Warning)]
    [InlineData("Express Edition (64-bit)", 4, BackupPreCheckStatus.Warning)]
    [InlineData("SQL Azure", 5, BackupPreCheckStatus.Warning)]
    [InlineData(null, null, BackupPreCheckStatus.Warning)]
    public void OnlyAnEnterpriseClassTargetIsKnownToSupportEveryFeature(string? edition, int? engineEdition, BackupPreCheckStatus status)
    {
        var item = SqlServerRestoreCompatibility.CheckEdition(SqlServerEdition.FromServerProperties(edition, engineEdition));

        Assert.Equal(status, item.Status);
        Assert.Equal(status == BackupPreCheckStatus.Passed ? SqlServerRestoreReasons.EditionCompatible : SqlServerRestoreReasons.EditionUnknown, item.ReasonCode);
    }

    [Fact]
    public void FamilyGuidsDecideWhetherTheTargetIsRelatedToTheBackup()
    {
        var family = Guid.NewGuid();

        var related = SqlServerRestoreCompatibility.CheckRelation(family, family);
        var unrelated = SqlServerRestoreCompatibility.CheckRelation(family, Guid.NewGuid());
        var unknownTarget = SqlServerRestoreCompatibility.CheckRelation(null, family);
        var unknownBackup = SqlServerRestoreCompatibility.CheckRelation(family, null);

        Assert.Equal((BackupPreCheckStatus.Passed, SqlServerRestoreReasons.TargetRelated), (related.Status, related.ReasonCode));
        Assert.Equal((BackupPreCheckStatus.Warning, SqlServerRestoreReasons.TargetUnrelated), (unrelated.Status, unrelated.ReasonCode));
        Assert.Equal((BackupPreCheckStatus.Warning, SqlServerRestoreReasons.TargetRelationUnknown), (unknownTarget.Status, unknownTarget.ReasonCode));
        Assert.Equal((BackupPreCheckStatus.Warning, SqlServerRestoreReasons.TargetRelationUnknown), (unknownBackup.Status, unknownBackup.ReasonCode));
    }

    [Theory]
    [InlineData(SqlServerRestoreReasons.ServerUnavailable, BackupFailureCause.DatabaseUnavailable)]
    [InlineData(SqlServerRestoreReasons.ConnectionLost, BackupFailureCause.DatabaseUnavailable)]
    [InlineData(SqlServerPreCheckReasons.ServerUnreachable, BackupFailureCause.DatabaseUnavailable)]
    [InlineData(SqlServerPreCheckReasons.MetadataUnavailable, BackupFailureCause.DatabaseUnavailable)]
    [InlineData(SqlServerRestoreReasons.ConnectionTimeout, BackupFailureCause.Timeout)]
    [InlineData(SqlServerRestoreReasons.RestoreTimeout, BackupFailureCause.Timeout)]
    [InlineData(SqlServerRestoreReasons.InspectTimeout, BackupFailureCause.Timeout)]
    [InlineData(SqlServerRestoreReasons.VerifyTimeout, BackupFailureCause.Timeout)]
    [InlineData(SqlServerRestoreReasons.ConnectionSettingsInvalid, BackupFailureCause.InvalidConfiguration)]
    [InlineData(SqlServerRestoreReasons.DatabaseNameInvalid, BackupFailureCause.InvalidConfiguration)]
    [InlineData(SqlServerRestoreReasons.AuthenticationFailed, BackupFailureCause.AuthenticationFailed)]
    [InlineData(SqlServerRestoreReasons.PermissionDenied, BackupFailureCause.PermissionDenied)]
    [InlineData(SqlServerRestoreReasons.RestorePermissionDenied, BackupFailureCause.PermissionDenied)]
    [InlineData(RestoreReasonCodes.TargetDatabaseMissing, BackupFailureCause.DatabaseNotFound)]
    [InlineData(RestoreReasonCodes.BackupTypeUnsupported, BackupFailureCause.Unsupported)]
    [InlineData(RestoreReasonCodes.TargetVersionTooOld, BackupFailureCause.Unsupported)]
    [InlineData(SqlServerRestoreReasons.BackupEncryptedUnsupported, BackupFailureCause.Unsupported)]
    [InlineData(SqlServerRestoreReasons.BackupFileUnreadable, BackupFailureCause.StagingInvalid)]
    [InlineData(RestoreReasonCodes.TargetDatabaseExists, BackupFailureCause.Unclassified)]
    [InlineData(RestoreReasonCodes.TargetInUse, BackupFailureCause.Unclassified)]
    [InlineData(RestoreReasonCodes.TargetProtected, BackupFailureCause.Unclassified)]
    [InlineData(RestoreReasonCodes.InsufficientDiskSpace, BackupFailureCause.Unclassified)]
    [InlineData(RestoreReasonCodes.FileMappingFailed, BackupFailureCause.Unclassified)]
    [InlineData("something-new", BackupFailureCause.Unclassified)]
    public void RestoreReasonsMapToTheEngineIndependentCause(string reason, BackupFailureCause cause) =>
        Assert.Equal(cause, SqlServerFailureCauses.ForRestoreReason(reason));

    [Fact]
    public void EveryFailedCheckHelperItemCarriesTheCauseOfItsReason()
    {
        var item = SqlServerRestoreChecks.Failed("x", SqlServerRestoreReasons.RestorePermissionDenied, "no");

        Assert.Equal(BackupFailureCause.PermissionDenied, item.Cause);
        Assert.Equal(BackupFailureCause.Unclassified, SqlServerRestoreChecks.Warning("x", "w", "m").Cause);
        Assert.Equal(BackupFailureCause.Unclassified, SqlServerRestoreChecks.Passed("x", "p", "m").Cause);
    }
}
