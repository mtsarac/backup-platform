namespace BackupPlatform.Domain.Backups;

/// <summary>
/// The closed vocabulary of attempt failure codes and the ONE place that decides whether a failure may be retried.
/// Codes are opaque, provider-independent identifiers: the agent maps engine errors to a code before they reach the backend, so
/// nothing here (or anywhere above Infrastructure) sees a provider exception. An unknown code is never retried.
/// </summary>
public static class BackupFailureCodes
{
    // Coarse stage codes (used when the cause is unclassified).
    public const string PreCheckFailed = "PreCheckFailed";
    public const string SqlBackupFailed = "SqlBackupFailed";
    public const string VerificationFailed = "VerificationFailed";
    public const string HashFailed = "HashFailed";
    public const string StorageWriteFailed = "StorageWriteFailed";
    public const string ReplicaVerificationFailed = "ReplicaVerificationFailed";
    public const string InternalError = "InternalError";

    // Classified causes.
    public const string DatabaseUnavailable = "DatabaseUnavailable";
    public const string OperationTimedOut = "OperationTimedOut";
    public const string StorageUnavailable = "StorageUnavailable";
    public const string DatabaseNotFound = "DatabaseNotFound";
    public const string InvalidConfiguration = "InvalidConfiguration";
    public const string UnsupportedCapability = "UnsupportedCapability";
    public const string AuthenticationFailed = "AuthenticationFailed";
    public const string PermissionDenied = "PermissionDenied";
    public const string StagingInvalid = "StagingInvalid";

    // Outcomes that are not agent-reported failures.
    public const string Cancelled = "Cancelled";
    public const string AgentLost = "AgentLost";
    /// <summary>A scheduled run's first attempt was not claimed before its grace period ended, so the stale work was dropped. Not agent-reported, never retried.</summary>
    public const string ScheduleGraceExpired = "ScheduleGraceExpired";

    private static readonly Dictionary<string, bool> AgentReported = new(StringComparer.Ordinal)
    {
        [PreCheckFailed] = false,
        [SqlBackupFailed] = false,
        [VerificationFailed] = false,
        [HashFailed] = false,
        [StorageWriteFailed] = false,
        // A copy that is missing or inconsistent after upload points at the storage path; a new attempt writes a new object.
        [ReplicaVerificationFailed] = true,
        [InternalError] = false,
        [DatabaseUnavailable] = true,
        [OperationTimedOut] = true,
        [StorageUnavailable] = true,
        [DatabaseNotFound] = false,
        [InvalidConfiguration] = false,
        [UnsupportedCapability] = false,
        [AuthenticationFailed] = false,
        [PermissionDenied] = false,
        [StagingInvalid] = false
    };

    /// <summary>Codes an agent may report together with a Failed outcome.</summary>
    public static IReadOnlyCollection<string> AgentReportable => AgentReported.Keys;

    public static bool IsAgentReportable(string? code) => code is not null && AgentReported.ContainsKey(code);

    /// <summary>
    /// Whether another attempt can reasonably succeed. Cancellation is never retryable, a lost agent always is, an unknown or
    /// absent code is not. Whether an attempt budget remains is a separate question (<see cref="BackupRetryPolicy"/>).
    /// </summary>
    public static bool IsRetryable(string? code) => code switch
    {
        null => false,
        AgentLost => true,
        _ => AgentReported.TryGetValue(code, out var retryable) && retryable
    };
}

public enum BackupOverlapPolicy
{
    /// <summary>A new run is refused while another run of the same job is queued, retrying or executing.</summary>
    Skip = 1,
    /// <summary>A new run is queued behind the active ones.</summary>
    Queue = 2
}

public enum BackupRetryState
{
    /// <summary>No retry is scheduled and none was decided (never failed, succeeded, or cancelled).</summary>
    None = 0,
    /// <summary>The run is RetryPending: a new attempt will be created at NextAttemptAt.</summary>
    Scheduled = 1,
    /// <summary>The failure was retryable but the attempt budget is spent.</summary>
    Exhausted = 2,
    /// <summary>The failure is not retryable.</summary>
    NotRetryable = 3
}
