using System.Diagnostics.CodeAnalysis;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Core.Connectivity;

public enum AgentBackendFailureKind
{
    /// <summary>The credential was not accepted (unknown, invalid, revoked or disabled: the backend does not say which).</summary>
    Unauthorized,
    /// <summary>The protocol window of agent and backend do not overlap.</summary>
    ProtocolIncompatible,
    RateLimited,
    /// <summary>Network error, timeout or 5xx: worth retrying with backoff.</summary>
    Transient,
    /// <summary>Any other refusal (unexpected 4xx, redirect, malformed answer).</summary>
    Rejected,
}

/// <summary>A failed backend interaction. <see cref="Description"/> never contains credentials or headers.</summary>
public sealed record AgentBackendFailure(AgentBackendFailureKind Kind, string Description)
{
    /// <summary>Server-requested wait for <see cref="AgentBackendFailureKind.RateLimited"/>; null when the server gave none.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>The structured error body for <see cref="AgentBackendFailureKind.ProtocolIncompatible"/>, when the transport could read one.</summary>
    public AgentApiError? Error { get; init; }

    public int? StatusCode { get; init; }
}

/// <summary>Outcome of one backend call: either a value or an <see cref="AgentBackendFailure"/>. Network errors are failures, not exceptions.</summary>
public sealed class AgentBackendResult<T>
{
    private readonly T? _value;
    private readonly AgentBackendFailure? _failure;

    private AgentBackendResult(T? value, AgentBackendFailure? failure)
    {
        _value = value;
        _failure = failure;
    }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool IsSuccess => _failure is null;

    public T? Value => _value;

    public AgentBackendFailure? Failure => _failure;

    public static AgentBackendResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new AgentBackendResult<T>(value, null);
    }

    public static AgentBackendResult<T> Fail(AgentBackendFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new AgentBackendResult<T>(default, failure);
    }
}
