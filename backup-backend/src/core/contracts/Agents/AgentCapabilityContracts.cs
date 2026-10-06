using System.Security.Cryptography;
using System.Text;

namespace BackupPlatform.Contracts.Agents;

/// <summary>Names of <see cref="AgentCapabilityReport.Support"/>; they mirror the domain's capability support values.</summary>
public static class AgentCapabilitySupportNames
{
    public const string Unknown = "Unknown";
    public const string Supported = "Supported";
    public const string Unsupported = "Unsupported";
}

/// <summary>
/// One capability the agent reports. <see cref="Support"/> is one of <see cref="AgentCapabilitySupportNames"/>; the
/// metadata is small, free-form, non-secret descriptive text. Wire JSON uses camelCase.
/// </summary>
public sealed record AgentCapabilityReport
{
    public string Identifier { get; init; } = string.Empty;
    public string Support { get; init; } = AgentCapabilitySupportNames.Unknown;
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Full capability snapshot, sent only on <c>PUT /api/agent/capabilities</c> after a heartbeat answered
/// <c>capabilitySyncRequested</c>. <see cref="Revision"/> must equal <see cref="AgentCapabilityRevision.Compute"/> of
/// <see cref="Capabilities"/>; the authenticated principal, never this body, identifies the agent and tenant.
/// </summary>
public sealed record AgentCapabilitySyncRequest
{
    public string Revision { get; init; } = string.Empty;
    public IReadOnlyList<AgentCapabilityReport> Capabilities { get; init; } = [];
}

public sealed record AgentCapabilitySyncResponse
{
    /// <summary>The revision now stored for the agent.</summary>
    public string Revision { get; init; } = string.Empty;
    /// <summary>False when the stored snapshot already had this revision and nothing was written.</summary>
    public bool Changed { get; init; }
}

/// <summary>
/// Deterministic revision of a capability set, shared by the agent (which advertises it) and the backend (which verifies
/// it), so both sides always compute the same value from the same capabilities regardless of order.
/// </summary>
public static class AgentCapabilityRevision
{
    /// <summary>Prefix that versions the canonical form; a change to the canonical text must introduce a new prefix.</summary>
    public const string Prefix = "c1-";

    /// <summary>Longest revision this code produces; within the 64 characters a heartbeat may carry.</summary>
    public const int Length = 51;

    private const int HashBytes = 24;

    public static string Compute(IEnumerable<AgentCapabilityReport> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var ordered = capabilities.OrderBy(static capability => capability.Identifier, StringComparer.Ordinal).ToList();
        var canonical = new StringBuilder();
        foreach (var capability in ordered)
        {
            Append(canonical, capability.Identifier);
            Append(canonical, capability.Support);
            var metadata = capability.Metadata is { Count: > 0 } entries
                ? entries.OrderBy(static entry => entry.Key, StringComparer.Ordinal).ToList()
                : [];
            canonical.Append(metadata.Count).Append(';');
            foreach (var entry in metadata)
            {
                Append(canonical, entry.Key);
                Append(canonical, entry.Value);
            }
            canonical.Append('\n');
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Prefix + Convert.ToHexStringLower(hash.AsSpan(0, HashBytes));
    }

    // Length-prefixed fields keep the canonical text unambiguous whatever characters the values contain.
    private static void Append(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length).Append(':').Append(value).Append(';');
    }
}
