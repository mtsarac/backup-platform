using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Core.Connectivity;

/// <summary>Result of <see cref="IAgentPushChannel.ConnectAsync"/>.</summary>
public sealed class AgentPushConnectResult
{
    private AgentPushConnectResult(AgentBackendFailure? failure) => Failure = failure;

    public static AgentPushConnectResult Connected { get; } = new(null);

    public bool IsConnected => Failure is null;

    public AgentBackendFailure? Failure { get; }

    public static AgentPushConnectResult Failed(AgentBackendFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new AgentPushConnectResult(failure);
    }
}

/// <summary>
/// Receives what happens on a connected push channel. Called from arbitrary transport threads; implementations must not
/// block or throw.
/// </summary>
public interface IAgentPushChannelListener
{
    void OnPollHint(AgentPollHint hint);

    void OnProtocolNotice(AgentProtocolNotice notice);

    /// <summary>The connection was lost and the channel is retrying on its own; the connection is not usable until <see cref="OnReconnected"/>.</summary>
    void OnReconnecting(AgentBackendFailure? reason);

    void OnReconnected();

    /// <summary>The channel is over and will not recover on its own; <paramref name="failure"/> is null for a clean server-side close.</summary>
    void OnClosed(AgentBackendFailure? failure);
}

/// <summary>
/// The server-to-agent push channel. One connection at a time: connecting again replaces the previous connection.
/// After <see cref="ConnectAsync"/> succeeds, the channel may retry internally after connection loss (reporting
/// <see cref="IAgentPushChannelListener.OnReconnecting"/>/<see cref="IAgentPushChannelListener.OnReconnected"/>) and only
/// reports <see cref="IAgentPushChannelListener.OnClosed"/> when it gives up. Implementations never throw for network
/// errors, only for cancellation.
/// </summary>
public interface IAgentPushChannel : IAsyncDisposable
{
    Task<AgentPushConnectResult> ConnectAsync(AgentLocalIdentity identity, IAgentPushChannelListener listener, CancellationToken cancellationToken);

    /// <summary>Stops and releases the current connection, if any. No listener callback fires afterwards.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}
