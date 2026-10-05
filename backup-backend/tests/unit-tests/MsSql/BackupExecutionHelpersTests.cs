using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class BackupExecutionHelpersTests
{
    private sealed class Recorder : IProgress<BackupProgress>
    {
        public List<BackupProgress> Reports { get; } = [];

        public void Report(BackupProgress value) => Reports.Add(value);
    }

    private sealed class ThrowingProgress : IProgress<BackupProgress>
    {
        public int Calls { get; private set; }

        public void Report(BackupProgress value)
        {
            Calls++;
            throw new InvalidOperationException("consumer is broken");
        }
    }

    private static BackupProgressReporter Reporter(IProgress<BackupProgress>? progress) =>
        new(progress, TimeProvider.System, NullLogger.Instance);

    [Theory]
    [InlineData("10 percent processed.", true, 10)]
    [InlineData("  7 percent processed.", true, 7)]
    [InlineData("100 percent processed.", true, 100)]
    [InlineData("0 percent processed.", true, 0)]
    [InlineData("101 percent processed.", false, 0)]
    [InlineData("-5 percent processed.", false, 0)]
    [InlineData("percent processed.", false, 0)]
    [InlineData("99999999999999999999 percent", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void StatsMessagesAreParsedDefensively(string? message, bool parsed, int expected)
    {
        Assert.Equal(parsed, BackupProgressReporter.TryParsePercent(message, out var percent));
        if (parsed)
        {
            Assert.Equal(expected, percent);
        }
    }

    [Fact]
    public void StagesAreReportedInOrderAndNeverBackwards()
    {
        var recorder = new Recorder();
        var reporter = Reporter(recorder);

        Assert.True(reporter.EnterStage(BackupExecutionStage.Preparing));
        Assert.True(reporter.EnterStage(BackupExecutionStage.BackingUp));
        Assert.False(reporter.EnterStage(BackupExecutionStage.Preparing));
        Assert.False(reporter.EnterStage(BackupExecutionStage.BackingUp));
        Assert.True(reporter.EnterStage(BackupExecutionStage.Verifying));

        Assert.Equal(
            [BackupExecutionStage.Preparing, BackupExecutionStage.BackingUp, BackupExecutionStage.Verifying],
            recorder.Reports.Select(report => report.Stage));
        Assert.Equal(BackupExecutionStage.Verifying, reporter.CurrentStage);
    }

    [Theory]
    [InlineData(BackupExecutionStage.Completed)]
    [InlineData(BackupExecutionStage.Failed)]
    [InlineData(BackupExecutionStage.Cancelled)]
    [InlineData(BackupExecutionStage.Uploading)]
    public void TheProviderNeverReportsTheTerminalOrPipelineStages(BackupExecutionStage stage) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Reporter(null).EnterStage(stage));

    [Fact]
    public void PercentNeverDecreasesAndOnlyCountsWhileBackingUp()
    {
        var recorder = new Recorder();
        var reporter = Reporter(recorder);
        reporter.ReportPercent(5);
        reporter.EnterStage(BackupExecutionStage.BackingUp);

        reporter.ReportPercent(10);
        reporter.ReportPercent(30);
        reporter.ReportPercent(20);
        reporter.ReportPercent(30);
        reporter.ReportPercent(101);
        reporter.ReportBytes(4096);
        reporter.EnterStage(BackupExecutionStage.Verifying);
        reporter.ReportPercent(90);

        var percents = recorder.Reports.Where(report => report.Stage == BackupExecutionStage.BackingUp && report.Bytes is null).Select(report => report.Percent);
        Assert.Equal([null, 10d, 30d, 30d], percents);
        var bytes = Assert.Single(recorder.Reports, report => report.Bytes is not null);
        Assert.Equal(4096, bytes.Bytes);
        Assert.Equal(30d, bytes.Percent);
        Assert.DoesNotContain(recorder.Reports, report => report.Stage == BackupExecutionStage.Verifying && report.Percent is not null);
    }

    [Fact]
    public void AConsumerThatThrowsIsSwallowedOnEveryKindOfReport()
    {
        var consumer = new ThrowingProgress();
        var reporter = Reporter(consumer);

        var exception = Record.Exception(() =>
        {
            reporter.EnterStage(BackupExecutionStage.Preparing);
            reporter.EnterStage(BackupExecutionStage.BackingUp);
            reporter.ReportPercent(10);
            reporter.ReportBytes(1);
            reporter.EnterStage(BackupExecutionStage.Verifying);
        });

        Assert.Null(exception);
        Assert.Equal(5, consumer.Calls);
    }

    [Theory]
    [InlineData("Succeeded", StagingFailurePolicy.Delete, true, "None")]
    [InlineData("Cancelled", StagingFailurePolicy.Delete, true, "Delete")]
    [InlineData("Cancelled", StagingFailurePolicy.Keep, true, "Delete")]
    [InlineData("BackupFailed", StagingFailurePolicy.Delete, true, "Delete")]
    [InlineData("BackupFailed", StagingFailurePolicy.Keep, true, "Delete")]
    [InlineData("VerificationFailed", StagingFailurePolicy.Delete, true, "Delete")]
    [InlineData("VerificationFailed", StagingFailurePolicy.Keep, true, "Keep")]
    [InlineData("Cancelled", StagingFailurePolicy.Delete, false, "None")]
    [InlineData("BackupFailed", StagingFailurePolicy.Delete, false, "None")]
    public void CleanupFollowsWhoCreatedTheFileAndHowTheRunEnded(
        string end, StagingFailurePolicy policy, bool backupIssued, string expected) =>
        Assert.Equal(Enum.Parse<StagingCleanupAction>(expected), StagingCleanupPolicy.Decide(Enum.Parse<ExecutionEnd>(end), policy, backupIssued));

    [Fact]
    public void ACleanupFailureOnlyAddsAWarningAndNamesTheLeftoverFile()
    {
        var (cleanup, warning) = StagingCleanupPolicy.Describe(StagingCleanupAction.Delete, fileExisted: true, deleted: false);

        Assert.Equal(StagingCleanupStatus.Failed, cleanup.Status);
        Assert.NotNull(cleanup.ReasonCode);
        Assert.Equal(SqlServerBackupWarningCodes.StagingCleanupFailed, warning?.Code);
    }

    [Theory]
    [InlineData("Delete", true, true, StagingCleanupStatus.Deleted)]
    [InlineData("Delete", false, true, StagingCleanupStatus.NotNeeded)]
    [InlineData("Keep", true, false, StagingCleanupStatus.Kept)]
    [InlineData("Keep", false, false, StagingCleanupStatus.NotNeeded)]
    [InlineData("None", true, false, StagingCleanupStatus.NotNeeded)]
    public void CleanupStatusReportsWhatActuallyHappened(string action, bool existed, bool deleted, StagingCleanupStatus expected)
    {
        var (cleanup, warning) = StagingCleanupPolicy.Describe(Enum.Parse<StagingCleanupAction>(action), existed, deleted);

        Assert.Equal(expected, cleanup.Status);
        Assert.Null(warning);
    }

    [Fact]
    public void ACleanupProblemNeverReplacesACancellationOrTheFirstFailure()
    {
        var state = new FullBackupExecutionState();
        state.Cancel(BackupExecutionStage.BackingUp);
        state.Fail(BackupFailureKind.SqlBackupFailed, BackupExecutionStage.BackingUp, "late", "A later failure must not win.");
        var (cleanup, warning) = StagingCleanupPolicy.Describe(StagingCleanupAction.Delete, fileExisted: true, deleted: false);
        state.AddWarning(warning!);

        var result = state.ToResult(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), cleanup);

        Assert.Equal(FullBackupStatus.Cancelled, result.Status);
        Assert.Equal(BackupFailureKind.Cancelled, result.Failure?.Kind);
        Assert.Equal(StagingCleanupStatus.Failed, result.Cleanup.Status);
        Assert.Contains(result.Warnings, item => item.Code == SqlServerBackupWarningCodes.StagingCleanupFailed);
    }

    [Fact]
    public void AFailedStateNeverHandsOverAStagedFile()
    {
        var state = new FullBackupExecutionState
        {
            StagedFile = new StagedBackupFile(new DatabaseServerPath("/s/a.bak"), new AgentPath(MsSqlTestData.AgentAbsolute("/a/a.bak")), "a.bak", 10)
        };
        state.Fail(BackupFailureKind.VerificationFailed, BackupExecutionStage.Verifying, "verify-failed", "Verification failed.");

        var result = state.ToResult(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, StagingCleanup.NotNeeded);

        Assert.Equal(FullBackupStatus.Failed, result.Status);
        Assert.Null(result.StagedFile);
        Assert.Equal(ExecutionEnd.VerificationFailed, state.End);
    }
}
