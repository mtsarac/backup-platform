using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Domain.Capabilities;

namespace BackupPlatform.Agents.Capabilities;

public enum CapabilitySyncStatus
{
    Synced = 1,
    InvalidRequest = 2,
    AgentNotFound = 3
}

public sealed record CapabilitySyncResult(CapabilitySyncStatus Status, string? InvalidField, string? Revision, bool Changed);

/// <summary>
/// Agent-facing capability sync. Trusts only the authenticated identity and the server clock: the request body can
/// never choose the agent or tenant, and the revision it claims must match the capabilities it carries.
/// </summary>
public sealed class AgentCapabilityService(IAgentCapabilityStore store, TimeProvider timeProvider)
{
    public const int MaximumCapabilities = 128;
    public const int MaximumIdentifierLength = 128;
    public const int MaximumMetadataEntries = 8;
    public const int MaximumMetadataKeyLength = 64;
    public const int MaximumMetadataValueLength = 256;

    private const string RevisionField = "Revision";
    private const string CapabilitiesField = "Capabilities";
    private const string IdentifierField = "Capabilities.Identifier";
    private const string SupportField = "Capabilities.Support";
    private const string MetadataField = "Capabilities.Metadata";

    /// <summary>
    /// True when the agent advertised a revision that is unknown or differs from the stored snapshot. An agent that
    /// advertises no revision is never asked to sync. At most one indexed read.
    /// </summary>
    public async Task<bool> IsSyncRequestedAsync(AgentIdentity caller, string? reportedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(reportedRevision))
        {
            return false;
        }
        var stored = await store.GetRevisionAsync(caller, cancellationToken).ConfigureAwait(false);
        return !string.Equals(stored, reportedRevision.Trim(), StringComparison.Ordinal);
    }

    public async Task<CapabilitySyncResult> SyncAsync(
        AgentIdentity caller, AgentCapabilitySyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (!TryValidate(request, out var capabilities, out var invalidField))
        {
            return new CapabilitySyncResult(CapabilitySyncStatus.InvalidRequest, invalidField, null, false);
        }
        var outcome = await store.ReplaceSnapshotAsync(caller, request.Revision, capabilities, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return outcome switch
        {
            ReplaceSnapshotOutcome.Replaced => new CapabilitySyncResult(CapabilitySyncStatus.Synced, null, request.Revision, true),
            ReplaceSnapshotOutcome.Unchanged => new CapabilitySyncResult(CapabilitySyncStatus.Synced, null, request.Revision, false),
            _ => new CapabilitySyncResult(CapabilitySyncStatus.AgentNotFound, null, null, false)
        };
    }

    private static bool TryValidate(
        AgentCapabilitySyncRequest request, out IReadOnlyCollection<Capability> capabilities, out string? invalidField)
    {
        capabilities = [];
        invalidField = null;
        // An empty snapshot has no row to carry its revision, so it cannot be stored; an agent without capabilities advertises none.
        if (request.Capabilities is not { Count: > 0 and <= MaximumCapabilities } reports || reports.Any(static report => report is null))
        {
            invalidField = CapabilitiesField;
            return false;
        }
        var parsed = new List<Capability>(reports.Count);
        foreach (var report in reports)
        {
            if (!TryParseIdentifier(report.Identifier, out var identifier))
            {
                invalidField = IdentifierField;
                return false;
            }
            if (!TryParseSupport(report.Support, out var support))
            {
                invalidField = SupportField;
                return false;
            }
            if (!IsValidMetadata(report.Metadata))
            {
                invalidField = MetadataField;
                return false;
            }
            parsed.Add(new Capability(identifier, support, metadata: report.Metadata));
        }
        try
        {
            _ = new CapabilitySet(parsed);
        }
        catch (ArgumentException)
        {
            invalidField = IdentifierField;
            return false;
        }
        if (request.Revision is not { Length: > 0 } revision
            || !string.Equals(revision, AgentCapabilityRevision.Compute(reports), StringComparison.Ordinal))
        {
            invalidField = RevisionField;
            return false;
        }
        capabilities = parsed;
        return true;
    }

    private static bool TryParseIdentifier(string? value, out CapabilityIdentifier identifier)
    {
        identifier = null!;
        if (value is not { Length: <= MaximumIdentifierLength })
        {
            return false;
        }
        try
        {
            identifier = new CapabilityIdentifier(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Accepts only the exact defined names; numeric text such as "1" is never a support value.</summary>
    private static bool TryParseSupport(string? value, out CapabilitySupport support)
    {
        support = default;
        foreach (var candidate in Enum.GetValues<CapabilitySupport>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.Ordinal))
            {
                support = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool IsValidMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return true;
        }
        if (metadata.Count > MaximumMetadataEntries)
        {
            return false;
        }
        foreach (var (key, value) in metadata)
        {
            if (!IsValidText(key, MaximumMetadataKeyLength) || !IsValidText(value, MaximumMetadataValueLength))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsValidText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);
}
