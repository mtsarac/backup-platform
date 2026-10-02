using BackupPlatform.Domain.Identity;
using Microsoft.AspNetCore.SignalR;

namespace BackupPlatform.Api.Push;

/// <summary>
/// Which live connection currently serves which agent, on this process only. Purely a delivery aid: nothing durable
/// or security relevant depends on it (liveness is persisted by heartbeats and every connection is authenticated
/// independently), and losing it on restart merely means agents reconnect. A connection identifier is never an identity.
/// </summary>
/// <remarks>
/// Policy: one live connection per agent. A newer connection replaces the older one, which is aborted, because the
/// usual cause is an agent restart or a network change leaving a half-open connection on the server; refusing the new
/// one would strand a healthy agent behind a dead socket. Two live agents sharing one credential therefore take the
/// connection from each other, throttled by their reconnect backoff and the connect rate limit.
/// </remarks>
public sealed class AgentConnectionRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<AgentId, Entry> _connections = [];

    public IReadOnlyCollection<AgentId> ConnectedAgents
    {
        get
        {
            lock (_gate)
            {
                return [.. _connections.Keys];
            }
        }
    }

    /// <summary>Registers the connection and aborts any previous one for the agent.</summary>
    public void Register(AgentId agentId, HubCallerContext connection)
    {
        Entry? replaced;
        lock (_gate)
        {
            _connections.TryGetValue(agentId, out replaced);
            _connections[agentId] = new Entry(connection.ConnectionId, connection);
        }
        replaced?.Connection.Abort();
    }

    /// <summary>Removes the mapping only if it still points at this connection, so a replaced connection cannot evict its successor.</summary>
    public void Unregister(AgentId agentId, string connectionId)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(agentId, out var entry) && entry.ConnectionId == connectionId)
            {
                _connections.Remove(agentId);
            }
        }
    }

    public string? FindConnectionId(AgentId agentId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(agentId, out var entry) ? entry.ConnectionId : null;
        }
    }

    /// <summary>Aborts the agent's connection if any. The mapping is removed when the disconnect callback runs.</summary>
    public bool Abort(AgentId agentId)
    {
        Entry? entry;
        lock (_gate)
        {
            _connections.TryGetValue(agentId, out entry);
        }
        entry?.Connection.Abort();
        return entry is not null;
    }

    private sealed record Entry(string ConnectionId, HubCallerContext Connection);
}
