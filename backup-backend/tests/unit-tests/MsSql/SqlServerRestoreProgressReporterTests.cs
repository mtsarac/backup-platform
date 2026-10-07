using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Restore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerRestoreProgressReporterTests
{
    private sealed class Recorder : IProgress<RestoreProgress>
    {
        public List<RestoreProgress> Reports { get; } = [];

        public void Report(RestoreProgress value) => Reports.Add(value);
    }

    private sealed class Throwing : IProgress<RestoreProgress>
    {
        public int Calls { get; private set; }

        public void Report(RestoreProgress value)
        {
            Calls++;
            throw new InvalidOperationException("consumer failure");
        }
    }

    private static SqlServerRestoreProgressReporter Reporter(IProgress<RestoreProgress>? progress) =>
        new(progress, TimeProvider.System, NullLogger.Instance);

    [Fact]
    public void StagesAreReportedInOrderAndOnlyForward()
    {
        var recorder = new Recorder();
        var reporter = Reporter(recorder);

        Assert.True(reporter.EnterStage(RestoreExecutionStage.PreparingTarget));
        Assert.True(reporter.EnterStage(RestoreExecutionStage.Restoring));
        Assert.False(reporter.EnterStage(RestoreExecutionStage.PreparingTarget));
        Assert.False(reporter.EnterStage(RestoreExecutionStage.Restoring));
        Assert.True(reporter.EnterStage(RestoreExecutionStage.Finalizing));

        Assert.Equal(
            [RestoreExecutionStage.PreparingTarget, RestoreExecutionStage.Restoring, RestoreExecutionStage.Finalizing],
            recorder.Reports.Select(report => report.Stage));
        Assert.Equal(RestoreExecutionStage.Finalizing, reporter.CurrentStage);
    }

    [Theory]
    [InlineData(RestoreExecutionStage.Preparing)]
    [InlineData(RestoreExecutionStage.DownloadingArtifact)]
    [InlineData(RestoreExecutionStage.VerifyingChecksum)]
    [InlineData(RestoreExecutionStage.InspectingBackup)]
    [InlineData(RestoreExecutionStage.Completed)]
    [InlineData(RestoreExecutionStage.Failed)]
    [InlineData(RestoreExecutionStage.Cancelled)]
    public void StagesOfThePipelineAroundTheProviderAreRefused(RestoreExecutionStage stage) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Reporter(null).EnterStage(stage));

    [Fact]
    public void PercentIsReportedOnlyWhileRestoringAndNeverDecreases()
    {
        var recorder = new Recorder();
        var reporter = Reporter(recorder);

        reporter.ReportPercent(10);
        reporter.EnterStage(RestoreExecutionStage.PreparingTarget);
        reporter.ReportPercent(10);
        reporter.EnterStage(RestoreExecutionStage.Restoring);
        reporter.ReportPercent(5);
        reporter.ReportPercent(40);
        reporter.ReportPercent(39);
        reporter.ReportPercent(40);
        reporter.ReportPercent(100);
        reporter.ReportPercent(101);
        reporter.ReportPercent(-1);

        Assert.Equal(
            [null, null, 5.0, 40.0, 40.0, 100.0],
            recorder.Reports.Select(report => report.Percent));
    }

    [Fact]
    public void EachNewStageStartsWithoutAPercentage()
    {
        var recorder = new Recorder();
        var reporter = Reporter(recorder);
        reporter.EnterStage(RestoreExecutionStage.Restoring);
        reporter.ReportPercent(100);

        reporter.EnterStage(RestoreExecutionStage.Finalizing);

        Assert.Null(recorder.Reports[^1].Percent);
        Assert.Equal(RestoreExecutionStage.Finalizing, recorder.Reports[^1].Stage);
    }

    [Fact]
    public void AConsumerThatThrowsNeverAffectsTheReporter()
    {
        var consumer = new Throwing();
        var reporter = Reporter(consumer);

        reporter.EnterStage(RestoreExecutionStage.PreparingTarget);
        reporter.EnterStage(RestoreExecutionStage.Restoring);
        reporter.ReportPercent(50);

        Assert.Equal(3, consumer.Calls);
        Assert.Equal(RestoreExecutionStage.Restoring, reporter.CurrentStage);
    }

    [Fact]
    public void WithoutAConsumerNothingHappensAndTheStageIsStillTracked()
    {
        var reporter = Reporter(null);

        reporter.EnterStage(RestoreExecutionStage.Restoring);
        reporter.ReportPercent(10);

        Assert.Equal(RestoreExecutionStage.Restoring, reporter.CurrentStage);
    }

    [Fact]
    public void TheStageBeforeAnyReportIsPreparingTarget() => Assert.Equal(RestoreExecutionStage.PreparingTarget, Reporter(null).CurrentStage);
}
