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

    /// <summary>Strictly parses the <see cref="ToString"/> form: <c>major.minor</c>, ASCII digits only, major at least 1.</summary>
    public static bool TryParse(string? value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProtocolVersion? version)
    {
        version = null;
        if (value is null)
        {
            return false;
        }
        var separator = value.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0
            || !int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || major < 1)
        {
            return false;
        }
        version = new ProtocolVersion(major, minor);
        return true;
    }
}
