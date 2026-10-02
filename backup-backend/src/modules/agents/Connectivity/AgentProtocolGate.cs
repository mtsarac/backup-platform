using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Protocol;

namespace BackupPlatform.Agents.Connectivity;

public enum AgentProtocolOutcome
{
    Compatible = 1,
    Missing = 2,
    Malformed = 3,
    AgentTooOld = 4,
    AgentTooNew = 5
}

public sealed record AgentProtocolDecision(AgentProtocolOutcome Outcome, ProtocolVersion? Version)
{
    public bool IsCompatible => Outcome == AgentProtocolOutcome.Compatible;

    /// <summary>The error code and action a rejected agent receives; null when compatible.</summary>
    public (string Error, string? Action)? ToError() => Outcome switch
    {
        AgentProtocolOutcome.Compatible => null,
        AgentProtocolOutcome.Missing => (AgentErrorCodes.ProtocolMissing, null),
        AgentProtocolOutcome.Malformed => (AgentErrorCodes.InvalidRequest, null),
        AgentProtocolOutcome.AgentTooOld => (AgentErrorCodes.ProtocolIncompatible, AgentErrorActions.UpgradeAgent),
        _ => (AgentErrorCodes.ProtocolIncompatible, AgentErrorActions.WaitForServerUpgrade)
    };
}

/// <summary>
/// Applies the backend's protocol window to a protocol version declared by an agent after enrollment, reusing the
/// same window enrollment enforces. The agent's software release version plays no part in this decision.
/// </summary>
public sealed class AgentProtocolGate(AgentSecurityOptions options)
{
    public AgentProtocolDecision Evaluate(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return new AgentProtocolDecision(AgentProtocolOutcome.Missing, null);
        }
        if (!ProtocolVersion.TryParse(declared, out var version))
        {
            return new AgentProtocolDecision(AgentProtocolOutcome.Malformed, null);
        }
        return Evaluate(version);
    }

    public AgentProtocolDecision Evaluate(ProtocolVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var window = options.Protocol.SupportedVersions;
        if (window.Contains(version))
        {
            return new AgentProtocolDecision(AgentProtocolOutcome.Compatible, version);
        }
        return new AgentProtocolDecision(
            version.CompareTo(window.Minimum) < 0 ? AgentProtocolOutcome.AgentTooOld : AgentProtocolOutcome.AgentTooNew, version);
    }

    public bool IsSupported(int major, int minor) => options.Protocol.SupportedVersions.Contains(new ProtocolVersion(major, minor));
}
