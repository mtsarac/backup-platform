using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Capabilities;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerStagingPreCheckTests
{
    private static readonly StagingAgentAccess Good = new(true, true, true, 10_000_000_000);

    private static SqlServerStagingFacts Facts(
        SqlServerStagingProbeOutcome? probe = SqlServerStagingProbeOutcome.CreatedAndVisible,
        StagingAgentAccess? access = null,
        SqlServerBackupSizeEstimate? estimate = null,
        bool compression = false) =>
        new(access ?? Good, probe, estimate ?? new SqlServerBackupSizeEstimate(1_000_000, false), compression);

    private static BackupPreCheckItem Item(IReadOnlyList<BackupPreCheckItem> items, string id) => Assert.Single(items, item => item.CheckId == id);

    [Fact]
    public void ItemsAreReportedInTheOrderTheDownstreamListUses()
    {
        var items = SqlServerStagingPreCheck.Evaluate(Facts());

        Assert.Equal(SqlServerStagingPreCheck.CheckIds, items.Select(item => item.CheckId));
        Assert.All(items, item => Assert.Equal(BackupPreCheckStatus.Passed, item.Status));
    }

    [Theory]
    [InlineData(false, false, false, SqlServerPreCheckReasons.StagingAgentDirectoryMissing)]
    [InlineData(true, false, true, SqlServerPreCheckReasons.StagingAgentNotListable)]
    [InlineData(true, true, false, SqlServerPreCheckReasons.StagingAgentNotWritable)]
    public void AnAgentThatCannotUseItsSideFailsAndSkipsTheProbeBasedChecks(bool exists, bool canList, bool canWrite, string reason)
    {
        var items = SqlServerStagingPreCheck.Evaluate(Facts(probe: null, access: new StagingAgentAccess(exists, canList, canWrite, null)));

        var agent = Item(items, BackupPreCheckIds.StagingAgentAccess);
        Assert.Equal(BackupPreCheckStatus.Failed, agent.Status);
        Assert.Equal(reason, agent.ReasonCode);
        Assert.All(items.Where(item => item != agent), item => Assert.Equal(BackupPreCheckStatus.Skipped, item.Status));
    }

    [Fact]
    public void AgentAccessMessagesNeverContainAPath()
    {
        var item = SqlServerStagingPreCheck.CheckAgentAccess(Good);

        Assert.DoesNotContain("/", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACreatedAndVisibleProbeProvesBothWriteAccessAndMapping()
    {
        var items = SqlServerStagingPreCheck.Evaluate(Facts(SqlServerStagingProbeOutcome.CreatedAndVisible));

        Assert.Equal(SqlServerPreCheckReasons.StagingServerWritable, Item(items, BackupPreCheckIds.StagingServerWritable).ReasonCode);
        Assert.Equal(SqlServerPreCheckReasons.StagingMappingVerified, Item(items, BackupPreCheckIds.StagingMapping).ReasonCode);
    }

    [Fact]
    public void AProbeTheAgentCouldNotRemoveWarnsAboutTheMappingItemButStillProvesWriteAccess()
    {
        var items = SqlServerStagingPreCheck.Evaluate(Facts(SqlServerStagingProbeOutcome.CreatedAndVisibleLeftover));

        Assert.Equal(BackupPreCheckStatus.Passed, Item(items, BackupPreCheckIds.StagingServerWritable).Status);
        var mapping = Item(items, BackupPreCheckIds.StagingMapping);
        Assert.Equal(BackupPreCheckStatus.Warning, mapping.Status);
        Assert.Equal(SqlServerPreCheckReasons.ProbeLeftover, mapping.ReasonCode);
    }

    [Fact]
    public void ADirectoryTheAgentCannotSeeFailsTheMappingButNotTheWriteAccess()
    {
        var items = SqlServerStagingPreCheck.Evaluate(Facts(SqlServerStagingProbeOutcome.CreatedNotVisible));

        Assert.Equal(BackupPreCheckStatus.Passed, Item(items, BackupPreCheckIds.StagingServerWritable).Status);
        var mapping = Item(items, BackupPreCheckIds.StagingMapping);
        Assert.Equal(BackupPreCheckStatus.Failed, mapping.Status);
        Assert.Equal(SqlServerPreCheckReasons.StagingMappingMismatch, mapping.ReasonCode);
    }

    [Fact]
    public void ALoginThatCannotProbeGetsWarningsNeverAPass()
    {
        var items = SqlServerStagingPreCheck.Evaluate(Facts(SqlServerStagingProbeOutcome.PermissionDenied));

        Assert.Equal(BackupPreCheckStatus.Warning, Item(items, BackupPreCheckIds.StagingServerWritable).Status);
        Assert.Equal(BackupPreCheckStatus.Warning, Item(items, BackupPreCheckIds.StagingMapping).Status);
        Assert.All(
            [Item(items, BackupPreCheckIds.StagingServerWritable), Item(items, BackupPreCheckIds.StagingMapping)],
            item => Assert.Equal(SqlServerPreCheckReasons.StagingServerWriteUnverifiable, item.ReasonCode));
        Assert.True(new BackupPreCheckReport(items).CanProceed);
    }

    [Fact]
    public void AnOperatingSystemRefusalFailsWritabilityAndSkipsTheMapping()
    {
        var items = SqlServerStagingPreCheck.Evaluate(Facts(SqlServerStagingProbeOutcome.ServerOsFailure));

        var writable = Item(items, BackupPreCheckIds.StagingServerWritable);
        Assert.Equal(BackupPreCheckStatus.Failed, writable.Status);
        Assert.Equal(SqlServerPreCheckReasons.StagingServerCannotWrite, writable.ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(items, BackupPreCheckIds.StagingMapping).Status);
    }

    [Fact]
    public void AMissingServerDirectoryFailsWritabilityNamesTheCauseAndSkipsTheMapping()
    {
        var items = SqlServerStagingPreCheck.Evaluate(Facts(SqlServerStagingProbeOutcome.ServerDirectoryMissing));

        var writable = Item(items, BackupPreCheckIds.StagingServerWritable);
        Assert.Equal(BackupPreCheckStatus.Failed, writable.Status);
        Assert.Equal(SqlServerPreCheckReasons.StagingServerDirectoryMissing, writable.ReasonCode);
        Assert.Contains("never creates", writable.Message, StringComparison.Ordinal);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(items, BackupPreCheckIds.StagingMapping).Status);
    }

    [Fact]
    public void AnUnclassifiedProbeFailureProvesNothingAndOnlyWarns()
    {
        var items = SqlServerStagingPreCheck.Evaluate(Facts(SqlServerStagingProbeOutcome.Failed));

        Assert.Equal(SqlServerPreCheckReasons.StagingProbeFailed, Item(items, BackupPreCheckIds.StagingServerWritable).ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Warning, Item(items, BackupPreCheckIds.StagingMapping).Status);
    }

    [Theory]
    [InlineData(1_000, 2_000, false, false, BackupPreCheckStatus.Passed, SqlServerPreCheckReasons.DiskSpaceSufficient)]
    [InlineData(2_000, 2_000, false, false, BackupPreCheckStatus.Passed, SqlServerPreCheckReasons.DiskSpaceSufficient)]
    [InlineData(2_001, 2_000, false, false, BackupPreCheckStatus.Failed, SqlServerPreCheckReasons.DiskSpaceInsufficient)]
    [InlineData(2_001, 2_000, true, false, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DiskSpaceEstimateUncertain)]
    [InlineData(2_001, 2_000, false, true, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DiskSpaceEstimateUncertain)]
    [InlineData(1_000, 2_000, true, true, BackupPreCheckStatus.Passed, SqlServerPreCheckReasons.DiskSpaceSufficient)]
    public void DiskSpaceFailsOnlyWhenTheEstimateIsExactAndNothingCouldShrinkIt(
        long estimate, long free, bool upperBound, bool compression, BackupPreCheckStatus status, string reason)
    {
        var item = SqlServerStagingPreCheck.CheckDiskSpace(
            new StagingAgentAccess(true, true, true, free), new SqlServerBackupSizeEstimate(estimate, upperBound), compression);

        Assert.Equal(status, item.Status);
        Assert.Equal(reason, item.ReasonCode);
    }

    [Fact]
    public void UnreadableFreeSpaceOrMissingEstimateIsAWarningNotAPass()
    {
        var noFree = SqlServerStagingPreCheck.CheckDiskSpace(new StagingAgentAccess(true, true, true, null), new SqlServerBackupSizeEstimate(1, false), false);
        var noEstimate = SqlServerStagingPreCheck.CheckDiskSpace(Good, null, false);

        Assert.Equal(SqlServerPreCheckReasons.DiskSpaceUnknown, noFree.ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Warning, noFree.Status);
        Assert.Equal(SqlServerPreCheckReasons.DiskSpaceUnknown, noEstimate.ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Warning, noEstimate.Status);
    }

    [Fact]
    public void TheFullPreCheckSkipsTheStagingItemsWhenThePathFormatFailed()
    {
        var facts = new SqlServerPreCheckFacts(
            null, null, MsSqlTestData.Instance(platform: SqlServerHostPlatform.Linux), MsSqlTestData.Database(),
            SqlServerBackupPermission.Granted, Facts());
        var request = new FullBackupPreCheckRequest("Sales", staging: StagingArea.Mapped(new DatabaseServerPath(@"C:\Backups"), new AgentPath(MsSqlTestData.AgentAbsolute("/tmp"))));

        var report = new BackupPreCheckReport(SqlServerFullBackupPreCheck.Evaluate(facts, request));

        Assert.Equal(SqlServerPreCheckReasons.StagingPathWrongStyle, Item(report.Items, BackupPreCheckIds.StagingPathFormat).ReasonCode);
        Assert.All(SqlServerStagingPreCheck.CheckIds, id => Assert.Equal(BackupPreCheckStatus.Skipped, Item(report.Items, id).Status));
    }

    [Fact]
    public void TheFullPreCheckAddsTheStagingItemsAfterThePathFormat()
    {
        var facts = new SqlServerPreCheckFacts(
            null, null, MsSqlTestData.Instance(platform: SqlServerHostPlatform.Linux), MsSqlTestData.Database(),
            SqlServerBackupPermission.Granted, Facts());
        var request = new FullBackupPreCheckRequest("Sales", [SqlServerCapabilityIds.BackupChecksum], StagingArea.Mapped(new DatabaseServerPath("/srv/staging"), new AgentPath(MsSqlTestData.AgentAbsolute("/tmp"))));

        var report = new BackupPreCheckReport(SqlServerFullBackupPreCheck.Evaluate(facts, request));

        var ids = report.Items.Select(item => item.CheckId).ToList();
        var format = ids.IndexOf(BackupPreCheckIds.StagingPathFormat);
        Assert.Equal(SqlServerStagingPreCheck.CheckIds, ids.Skip(format + 1).Take(4));
        Assert.Equal(BackupPreCheckIds.RequestedCapability(SqlServerCapabilityIds.BackupChecksum), ids[^1]);
    }
}
