using BackupPlatform.Domain.Backups;
using Xunit;

namespace BackupPlatform.UnitTests.Backup;

public sealed class BackupSizeAnomalyDetectorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ColdStart_WithFewerThan3Runs_ReturnsBaselinePending(int priorRunsCount)
    {
        var baseline = Enumerable.Repeat(100_000_000L, priorRunsCount).ToList();

        var result = BackupSizeAnomalyDetector.Evaluate(100_000_000L, baseline);

        Assert.Equal(BackupSizeAnomalyStatuses.BaselinePending, result.Status);
        Assert.False(result.IsAnomaly);
        Assert.Null(result.DeviationRatio);
        Assert.Null(result.BaselineMeanBytes);
    }

    [Fact]
    public void NormalVariance_WithinThreshold_ReturnsNormal()
    {
        // 5% drop from baseline
        var baseline = new List<long> { 100_000_000L, 100_000_000L, 100_000_000L };

        var result = BackupSizeAnomalyDetector.Evaluate(95_000_000L, baseline);

        Assert.Equal(BackupSizeAnomalyStatuses.Normal, result.Status);
        Assert.False(result.IsAnomaly);
        Assert.NotNull(result.DeviationRatio);
        Assert.Equal(-0.05, result.DeviationRatio.Value, precision: 2);
        Assert.Equal(100_000_000L, result.BaselineMeanBytes);
    }

    [Fact]
    public void SuddenDrop_40PercentDrop_TriggersAnomalyWarning()
    {
        // 40% drop (> 30% and > 2 sigma)
        var baseline = new List<long> { 100_000_000L, 100_000_000L, 100_000_000L };

        var result = BackupSizeAnomalyDetector.Evaluate(60_000_000L, baseline);

        Assert.Equal(BackupSizeAnomalyStatuses.SizeAnomalyWarning, result.Status);
        Assert.True(result.IsAnomaly);
        Assert.NotNull(result.DeviationRatio);
        Assert.Equal(-0.40, result.DeviationRatio.Value, precision: 2);
        Assert.Equal(100_000_000L, result.BaselineMeanBytes);
    }

    [Fact]
    public void SuddenSpike_300PercentIncrease_TriggersAnomalyWarning()
    {
        // 300% spike (> 30% and > 2 sigma)
        var baseline = new List<long> { 100_000_000L, 100_000_000L, 100_000_000L };

        var result = BackupSizeAnomalyDetector.Evaluate(400_000_000L, baseline);

        Assert.Equal(BackupSizeAnomalyStatuses.SizeAnomalyWarning, result.Status);
        Assert.True(result.IsAnomaly);
        Assert.NotNull(result.DeviationRatio);
        Assert.Equal(3.0, result.DeviationRatio.Value, precision: 2);
        Assert.Equal(100_000_000L, result.BaselineMeanBytes);
    }

    [Fact]
    public void ZeroVariance_DoesNotThrowDivideByZero_AndDetectsDeviationOver30Percent()
    {
        var baseline = new List<long> { 50_000_000L, 50_000_000L, 50_000_000L, 50_000_000L };

        // 32% increase (> 30%) with sigma == 0
        var anomaly = BackupSizeAnomalyDetector.Evaluate(66_000_000L, baseline);
        Assert.True(anomaly.IsAnomaly);
        Assert.Equal(BackupSizeAnomalyStatuses.SizeAnomalyWarning, anomaly.Status);

        // 20% increase (<= 30%) with sigma == 0
        var normal = BackupSizeAnomalyDetector.Evaluate(60_000_000L, baseline);
        Assert.False(normal.IsAnomaly);
        Assert.Equal(BackupSizeAnomalyStatuses.Normal, normal.Status);
    }

    [Fact]
    public void SmallStandardDeviation_DoesNotTriggerFalsePositive_WhenDeviationUnder30Percent()
    {
        // Baseline with small variance: [100, 101, 99], mu = 100, sigma = ~0.816
        // Current = 103 -> deviation = 3 -> 3.67 sigma (> 2 sigma), but only 3% (< 30%)
        var baseline = new List<long> { 100_000_000L, 101_000_000L, 99_000_000L };

        var result = BackupSizeAnomalyDetector.Evaluate(103_000_000L, baseline);

        Assert.False(result.IsAnomaly);
        Assert.Equal(BackupSizeAnomalyStatuses.Normal, result.Status);
    }

    [Fact]
    public void MovingWindow_OnlyConsidersUpTo10RecentRuns()
    {
        // 12 runs: oldest 2 are huge (1 GB), but recent 10 are all 100 MB
        var baseline = new List<long>
        {
            100_000_000L, 100_000_000L, 100_000_000L, 100_000_000L, 100_000_000L,
            100_000_000L, 100_000_000L, 100_000_000L, 100_000_000L, 100_000_000L,
            1_000_000_000L, 1_000_000_000L
        };

        var result = BackupSizeAnomalyDetector.Evaluate(95_000_000L, baseline);

        Assert.Equal(BackupSizeAnomalyStatuses.Normal, result.Status);
        Assert.Equal(100_000_000L, result.BaselineMeanBytes);
    }
}
