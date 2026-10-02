namespace BackupPlatform.Agent.Core.Connectivity;

/// <summary>
/// Extension point for the capability set the agent advertises. The heartbeat carries only this opaque revision (at most
/// 64 characters), never the capabilities themselves.
/// </summary>
public interface ICapabilityRevisionSource
{
    /// <summary>The current revision, or null when the agent advertises no capabilities.</summary>
    string? GetRevision();
}

/// <summary>Default source: no capabilities exist yet.</summary>
public sealed class NullCapabilityRevisionSource : ICapabilityRevisionSource
{
    public string? GetRevision() => null;
}
