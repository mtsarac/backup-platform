using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Capabilities;
using BackupPlatform.BackupProviders.MSSQL.Discovery;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerCapabilityTests
{
    private static readonly DatabaseCapabilityId[] CoreIds =
    [
        SqlServerCapabilityIds.FullBackup,
        SqlServerCapabilityIds.DifferentialBackup,
        SqlServerCapabilityIds.LogBackup,
        SqlServerCapabilityIds.BackupChecksum,
        SqlServerCapabilityIds.CopyOnly,
        SqlServerCapabilityIds.RestoreVerifyOnly
    ];

    [Fact]
    public void EveryDeclaredCapabilityIsEvaluatedExactlyOnce()
    {
        var set = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance());

        Assert.Equal(9, set.Count);
        foreach (var id in CoreIds.Concat([
            SqlServerCapabilityIds.BackupCompression,
            SqlServerCapabilityIds.BackupEncryption,
            SqlServerCapabilityIds.TransparentDataEncryption]))
        {
            Assert.True(set.TryGet(id, out _), id.Value);
        }
    }

    [Fact]
    public void EnterpriseClassServerSupportsEverythingEvaluated()
    {
        foreach (var edition in new[] { ("Enterprise Edition (64-bit)", 3), ("Developer Edition (64-bit)", 3), ("Enterprise Evaluation Edition", 3) })
        {
            var set = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("16.0.4165.4", edition.Item1, edition.Item2));
            Assert.All(set, capability => Assert.Equal(DatabaseCapabilitySupport.Supported, capability.Support));
        }
    }

    [Fact]
    public void StandardEditionRulesFollowVersionGates()
    {
        var modern = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("16.0.1000.6", "Standard Edition (64-bit)", 2));
        Assert.Equal(DatabaseCapabilitySupport.Supported, modern.SupportOf(SqlServerCapabilityIds.BackupCompression));
        Assert.Equal(DatabaseCapabilitySupport.Supported, modern.SupportOf(SqlServerCapabilityIds.TransparentDataEncryption));
        Assert.Equal(DatabaseCapabilitySupport.Unknown, modern.SupportOf(SqlServerCapabilityIds.BackupEncryption));

        var sql2017 = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("14.0.3000.1", "Standard Edition (64-bit)", 2));
        AssertCapability(sql2017, SqlServerCapabilityIds.TransparentDataEncryption, DatabaseCapabilitySupport.Unsupported, SqlServerCapabilityReasons.VersionTooOld);

        var sql2008 = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("10.0.6000.1", "Standard Edition (64-bit)", 2));
        AssertCapability(sql2008, SqlServerCapabilityIds.BackupCompression, DatabaseCapabilitySupport.Unsupported, SqlServerCapabilityReasons.VersionTooOld);
        Assert.Equal(DatabaseCapabilitySupport.Supported, sql2008.SupportOf(SqlServerCapabilityIds.FullBackup));

        var sql2008R2 = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("10.50.6000.1", "Standard Edition (64-bit)", 2));
        Assert.Equal(DatabaseCapabilitySupport.Supported, sql2008R2.SupportOf(SqlServerCapabilityIds.BackupCompression));
    }

    [Fact]
    public void WebAndBusinessIntelligenceShareAnEngineEditionWithStandardAndAreNotGuessed()
    {
        foreach (var edition in new[] { "Web Edition (64-bit)", "Business Intelligence Edition (64-bit)", "A Brand New Edition" })
        {
            var set = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("16.0.1000.6", edition, 2));
            AssertCapability(set, SqlServerCapabilityIds.BackupCompression, DatabaseCapabilitySupport.Unknown, SqlServerCapabilityReasons.EditionUnverified);
            AssertCapability(set, SqlServerCapabilityIds.TransparentDataEncryption, DatabaseCapabilitySupport.Unknown, SqlServerCapabilityReasons.EditionUnverified);
            Assert.Equal(DatabaseCapabilitySupport.Supported, set.SupportOf(SqlServerCapabilityIds.FullBackup));
        }
    }

    [Fact]
    public void ExpressCannotCompressAndEverythingElseUnverifiedIsUnknown()
    {
        var set = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("16.0.1000.6", "Express Edition (64-bit)", 4));

        AssertCapability(set, SqlServerCapabilityIds.BackupCompression, DatabaseCapabilitySupport.Unsupported, SqlServerCapabilityReasons.EditionNotSupported);
        Assert.Equal(DatabaseCapabilitySupport.Unknown, set.SupportOf(SqlServerCapabilityIds.BackupEncryption));
        Assert.Equal(DatabaseCapabilitySupport.Unknown, set.SupportOf(SqlServerCapabilityIds.TransparentDataEncryption));
        foreach (var id in CoreIds)
        {
            Assert.Equal(DatabaseCapabilitySupport.Supported, set.SupportOf(id));
        }
    }

    [Fact]
    public void FeaturesBelowTheirFirstReleaseAreUnsupported()
    {
        var sql2008 = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("10.0.6000.1"));
        AssertCapability(sql2008, SqlServerCapabilityIds.BackupEncryption, DatabaseCapabilitySupport.Unsupported, SqlServerCapabilityReasons.VersionTooOld);

        var sql2000 = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("8.0.2039"));
        Assert.All(sql2000, capability => Assert.Equal(DatabaseCapabilitySupport.Unsupported, capability.Support));
    }

    [Fact]
    public void FutureVersionsKeepWorkingUnderNumericGates()
    {
        var set = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("99.0.1.1", "Standard Edition (64-bit)", 2));

        Assert.Equal(DatabaseCapabilitySupport.Supported, set.SupportOf(SqlServerCapabilityIds.FullBackup));
        Assert.Equal(DatabaseCapabilitySupport.Supported, set.SupportOf(SqlServerCapabilityIds.TransparentDataEncryption));
    }

    [Fact]
    public void UnparseableVersionMakesEveryCapabilityUnknownNotSupported()
    {
        var set = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("not-a-version"));

        Assert.All(set, capability =>
        {
            Assert.Equal(DatabaseCapabilitySupport.Unknown, capability.Support);
            Assert.Equal(SqlServerCapabilityReasons.VersionUnknown, capability.ReasonCode);
        });
    }

    [Fact]
    public void AzureSqlDatabaseDoesNotAcceptUserBackups()
    {
        var set = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("12.0.2000.8", "SQL Azure", 5));

        foreach (var id in CoreIds.Concat([SqlServerCapabilityIds.BackupCompression, SqlServerCapabilityIds.BackupEncryption]))
        {
            AssertCapability(set, id, DatabaseCapabilitySupport.Unsupported, SqlServerCapabilityReasons.ManagedService);
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(77)]
    public void EngineEditionsTheRulesDoNotCoverStayUnknown(int engineEdition)
    {
        var set = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("16.0.1000.6", "Whatever", engineEdition));

        Assert.All(set, capability =>
        {
            Assert.Equal(DatabaseCapabilitySupport.Unknown, capability.Support);
            Assert.Equal(SqlServerCapabilityReasons.EngineEditionNotEvaluated, capability.ReasonCode);
        });
    }

    [Fact]
    public void TempdbCannotBeBackedUpButStillReportsNonBackupCapabilities()
    {
        var instance = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance());
        var tempdb = SqlServerCapabilityEvaluator.EvaluateDatabase(instance, MsSqlTestData.Database("tempdb", id: 2, recoveryModel: SqlServerRecoveryModel.Simple));

        foreach (var capability in tempdb.Where(c => c.Id.Value.StartsWith("mssql.backup.", StringComparison.Ordinal)))
        {
            AssertCapability(tempdb, capability.Id, DatabaseCapabilitySupport.Unsupported, SqlServerCapabilityReasons.SystemDatabaseTempdb);
        }

        Assert.Equal(DatabaseCapabilitySupport.Supported, tempdb.SupportOf(SqlServerCapabilityIds.RestoreVerifyOnly));
        Assert.Equal(DatabaseCapabilitySupport.Supported, tempdb.SupportOf(SqlServerCapabilityIds.TransparentDataEncryption));
    }

    [Fact]
    public void MasterSupportsFullBackupOnly()
    {
        var instance = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance());
        var master = SqlServerCapabilityEvaluator.EvaluateDatabase(instance, MsSqlTestData.Database("master", id: 1, recoveryModel: SqlServerRecoveryModel.Simple));

        Assert.Equal(DatabaseCapabilitySupport.Supported, master.SupportOf(SqlServerCapabilityIds.FullBackup));
        AssertCapability(master, SqlServerCapabilityIds.DifferentialBackup, DatabaseCapabilitySupport.Unsupported, SqlServerCapabilityReasons.SystemDatabaseMasterFullOnly);
        AssertCapability(master, SqlServerCapabilityIds.LogBackup, DatabaseCapabilitySupport.Unsupported, SqlServerCapabilityReasons.SystemDatabaseMasterFullOnly);
    }

    [Fact]
    public void DatabaseSnapshotsCannotBeBackedUp()
    {
        var instance = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance());
        var snapshot = SqlServerCapabilityEvaluator.EvaluateDatabase(instance, MsSqlTestData.Database(snapshot: true));

        AssertCapability(snapshot, SqlServerCapabilityIds.FullBackup, DatabaseCapabilitySupport.Unsupported, SqlServerCapabilityReasons.DatabaseSnapshot);
    }

    [Theory]
    [InlineData(SqlServerRecoveryModel.Full, DatabaseCapabilitySupport.Supported)]
    [InlineData(SqlServerRecoveryModel.BulkLogged, DatabaseCapabilitySupport.Supported)]
    [InlineData(SqlServerRecoveryModel.Simple, DatabaseCapabilitySupport.Unsupported)]
    [InlineData(SqlServerRecoveryModel.Unknown, DatabaseCapabilitySupport.Unknown)]
    public void LogBackupFollowsTheRecoveryModelOfTheDatabase(SqlServerRecoveryModel model, DatabaseCapabilitySupport expected)
    {
        var instance = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance());
        var database = SqlServerCapabilityEvaluator.EvaluateDatabase(instance, MsSqlTestData.Database(recoveryModel: model));

        Assert.Equal(expected, database.SupportOf(SqlServerCapabilityIds.LogBackup));
        Assert.Equal(DatabaseCapabilitySupport.Supported, database.SupportOf(SqlServerCapabilityIds.FullBackup));
        Assert.Equal(DatabaseCapabilitySupport.Supported, database.SupportOf(SqlServerCapabilityIds.DifferentialBackup));
    }

    [Fact]
    public void ADatabaseRestrictionNeverUpgradesAnInstanceLevelResult()
    {
        var oldServer = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("8.0.2039"));
        var tempdb = SqlServerCapabilityEvaluator.EvaluateDatabase(oldServer, MsSqlTestData.Database("tempdb", id: 2));
        AssertCapability(tempdb, SqlServerCapabilityIds.FullBackup, DatabaseCapabilitySupport.Unsupported, SqlServerCapabilityReasons.VersionTooOld);

        var unknownServer = SqlServerCapabilityEvaluator.EvaluateInstance(MsSqlTestData.Instance("16.0.1000.6", "Managed", 8));
        var logFull = SqlServerCapabilityEvaluator.EvaluateDatabase(unknownServer, MsSqlTestData.Database(recoveryModel: SqlServerRecoveryModel.Full));
        Assert.Equal(DatabaseCapabilitySupport.Unknown, logFull.SupportOf(SqlServerCapabilityIds.LogBackup));

        var logSimple = SqlServerCapabilityEvaluator.EvaluateDatabase(unknownServer, MsSqlTestData.Database(recoveryModel: SqlServerRecoveryModel.Simple));
        AssertCapability(logSimple, SqlServerCapabilityIds.LogBackup, DatabaseCapabilitySupport.Unsupported, SqlServerCapabilityReasons.RecoveryModelSimple);
    }

    private static void AssertCapability(DatabaseCapabilitySet set, DatabaseCapabilityId id, DatabaseCapabilitySupport support, string reason)
    {
        Assert.True(set.TryGet(id, out var capability), id.Value);
        Assert.Equal(support, capability.Support);
        Assert.Equal(reason, capability.ReasonCode);
    }
}
