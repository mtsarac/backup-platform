using System.Collections.Concurrent;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.AgentTests.Support;

internal sealed class FakeCredentialStore : IAgentCredentialStore
{
    private int _loadCount;

    public FakeCredentialStore(AgentLocalIdentity? identity) => Identity = identity;

    public AgentLocalIdentity? Identity { get; set; }

    public Exception? LoadFailure { get; set; }

    public int LoadCount => Volatile.Read(ref _loadCount);

    public Task<AgentLocalIdentity?> LoadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);
        return LoadFailure is { } failure ? Task.FromException<AgentLocalIdentity?>(failure) : Task.FromResult(Identity);
    }

    public Task SaveAsync(AgentLocalIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed record HeartbeatCall(DateTimeOffset At, Guid CredentialId, string Secret, AgentHeartbeatRequest Request);

internal sealed record PollCall(DateTimeOffset At, Guid CredentialId, string? Cursor);

internal sealed class FakeBackendClient(TimeProvider time) : IAgentBackendClient
{
    private readonly ConcurrentQueue<HeartbeatCall> _heartbeats = new();
    private readonly ConcurrentQueue<PollCall> _polls = new();

    public Uri BackendBaseUri { get; } = new("https://backup.example.test/");

    public Func<int, AgentLocalIdentity, AgentBackendResult<AgentHeartbeatResponse>> OnHeartbeat { get; set; } =
        (_, _) => AgentBackendResult<AgentHeartbeatResponse>.Success(new AgentHeartbeatResponse
        {
            ServerTime = DateTimeOffset.UnixEpoch,
            HeartbeatIntervalSeconds = 30,
            OfflineTimeoutSeconds = 90,
        });

    public Func<int, AgentLocalIdentity, string?, AgentBackendResult<AgentPollResponse>> OnPoll { get; set; } =
        (n, _, _) => AgentBackendResult<AgentPollResponse>.Success(new AgentPollResponse { Cursor = $"cursor-{n}", NextPollAfterSeconds = 0 });

    public IReadOnlyList<HeartbeatCall> Heartbeats => _heartbeats.ToArray();

    public IReadOnlyList<PollCall> Polls => _polls.ToArray();

    public Task<AgentBackendResult<AgentHeartbeatResponse>> SendHeartbeatAsync(AgentLocalIdentity identity, AgentHeartbeatRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _heartbeats.Enqueue(new HeartbeatCall(time.GetUtcNow(), identity.Current.CredentialId, identity.Current.Secret, request));
        return Task.FromResult(OnHeartbeat(_heartbeats.Count, identity));
    }

    public Task<AgentBackendResult<AgentPollResponse>> PollAsync(AgentLocalIdentity identity, string? cursor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _polls.Enqueue(new PollCall(time.GetUtcNow(), identity.Current.CredentialId, cursor));
        return Task.FromResult(OnPoll(_polls.Count, identity, cursor));
    }
}

internal sealed class FakePushChannel(TimeProvider time) : IAgentPushChannel
{
    private readonly ConcurrentQueue<DateTimeOffset> _connects = new();
    private int _stops;

    public Func<int, AgentLocalIdentity, AgentPushConnectResult> OnConnect { get; set; } = (_, _) => AgentPushConnectResult.Connected;

    public AgentLocalIdentity? LastIdentity { get; private set; }

    public IReadOnlyList<DateTimeOffset> ConnectTimes => _connects.ToArray();

    public IAgentPushChannelListener? Listener { get; private set; }

    public int StopCount => Volatile.Read(ref _stops);

    public bool Disposed { get; private set; }

    public Task<AgentPushConnectResult> ConnectAsync(AgentLocalIdentity identity, IAgentPushChannelListener listener, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Listener = listener;
        LastIdentity = identity;
        _connects.Enqueue(time.GetUtcNow());
        return Task.FromResult(OnConnect(_connects.Count, identity));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stops);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal static class Failures
{
    public static AgentPushConnectResult PushFailed(AgentBackendFailureKind kind = AgentBackendFailureKind.Transient, AgentApiError? error = null) =>
        AgentPushConnectResult.Failed(new AgentBackendFailure(kind, "test failure") { Error = error });

    public static AgentBackendResult<T> Fail<T>(AgentBackendFailureKind kind, TimeSpan? retryAfter = null, AgentApiError? error = null) =>
        AgentBackendResult<T>.Fail(new AgentBackendFailure(kind, "test failure") { RetryAfter = retryAfter, Error = error });

    public static AgentApiError ProtocolError(string action = AgentErrorActions.UpgradeAgent) =>
        new() { Error = AgentErrorCodes.ProtocolIncompatible, Action = action };
}
