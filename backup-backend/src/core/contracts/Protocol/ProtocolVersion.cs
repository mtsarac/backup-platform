using System.Globalization;

namespace BackupPlatform.Contracts.Protocol;

/// <summary>
/// Identifies a wire protocol revision independently of an application's release version.
/// </summary>
public sealed record ProtocolVersion : IComparable<ProtocolVersion>
{
    public ProtocolVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        Major = major;
        Minor = minor;
    }

    public int Major { get; }

    public int Minor { get; }

    /// <summary>
    /// Orders versions by major, then minor. Any version sorts after null.
    /// </summary>
    public int CompareTo(ProtocolVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        int majorComparison = Major.CompareTo(other.Major);
        return majorComparison != 0 ? majorComparison : Minor.CompareTo(other.Minor);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}
