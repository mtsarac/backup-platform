using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.UnitTests.Backup;

public sealed class BackupStageTrackerTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
    private readonly List<BackupProgress> _reports = [];

    private BackupStageTracker Create() => new(new Collector(_reports), _time);

    [Fact]
    public void StagesMoveForwardAndEveryTransitionIsRecordedAndReported()
    {
        var tracker = Create();

        tracker.Enter(BackupExecutionStage.Preparing);
        _time.Advance(TimeSpan.FromSeconds(2));
        tracker.Enter(BackupExecutionStage.BackingUp);
        _time.Advance(TimeSpan.FromSeconds(3));
        tracker.Enter(BackupExecutionStage.Completed);

        var records = tracker.Records;
        Assert.Equal([BackupExecutionStage.Preparing, BackupExecutionStage.BackingUp, BackupExecutionStage.Completed], records.Select(r => r.Stage));
        Assert.Equal(TimeSpan.FromSeconds(2), records[0].FinishedAt - records[0].StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(3), records[1].FinishedAt - records[1].StartedAt);
        Assert.Equal(records[2].StartedAt, records[2].FinishedAt);
        Assert.Equal([BackupExecutionStage.Preparing, BackupExecutionStage.BackingUp, BackupExecutionStage.Completed], _reports.Select(r => r.Stage));
        Assert.Equal(100, _reports[^1].Percent);
    }

    [Fact]
    public void StagesCanBeSkippedButNeverGoBackwards()
    {
        var tracker = Create();
        tracker.Enter(BackupExecutionStage.Uploading);

        Assert.Throws<InvalidOperationException>(() => tracker.Enter(BackupExecutionStage.HashingSource));
        tracker.Enter(BackupExecutionStage.VerifyingReplica);
        Assert.Equal(BackupExecutionStage.VerifyingReplica, tracker.Current);
    }

    [Theory]
    [InlineData(BackupExecutionStage.Completed)]
    [InlineData(BackupExecutionStage.Failed)]
    [InlineData(BackupExecutionStage.Cancelled)]
    public void ATerminalStageClosesTheTimeline(BackupExecutionStage terminal)
    {
        var tracker = Create();
        tracker.Enter(BackupExecutionStage.Uploading);
        tracker.Enter(terminal);

        Assert.Throws<InvalidOperationException>(() => tracker.Enter(BackupExecutionStage.VerifyingReplica));
        Assert.Throws<InvalidOperationException>(() => tracker.Enter(terminal == BackupExecutionStage.Failed ? BackupExecutionStage.Cancelled : BackupExecutionStage.Failed));
        var before = _reports.Count;
        tracker.Report(terminal, 10, 1, 2);
        Assert.Equal(before, _reports.Count);
    }

    [Fact]
    public void FailedAndCancelledAreReachableFromAnyStage()
    {
        foreach (var terminal in new[] { BackupExecutionStage.Failed, BackupExecutionStage.Cancelled })
        {
            foreach (var stage in Enum.GetValues<BackupExecutionStage>().Where(s => s < BackupExecutionStage.Completed))
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
        tracker.Enter(BackupExecutionStage.HashingSource);
        _reports.Clear();

        tracker.Report(BackupExecutionStage.Uploading, 50, 1, 2);
        tracker.Report(BackupExecutionStage.HashingSource, 250, -5, 10);
        tracker.Report(BackupExecutionStage.HashingSource, double.NaN, 3, null);

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
        tracker.Enter(BackupExecutionStage.Preparing);

        tracker.Forward(new BackupProgress(BackupExecutionStage.Preparing, null, null, null, _time.GetUtcNow()));
        tracker.Forward(new BackupProgress(BackupExecutionStage.BackingUp, 10, 100, 1000, _time.GetUtcNow()));
        tracker.Forward(new BackupProgress(BackupExecutionStage.Preparing, null, null, null, _time.GetUtcNow()));
        tracker.Forward(new BackupProgress(BackupExecutionStage.Completed, 100, null, null, _time.GetUtcNow()));
        tracker.Forward(new BackupProgress(BackupExecutionStage.Verifying, null, null, null, _time.GetUtcNow()));

        Assert.Equal(
            [BackupExecutionStage.Preparing, BackupExecutionStage.BackingUp, BackupExecutionStage.Verifying],
            tracker.Records.Select(r => r.Stage));
        Assert.Contains(_reports, r => r is { Stage: BackupExecutionStage.BackingUp, Percent: 10, Bytes: 100, TotalBytes: 1000 });
    }

    [Fact]
    public void AConsumerThatThrowsDoesNotBreakTracking()
    {
        var tracker = new BackupStageTracker(new ThrowingConsumer(), _time);

        tracker.Enter(BackupExecutionStage.Preparing);
        tracker.Enter(BackupExecutionStage.Failed);

        Assert.Equal(BackupExecutionStage.Failed, tracker.Current);
        Assert.Equal(2, tracker.Records.Count);
    }

    [Fact]
    public void ConcurrentReportsAreDeliveredOneAtATime()
    {
        var tracker = Create();
        tracker.Enter(BackupExecutionStage.BackingUp);

        Parallel.For(0, 500, index => tracker.Report(BackupExecutionStage.BackingUp, index % 100, index, 1000));

        Assert.Equal(501, _reports.Count);
    }

    [Fact]
    public void StagedFileNamesAreSafeUniqueAndDoNotDependOnTheDatabaseNameForUniqueness()
    {
        var started = new DateTimeOffset(2026, 10, 5, 8, 30, 15, TimeSpan.FromHours(3));
        var id = Guid.Parse("a1b2c3d4-0000-0000-0000-000000000000");

        Assert.Equal("Sales_20261005T053015Z_a1b2c3d4_FULL.bak", ManagedBackupLayout.StagedFileName("Sales", started, id));
        Assert.Equal("bp_it_q_uote_d____a___20261005T053015Z_a1b2c3d4_FULL.bak", ManagedBackupLayout.StagedFileName("bp_it_q]uote'd/..\\a;\"", started, id));
        Assert.StartsWith("database_", ManagedBackupLayout.StagedFileName("", started, id), StringComparison.Ordinal);
        var longName = ManagedBackupLayout.StagedFileName(new string('x', 500), started, id);
        Assert.True(longName.Length < 100);
        Assert.NotEqual(ManagedBackupLayout.StagedFileName("Sales", started, id), ManagedBackupLayout.StagedFileName("Sales", started, Guid.NewGuid()));
    }

    [Fact]
    public void EveryNameTheAgentStagesIsRecognisedAsManaged()
    {
        var started = new DateTimeOffset(2026, 10, 5, 8, 30, 15, TimeSpan.FromHours(3));
        string[] databases =
        [
            "Sales", "", " ", "bp_it_q]uote'd/..\\a;\"", "..", "../../etc/passwd", "a b", "naïve-データベース", "line\nbreak", "tab\t", "NUL\0", "x:y*z?<>|",
            new string('x', 500), new string('_', 48), new string('-', 49), "emoji-\U0001F600"
        ];

        foreach (var database in databases)
        {
            var name = ManagedBackupLayout.StagedFileName(database, started, Guid.NewGuid());
            Assert.True(ManagedBackupLayout.IsManagedArtifactName(name), $"not recognised: {name}");
        }
    }

    [Theory]
    [InlineData("Sales_20261005T053015Z_a1b2c3d4_FULL.bin")]
    [InlineData("Sales_20261005T053015Z_a1b2c3d4_full.bak")]
    [InlineData("Sales_20261005T053015Z_a1b2c3d4_DIFF.bak")]
    [InlineData("Sales_20261005T053015Z_A1B2C3D4_FULL.bak")]
    [InlineData("Sales_20261005T053015Z_a1b2c3d_FULL.bak")]
    [InlineData("Sales_20261005T053015Z_a1b2c3d4e_FULL.bak")]
    [InlineData("Sales_20261005T053015Z_FULL.bak")]
    [InlineData("Sales_20261005T053015_a1b2c3d4_FULL.bak")]
    [InlineData("Sales_20261005T053015Z_a1b2c3d4_FULL.bak.partial")]
    [InlineData(".Sales_20261005T053015Z_a1b2c3d4_FULL.bak")]
    [InlineData("_20261005T053015Z_a1b2c3d4_FULL.bak")]
    [InlineData("Sales Ltd_20261005T053015Z_a1b2c3d4_FULL.bak")]
    [InlineData("Sales_20261005T053015Z_a1b2c3d4_FULL.bak ")]
    [InlineData("Sales_20261005T053015Z_a1b2c3d4_FULL.bak\n")]
    [InlineData("sub/Sales_20261005T053015Z_a1b2c3d4_FULL.bak")]
    [InlineData("sub\\Sales_20261005T053015Z_a1b2c3d4_FULL.bak")]
    [InlineData("../Sales_20261005T053015Z_a1b2c3d4_FULL.bak")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("Sales_FULL.bak")]
    [InlineData("Sales.bak")]
    [InlineData(".bak")]
    [InlineData("backup.bak")]
    [InlineData(null)]
    public void NamesOutsideTheManagedGrammarAreNeverRecognised(string? name) =>
        Assert.False(ManagedBackupLayout.IsManagedArtifactName(name));

    [Fact]
    public void TheAttemptScopeIsTheDashlessLowercaseGuidsJoinedBySlashes()
    {
        var tenant = Guid.Parse("0b1f6c2e-8d0a-4f7e-9a53-2f4c1d9d7a10");
        var job = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var run = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var attempt = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

        Assert.Equal(
            "0b1f6c2e8d0a4f7e9a532f4c1d9d7a10/11111111222233334444555555555555/aaaaaaaabbbbccccddddeeeeeeeeeeee/0123456789abcdef0123456789abcdef",
            ManagedBackupLayout.AttemptScope(tenant, job, run, attempt));
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class Collector(List<BackupProgress> sink) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }

    private sealed class ThrowingConsumer : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => throw new InvalidOperationException("consumer failure");
    }
}
