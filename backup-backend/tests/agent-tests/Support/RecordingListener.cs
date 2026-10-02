using System.Collections.Concurrent;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.AgentTests.Support;

internal sealed class RecordingListener : IAgentPushChannelListener
{
    private readonly ConcurrentQueue<string> _events = new();
    private int _hints;
    private int _reconnected;

    public IReadOnlyList<string> Events => _events.ToArray();

    public int Hints => Volatile.Read(ref _hints);

    public int Reconnected => Volatile.Read(ref _reconnected);

    public AgentBackendFailure? ClosedFailure { get; private set; }

    public AgentProtocolNotice? Notice { get; private set; }

    public TaskCompletionSource ReconnectingSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReconnectedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ClosedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource NoticeSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void OnPollHint(AgentPollHint hint)
    {
        Interlocked.Increment(ref _hints);
        _events.Enqueue($"hint:{hint.Reason}");
    }

    public void OnProtocolNotice(AgentProtocolNotice notice)
    {
        Notice = notice;
        _events.Enqueue("notice");
        NoticeSignal.TrySetResult();
    }

    public void OnReconnecting(AgentBackendFailure? reason)
    {
        _events.Enqueue("reconnecting");
        ReconnectingSignal.TrySetResult();
    }

    public void OnReconnected()
    {
        Interlocked.Increment(ref _reconnected);
        _events.Enqueue("reconnected");
        ReconnectedSignal.TrySetResult();
    }

    public void OnClosed(AgentBackendFailure? failure)
    {
        ClosedFailure = failure;
        _events.Enqueue("closed");
        ClosedSignal.TrySetResult();
    }
}
