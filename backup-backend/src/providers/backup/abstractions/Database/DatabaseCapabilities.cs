using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace BackupPlatform.BackupProviders.Abstractions.Database;

/// <summary>
/// Whether a database feature is available. Numeric values match <c>BackupPlatform.Domain.Capabilities.CapabilitySupport</c>
/// so the backend can map one to the other without a lookup table. Agent-side projects cannot reference the domain
/// project, which is why the provider layer carries its own small model.
/// </summary>
public enum DatabaseCapabilitySupport
{
    /// <summary>The available metadata does not establish the answer. Never treated as available.</summary>
    Unknown = 0,
    Supported = 1,
    Unsupported = 2
}

/// <summary>
/// A canonical capability name using the same grammar as the platform capability identifiers: at least two nonempty
/// dot-separated segments of ASCII lowercase letters, digits and hyphens (for example <c>mssql.backup.full</c>).
/// </summary>
public sealed record DatabaseCapabilityId
{
    public DatabaseCapabilityId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var segments = value.Split('.');
        if (segments.Length < 2
            || segments.Any(segment => segment.Length == 0
                || !segment.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            throw new ArgumentException(
                "Capability names need at least two nonempty dot-separated segments of ASCII lowercase letters, digits and hyphens.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// One evaluated capability. <see cref="ReasonCode"/> is machine-readable and stable; <see cref="Explanation"/> is a
/// fixed human-readable sentence that never embeds server-supplied text.
/// </summary>
public sealed record DatabaseCapability
{
    public DatabaseCapability(DatabaseCapabilityId id, DatabaseCapabilitySupport support, string reasonCode, string explanation)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Enum.IsDefined(support))
        {
            throw new ArgumentOutOfRangeException(nameof(support), support, "Unknown capability support value.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        Id = id;
        Support = support;
        ReasonCode = reasonCode;
        Explanation = explanation;
    }

    public DatabaseCapabilityId Id { get; }
    public DatabaseCapabilitySupport Support { get; }
    public string ReasonCode { get; }
    public string Explanation { get; }
}

/// <summary>An immutable snapshot containing at most one capability per identifier.</summary>
public sealed class DatabaseCapabilitySet : IReadOnlyCollection<DatabaseCapability>
{
    private readonly Dictionary<DatabaseCapabilityId, DatabaseCapability> _capabilities = [];

    public DatabaseCapabilitySet(IEnumerable<DatabaseCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        foreach (var capability in capabilities)
        {
            if (capability is null)
            {
                throw new ArgumentException("A capability set must not contain null entries.", nameof(capabilities));
            }

            if (!_capabilities.TryAdd(capability.Id, capability))
            {
                throw new ArgumentException("A capability set must not contain duplicate identifiers.", nameof(capabilities));
            }
        }
    }

    public static DatabaseCapabilitySet Empty { get; } = new([]);

    public int Count => _capabilities.Count;

    public bool TryGet(DatabaseCapabilityId id, [NotNullWhen(true)] out DatabaseCapability? capability)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _capabilities.TryGetValue(id, out capability);
    }

    /// <summary>The support level for <paramref name="id"/>; identifiers that were never evaluated are <see cref="DatabaseCapabilitySupport.Unknown"/>.</summary>
    public DatabaseCapabilitySupport SupportOf(DatabaseCapabilityId id) =>
        TryGet(id, out var capability) ? capability.Support : DatabaseCapabilitySupport.Unknown;

    public IEnumerator<DatabaseCapability> GetEnumerator() => _capabilities.Values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
