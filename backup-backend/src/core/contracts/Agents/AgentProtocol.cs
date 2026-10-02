using BackupPlatform.Contracts.Protocol;

namespace BackupPlatform.Contracts.Agents;

/// <summary>The agent-backend wire protocol revision spoken by this build, independent of any software release version.</summary>
public static class AgentProtocol
{
    public static ProtocolNegotiationContract Current { get; } = new(
        new ProtocolVersion(1, 0),
        new ProtocolVersionRange(new ProtocolVersion(1, 0), new ProtocolVersion(1, 0)));
}
