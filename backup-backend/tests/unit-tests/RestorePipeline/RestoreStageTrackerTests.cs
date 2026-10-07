using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.UnitTests.RestorePipeline;

public sealed class RestoreStageTrackerTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
    private readonly List<RestoreProgress> _reports = [];

    private RestoreStageTracker Create() => new(new Collector(_reports), _time);

    [Fact]
    public void StagesMoveForwardAndEveryTransitionIsRecordedAndReported()
    {
        var tracker = Create();

        tracker.Enter(RestoreExecutionStage.Preparing);
        _time.Advance(TimeSpan.FromSeconds(2));
        tracker.Enter(RestoreExecutionStage.DownloadingArtifact);
        _time.Advance(TimeSpan.FromSeconds(3));
        tracker.Enter(RestoreExecutionStage.Completed);

        var records = tracker.Records;
        Assert.Equal([RestoreExecutionStage.Preparing, RestoreExecutionStage.DownloadingArtifact, RestoreExecutionStage.Completed], records.Select(r => r.Stage));
        Assert.Equal(TimeSpan.FromSeconds(2), records[0].FinishedAt - records[0].StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(3), records[1].FinishedAt - records[1].StartedAt);
        Assert.Equal(records[2].StartedAt, records[2].FinishedAt);
        Assert.Equal([RestoreExecutionStage.Preparing, RestoreExecutionStage.DownloadingArtifact, RestoreExecutionStage.Completed], _reports.Select(r => r.Stage));
        Assert.Equal(100, _reports[^1].Percent);
    }

    [Fact]
    public void StagesCanBeSkippedButNeverGoBackwards()
    {
        var tracker = Create();
        tracker.Enter(RestoreExecutionStage.Restoring);

        Assert.Throws<InvalidOperationException>(() => tracker.Enter(RestoreExecutionStage.InspectingBackup));
        tracker.Enter(RestoreExecutionStage.Finalizing);
        Assert.Equal(RestoreExecutionStage.Finalizing, tracker.Current);
    }

    [Fact]
    public void EnteringTheCurrentStageAgainIsANoOp()
    {
        var tracker = Create();
        tracker.Enter(RestoreExecutionStage.Restoring);
        tracker.Enter(RestoreExecutionStage.Restoring);

        Assert.Single(tracker.Records);
        Assert.Single(_reports);
    }

    [Theory]
    [InlineData(RestoreExecutionStage.Completed)]
    [InlineData(RestoreExecutionStage.Failed)]
    [InlineData(RestoreExecutionStage.Cancelled)]
    public void ATerminalStageClosesTheTimeline(RestoreExecutionStage terminal)
    {
        var tracker = Create();
        tracker.Enter(RestoreExecutionStage.Restoring);
        tracker.Enter(terminal);

        Assert.Throws<InvalidOperationException>(() => tracker.Enter(RestoreExecutionStage.Finalizing));
        Assert.Throws<InvalidOperationException>(() => tracker.Enter(terminal == RestoreExecutionStage.Failed ? RestoreExecutionStage.Cancelled : RestoreExecutionStage.Failed));
        var before = _reports.Count;
        tracker.Report(terminal, 10, 1, 2);
        Assert.Equal(before, _reports.Count);
    }

    [Fact]
    public void FailedAndCancelledAreReachableFromAnyStage()
    {
        foreach (var terminal in new[] { RestoreExecutionStage.Failed, RestoreExecutionStage.Cancelled })
        {
            foreach (var stage in Enum.GetValues<RestoreExecutionStage>().Where(s => s < RestoreExecutionStage.Completed))
            {
                var tracker = Create();
                tracker.Enter(stage);
                tracker.Enter(terminal);
                Assert.Equal(terminal, tracker.Current);
            }
        }
    }

    [Fact]
    public void ReportsForAStageThatIsNotCurrentAreIgnoredAndValuesAreClamped()
    {
        var tracker = Create();
        tracker.Enter(RestoreExecutionStage.DownloadingArtifact);
        _reports.Clear();

        tracker.Report(RestoreExecutionStage.Restoring, 50, 1, 2);
        tracker.Report(RestoreExecutionStage.DownloadingArtifact, 250, -5, 10);
        tracker.Report(RestoreExecutionStage.DownloadingArtifact, double.NaN, 3, null);

        Assert.Equal(2, _reports.Count);
        Assert.Equal(100, _reports[0].Percent);
        Assert.Null(_reports[0].Bytes);
        Assert.Null(_reports[1].Percent);
        Assert.Equal(3, _reports[1].Bytes);
    }

    [Fact]
    public void ForwardedProviderProgressRecordsStageChangesAndIgnoresTerminalAndBackwardStages()
    {
        var tracker = Create();
        tracker.Enter(RestoreExecutionStage.PreparingTarget);

        tracker.Forward(new RestoreProgress(RestoreExecutionStage.PreparingTarget, null, null, null, _time.GetUtcNow()));
        tracker.Forward(new RestoreProgress(RestoreExecutionStage.Restoring, 10, 100, 1000, _time.GetUtcNow()));
        tracker.Forward(new RestoreProgress(RestoreExecutionStage.PreparingTarget, null, null, null, _time.GetUtcNow()));
        tracker.Forward(new RestoreProgress(RestoreExecutionStage.Completed, 100, null, null, _time.GetUtcNow()));
        tracker.Forward(new RestoreProgress(RestoreExecutionStage.Finalizing, null, null, null, _time.GetUtcNow()));

        Assert.Equal(
            [RestoreExecutionStage.PreparingTarget, RestoreExecutionStage.Restoring, RestoreExecutionStage.Finalizing],
            tracker.Records.Select(r => r.Stage));
        Assert.Contains(_reports, r => r is { Stage: RestoreExecutionStage.Restoring, Percent: 10, Bytes: 100, TotalBytes: 1000 });
    }

    [Fact]
    public void AConsumerThatThrowsDoesNotBreakTracking()
    {
        var tracker = new RestoreStageTracker(new ThrowingConsumer(), _time);

        tracker.Enter(RestoreExecutionStage.Preparing);
        tracker.Enter(RestoreExecutionStage.Failed);

        Assert.Equal(RestoreExecutionStage.Failed, tracker.Current);
        Assert.Equal(2, tracker.Records.Count);
    }

    [Fact]
    public void ConcurrentReportsAreDeliveredOneAtATime()
    {
        var tracker = Create();
        tracker.Enter(RestoreExecutionStage.Restoring);

        Parallel.For(0, 500, index => tracker.Report(RestoreExecutionStage.Restoring, index % 100, index, 1000));

        Assert.Equal(501, _reports.Count);
    }

    [Fact]
    public void AnUnknownStageIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Create().Enter((RestoreExecutionStage)99));

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class Collector(List<RestoreProgress> sink) : IProgress<RestoreProgress>
    {
        public void Report(RestoreProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }

    private sealed class ThrowingConsumer : IProgress<RestoreProgress>
    {
        public void Report(RestoreProgress value) => throw new InvalidOperationException("consumer failure");
    }
}
