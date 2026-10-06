using BackupPlatform.Agent.Core.Identity;

namespace BackupPlatform.Agent.Core.Connectivity;

/// <summary>
/// The capability set the agent advertises. The heartbeat carries only the opaque revision (at most 64 characters),
/// never the capabilities themselves; the full set is sent by <see cref="SyncAsync"/> only when the backend answers a
/// heartbeat with <c>capabilitySyncRequested</c>.
/// </summary>
public interface ICapabilityRevisionSource
{
    /// <summary>The current revision, or null when the agent advertises no capabilities.</summary>
    string? GetRevision();

    /// <summary>
    /// Sends the full capability snapshot to the backend. Returns true when the backend accepted it. A failure is not
    /// retried by the caller: the backend keeps requesting a sync on the following heartbeats. Implementations must log
    /// failures without secrets and throw only for cancellation.
    /// </summary>
    Task<bool> SyncAsync(AgentLocalIdentity identity, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>Source of an agent that advertises no capabilities.</summary>
public sealed class NullCapabilityRevisionSource : ICapabilityRevisionSource
{
    public string? GetRevision() => null;
}
