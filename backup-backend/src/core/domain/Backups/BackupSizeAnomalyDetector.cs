namespace BackupPlatform.Domain.Backups;

public static class BackupSizeAnomalyStatuses
{
    public const string BaselinePending = "BaselinePending";
    public const string Normal = "Normal";
    public const string SizeAnomalyWarning = "SizeAnomalyWarning";
}

public sealed record BackupSizeAnomalyResult(
    string Status,
    bool IsAnomaly,
    double? DeviationRatio,
    long? BaselineMeanBytes)
{
    public static readonly BackupSizeAnomalyResult BaselinePendingResult =
        new(BackupSizeAnomalyStatuses.BaselinePending, false, null, null);

    public static BackupSizeAnomalyResult NormalResult(double deviationRatio, long baselineMeanBytes) =>
        new(BackupSizeAnomalyStatuses.Normal, false, deviationRatio, baselineMeanBytes);

    public static BackupSizeAnomalyResult WarningResult(double deviationRatio, long baselineMeanBytes) =>
        new(BackupSizeAnomalyStatuses.SizeAnomalyWarning, true, deviationRatio, baselineMeanBytes);
}

public static class BackupSizeAnomalyDetector
{
    private const int MinBaselineCount = 3;
    private const int MaxBaselineCount = 10;
    private const double AnomalyDeviationThreshold = 0.30;
    private const double AnomalyZScoreThreshold = 2.0;

    /// <summary>
    /// Evaluates current artifact size against rolling baseline (up to 10 most recent successful runs).
    /// Guarded against division by zero when standard deviation is zero.
    /// Flags SizeAnomalyWarning only when |Δ| > 2σ AND |Δ| > 30%.
    /// </summary>
    public static BackupSizeAnomalyResult Evaluate(long currentSizeBytes, IReadOnlyList<long> baselineSizes)
    {
        if (baselineSizes.Count < MinBaselineCount)
        {
            return BackupSizeAnomalyResult.BaselinePendingResult;
        }

        var sample = baselineSizes.Take(MaxBaselineCount).ToList();
        double sum = 0;
        foreach (var size in sample)
        {
            sum += size;
        }

        var mean = sum / sample.Count;
        var meanBytes = (long)Math.Round(mean);

        double varianceSum = 0;
        foreach (var size in sample)
        {
            var diff = size - mean;
            varianceSum += diff * diff;
        }

        var variance = varianceSum / sample.Count;
        var stdDev = Math.Sqrt(variance);

        var delta = currentSizeBytes - mean;
        var deviationRatio = mean > 0 ? delta / mean : 0.0;
        var absRelativeDeviation = Math.Abs(deviationRatio);

        bool isAnomaly;
        if (stdDev <= 0.000001)
        {
            // Zero variance: all prior baseline runs had identical size.
            // Any deviation > 30% exceeds zero variance.
            isAnomaly = absRelativeDeviation > AnomalyDeviationThreshold;
        }
        else
        {
            var zScore = Math.Abs(delta) / stdDev;
            isAnomaly = zScore > AnomalyZScoreThreshold && absRelativeDeviation > AnomalyDeviationThreshold;
        }

        return isAnomaly
            ? BackupSizeAnomalyResult.WarningResult(deviationRatio, meanBytes)
            : BackupSizeAnomalyResult.NormalResult(deviationRatio, meanBytes);
    }
}
