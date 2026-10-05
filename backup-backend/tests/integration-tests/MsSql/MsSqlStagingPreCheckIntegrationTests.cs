using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests.MsSql;

/// <summary>The staging pre-checks against a real SQL Server: the server-side probe, what a least-privilege login can and cannot prove, and the wrong-mapping case.</summary>
[Collection(MsSqlCollection.Name)]
public sealed class MsSqlStagingPreCheckIntegrationTests(MsSqlFixture fixture)
{
    private readonly SqlServerBackupProvider _provider = MsSqlCollection.CreateProvider();

    private Task<BackupPreCheckReport> PreCheckAsync(DatabaseCredential credential, StagingArea staging, params DatabaseCapabilityId[] requested) =>
        _provider.PreCheckFullBackupAsync(
            fixture.Settings, credential, new FullBackupPreCheckRequest(fixture.Source, requested, staging), CancellationToken.None);

    private static BackupPreCheckItem Item(BackupPreCheckReport report, string checkId) =>
        Assert.Single(report.Items, item => item.CheckId == checkId);

    private static string Describe(BackupPreCheckReport report) =>
        string.Join("; ", report.Items.Select(item => $"{item.CheckId}={item.Status}:{item.ReasonCode}"));

    [Fact]
    public async Task ASysadminProbeProvesWriteAccessAndTheMappingAndLeavesNoProbeDirectoryBehind()
    {
        var before = fixture.StagingEntries("bp_probe_");

        var report = await PreCheckAsync(fixture.Admin, fixture.Staging);

        Assert.True(report.CanProceed, Describe(report));
        Assert.Equal(BackupPreCheckStatus.Passed, report.Status);
        Assert.Equal(SqlServerPreCheckReasons.StagingAgentAccessible, Item(report, BackupPreCheckIds.StagingAgentAccess).ReasonCode);
        Assert.Equal(SqlServerPreCheckReasons.StagingServerWritable, Item(report, BackupPreCheckIds.StagingServerWritable).ReasonCode);
        Assert.Equal(SqlServerPreCheckReasons.StagingMappingVerified, Item(report, BackupPreCheckIds.StagingMapping).ReasonCode);
        Assert.Equal(SqlServerPreCheckReasons.DiskSpaceSufficient, Item(report, BackupPreCheckIds.StagingDiskSpace).ReasonCode);
        Assert.Equal(before, fixture.StagingEntries("bp_probe_"));
    }

    [Fact]
    public async Task TheLeastPrivilegeLoginGetsWarningsBecauseItCannotProbeNotAFalsePass()
    {
        var report = await PreCheckAsync(fixture.Credential(fixture.OperatorWithVerify), fixture.Staging);

        Assert.True(report.CanProceed, Describe(report));
        Assert.Equal(BackupPreCheckStatus.Warning, report.Status);
        Assert.Equal(BackupPreCheckStatus.Passed, Item(report, BackupPreCheckIds.StagingAgentAccess).Status);
        foreach (var id in new[] { BackupPreCheckIds.StagingServerWritable, BackupPreCheckIds.StagingMapping })
        {
            Assert.Equal(BackupPreCheckStatus.Warning, Item(report, id).Status);
            Assert.Equal(SqlServerPreCheckReasons.StagingServerWriteUnverifiable, Item(report, id).ReasonCode);
        }

        // The operator cannot read the used-pages DMV either, so the estimate is the allocated file size (an upper bound) and still fits.
        Assert.Equal(BackupPreCheckStatus.Passed, Item(report, BackupPreCheckIds.StagingDiskSpace).Status);
    }

