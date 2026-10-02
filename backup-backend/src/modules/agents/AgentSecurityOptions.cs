using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Protocol;

namespace BackupPlatform.Agents;

/// <summary>Lifetimes and the protocol window enforced by the agent security services.</summary>
public sealed class AgentSecurityOptions
{
    public TimeSpan DefaultEnrollmentTokenLifetime { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan MinimumEnrollmentTokenLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaximumEnrollmentTokenLifetime { get; init; } = TimeSpan.FromDays(7);
    /// <summary>How long an agent has to persist and confirm a rotated credential before it must begin again.</summary>
    public TimeSpan PendingCredentialLifetime { get; init; } = TimeSpan.FromHours(24);
    public ProtocolNegotiationContract Protocol { get; init; } = AgentProtocol.Current;
}
