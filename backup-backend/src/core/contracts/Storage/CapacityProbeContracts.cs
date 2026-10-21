namespace BackupPlatform.Contracts.Storage;

public static class CapacityProbeProtocol
{
    public const string TaskType = "capacity-probe";
    public const string Capability = "capacity-probe";
}

public static class CapacityProbeFailureCodes
{
    public const string CapacityUnavailable = "CapacityUnavailable";
    public const string TargetNotFound = "TargetNotFound";
    public const string InvalidConfiguration = "InvalidConfiguration";
    public const string AuthenticationFailed = "AuthenticationFailed";
    public const string ConnectionFailed = "ConnectionFailed";
}

public sealed record CapacityProbeTaskRequest
{
    public Guid TaskId { get; init; }
    public Guid ConnectionId { get; init; }
    public Guid TenantId { get; init; }
    public Guid AgentId { get; init; }
    public Guid LeaseToken { get; init; }
    public DateTimeOffset LeaseExpiresAt { get; init; }
    public string ProviderType { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Configuration { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string>? Secret { get; init; }
}

public sealed record CapacityProbeTaskResult
{
    public Guid TaskId { get; init; }
    public Guid ConnectionId { get; init; }
    public bool Succeeded { get; init; }
    public long? TotalBytes { get; init; }
    public long? UsedBytes { get; init; }
    public long? FreeBytes { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static CapacityProbeTaskResult Success(Guid taskId, Guid connectionId, long? totalBytes, long? usedBytes, long? freeBytes) =>
        new()
        {
            TaskId = taskId,
            ConnectionId = connectionId,
            Succeeded = true,
            TotalBytes = totalBytes,
            UsedBytes = usedBytes,
            FreeBytes = freeBytes
        };

    public static CapacityProbeTaskResult Failure(Guid taskId, Guid connectionId, string errorCode, string? errorMessage = null) =>
        new()
        {
            TaskId = taskId,
            ConnectionId = connectionId,
            Succeeded = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
}

public interface ISftpCapacityProbeExecutor
{
    Task<CapacityProbeTaskResult> ProbeAsync(CapacityProbeTaskRequest request, CancellationToken cancellationToken);
}

public sealed record CapacityProbeCompletionRequest
{
    public Guid LeaseToken { get; init; }
    public CapacityProbeTaskResult Result { get; init; } = new();
}
