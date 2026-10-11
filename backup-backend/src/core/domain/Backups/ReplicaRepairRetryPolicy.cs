namespace BackupPlatform.Domain.Backups;

public static class ReplicaRepairRetryPolicy
{
    public static readonly BackupRetryPolicy DefaultPolicy = new()
    {
        MaxAttempts = 5,
        InitialDelay = TimeSpan.FromSeconds(30),
        MaxDelay = TimeSpan.FromHours(1),
        Backoff = BackupBackoffStrategy.Exponential,
        Jitter = 0.2
    };

    public static bool IsTransient(string? failureCode) => failureCode switch
    {
        ReplicaRepairFailureCodes.StorageUnavailable => true,
        ReplicaRepairFailureCodes.OperationTimedOut => true,
        ReplicaRepairFailureCodes.StorageWriteFailed => true,
        ReplicaRepairFailureCodes.SourceInaccessible => true,
        ReplicaRepairFailureCodes.LeaseLost => true,
        "lease_expired" => true,
        ReplicaRepairFailureCodes.InternalError => true,
        _ => false
    };

    public static TimeSpan CalculateDelay(int attemptCount, double randomSample = 0.0)
    {
        var clampedAttempt = Math.Max(1, attemptCount);
        return DefaultPolicy.DelayAfter(clampedAttempt, Math.Clamp(randomSample, 0.0, 1.0));
    }
}
