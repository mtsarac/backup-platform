using System.Collections.ObjectModel;

namespace BackupPlatform.Domain.Capabilities;

/// <summary>An immutable capability description, independent of any provider catalog.</summary>
public sealed class Capability
{
    public Capability(
        CapabilityIdentifier identifier,
        CapabilitySupport support,
        Version? version = null,
        IEnumerable<KeyValuePair<string, string>>? constraints = null,
        IEnumerable<KeyValuePair<string, string>>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (!Enum.IsDefined(support))
        {
            throw new ArgumentOutOfRangeException(nameof(support), support, "Unknown capability support value.");
        }

        Identifier = identifier;
        Support = support;
        Version = version;
        Constraints = Snapshot(constraints, nameof(constraints));
        Metadata = Snapshot(metadata, nameof(metadata));
    }

    public CapabilityIdentifier Identifier { get; }
    public CapabilitySupport Support { get; }
    public Version? Version { get; }
    public IReadOnlyDictionary<string, string> Constraints { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    private static IReadOnlyDictionary<string, string> Snapshot(
        IEnumerable<KeyValuePair<string, string>>? entries,
        string parameterName)
    {
        if (entries is null)
        {
            return ReadOnlyDictionary<string, string>.Empty;
        }

        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                throw new ArgumentException("Capability dictionary keys must not be null or blank.", parameterName);
            }

            if (entry.Value is null)
            {
                throw new ArgumentException("Capability dictionary values must not be null.", parameterName);
            }

            if (!snapshot.TryAdd(entry.Key, entry.Value))
            {
                throw new ArgumentException("Capability dictionary keys must be unique using ordinal comparison.", parameterName);
            }
        }

        return snapshot.Count == 0
            ? ReadOnlyDictionary<string, string>.Empty
            : new ReadOnlyDictionary<string, string>(snapshot);
    }
}