    [Fact]
    public async Task AnAgentDirectoryThatIsNotTheServersDirectoryFailsTheMappingAndTheProbeIsCleanedUpByTheTest()
    {
        var before = fixture.StagingEntries("bp_probe_");
        var other = Directory.CreateTempSubdirectory("bp-it-other-");
        try
        {
            var staging = StagingArea.Mapped(fixture.Staging.ServerDirectory, new AgentPath(other.FullName));

            var report = await PreCheckAsync(fixture.Admin, staging);

            Assert.False(report.CanProceed);
            Assert.Equal(BackupPreCheckStatus.Passed, Item(report, BackupPreCheckIds.StagingServerWritable).Status);
            var mapping = Item(report, BackupPreCheckIds.StagingMapping);
            Assert.Equal(BackupPreCheckStatus.Failed, mapping.Status);
            Assert.Equal(SqlServerPreCheckReasons.StagingMappingMismatch, mapping.ReasonCode);
        }
        finally
        {
            // The probe directory exists on the server side of the real mapping; it is what the message warns about.
            var agent = new AgentStagingDirectory(fixture.Staging.AgentDirectory);
            foreach (var leftover in fixture.StagingEntries("bp_probe_").Except(before))
            {
                Assert.True(agent.TryDeleteEmptyDirectory(leftover));
            }

            other.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ADirectoryTheServiceAccountCannotWriteFailsWritabilityAndSkipsTheMapping()
    {
        var unwritable = fixture.UnwritableServerDirectory;
        var staging = StagingArea.Mapped(new DatabaseServerPath(unwritable), fixture.Staging.AgentDirectory);

        var report = await PreCheckAsync(fixture.Admin, staging);

        Assert.False(report.CanProceed);
        var writable = Item(report, BackupPreCheckIds.StagingServerWritable);
        Assert.Equal(BackupPreCheckStatus.Failed, writable.Status);
        Assert.Equal(SqlServerPreCheckReasons.StagingServerCannotWrite, writable.ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(report, BackupPreCheckIds.StagingMapping).Status);
        Assert.DoesNotContain(unwritable, writable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerDirectoryThatDoesNotExistFailsWithoutTheProbeCreatingIt()
    {
        // A sub-directory of the real staging directory that was never created: xp_create_subdir would build it (and every missing
        // parent) on the server, which is what a mistyped staging path must not cause.
        var missing = fixture.Staging.ServerDirectory.Combine($"bp_it_missing_{fixture.Suffix}");
        var staging = StagingArea.Mapped(missing, fixture.Staging.AgentDirectory);

        var report = await PreCheckAsync(fixture.Admin, staging);

        Assert.False(report.CanProceed);
        var writable = Item(report, BackupPreCheckIds.StagingServerWritable);
        Assert.Equal(BackupPreCheckStatus.Failed, writable.Status);
        Assert.Equal(SqlServerPreCheckReasons.StagingServerDirectoryMissing, writable.ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(report, BackupPreCheckIds.StagingMapping).Status);
        Assert.DoesNotContain(missing.Value, writable.Message, StringComparison.Ordinal);

        var exists = (await fixture.QueryAsync($"EXEC master.dbo.xp_fileexist {SqlServerBackupCommandBuilder.QuoteStringLiteral(missing.Value)}")).Single();
        Assert.Equal(0, Convert.ToInt32(exists[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AMissingAgentDirectoryFailsAgentAccessAndSkipsTheServerProbe()
    {
        var staging = StagingArea.Mapped(fixture.Staging.ServerDirectory, new AgentPath(Path.Combine(Path.GetTempPath(), $"bp-it-missing-{fixture.Suffix}")));

        var report = await PreCheckAsync(fixture.Admin, staging);

        Assert.Equal(SqlServerPreCheckReasons.StagingAgentDirectoryMissing, Item(report, BackupPreCheckIds.StagingAgentAccess).ReasonCode);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(report, BackupPreCheckIds.StagingServerWritable).Status);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(report, BackupPreCheckIds.StagingMapping).Status);
        Assert.Equal(BackupPreCheckStatus.Skipped, Item(report, BackupPreCheckIds.StagingDiskSpace).Status);
        Assert.False(report.CanProceed);
    }

    [Fact]
    public async Task NoStagingItemsAppearWhenNoStagingWasRequested()
    {
        var report = await _provider.PreCheckFullBackupAsync(
            fixture.Settings, fixture.Admin, new FullBackupPreCheckRequest(fixture.Source), CancellationToken.None);

        Assert.DoesNotContain(report.Items, item => item.CheckId.StartsWith("staging.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExtensionStillRunsAfterTheStagingChecks()
    {
        var provider = new SqlServerBackupProvider(NullLogger<SqlServerBackupProvider>.Instance, [new MarkerExtension()]);

        var report = await provider.PreCheckFullBackupAsync(
            fixture.Settings, fixture.Admin, new FullBackupPreCheckRequest(fixture.Source, staging: fixture.Staging), CancellationToken.None);

        Assert.Equal("extension.marker", report.Items[^1].CheckId);
        Assert.Equal(BackupPreCheckIds.StagingDiskSpace, report.Items[^2].CheckId);
    }

    private sealed class MarkerExtension : ISqlServerPreCheckExtension
    {
        public Task<IReadOnlyList<BackupPreCheckItem>> CheckAsync(SqlServerPreCheckContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BackupPreCheckItem>>([new BackupPreCheckItem("extension.marker", BackupPreCheckStatus.Passed, "ok", "Marker.")]);
    }
}
