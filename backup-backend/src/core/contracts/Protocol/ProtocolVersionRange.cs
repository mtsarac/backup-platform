namespace BackupPlatform.Contracts.Protocol;

/// <summary>
/// Declares an inclusive compatibility window within one protocol major version.
/// This data primitive does not negotiate a version or define runtime compatibility policy.
/// </summary>
public sealed record ProtocolVersionRange
{
    public ProtocolVersionRange(ProtocolVersion minimum, ProtocolVersion maximum)
    {
        ArgumentNullException.ThrowIfNull(minimum);
        ArgumentNullException.ThrowIfNull(maximum);
        if (minimum.Major != maximum.Major)
        {
            throw new ArgumentException("A protocol compatibility window must use a single major version.", nameof(maximum));
        }
        if (minimum.CompareTo(maximum) > 0)
        {
            throw new ArgumentException("The maximum protocol version cannot precede the minimum.", nameof(maximum));
        }
        Minimum = minimum;
        Maximum = maximum;
    }

    public ProtocolVersion Minimum { get; }

    public ProtocolVersion Maximum { get; }

    public bool Contains(ProtocolVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Major == Minimum.Major
            && version.Minor >= Minimum.Minor
            && version.Minor <= Maximum.Minor;
    }
}
