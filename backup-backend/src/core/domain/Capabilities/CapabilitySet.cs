using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace BackupPlatform.Domain.Capabilities;

/// <summary>An immutable snapshot containing at most one capability per identifier.</summary>
public sealed class CapabilitySet : IReadOnlyCollection<Capability>
{
    private readonly Dictionary<CapabilityIdentifier, Capability> _capabilities;

    public CapabilitySet(IEnumerable<Capability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = new Dictionary<CapabilityIdentifier, Capability>();
        foreach (var capability in capabilities)
        {
            if (capability is null)
            {
                throw new ArgumentException("A capability set must not contain null entries.", nameof(capabilities));
            }

            if (!_capabilities.TryAdd(capability.Identifier, capability))
            {
                throw new ArgumentException("A capability set must not contain duplicate identifiers.", nameof(capabilities));
            }
        }
    }

    public static CapabilitySet Empty { get; } = new([]);

    public int Count => _capabilities.Count;

    public bool TryGet(CapabilityIdentifier identifier, [NotNullWhen(true)] out Capability? capability)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return _capabilities.TryGetValue(identifier, out capability);
    }

    public IEnumerator<Capability> GetEnumerator() => _capabilities.Values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
