using System.Globalization;

namespace BackupPlatform.BackupProviders.MSSQL.Discovery;

/// <summary>
/// The SQL Server software version (<c>SERVERPROPERTY('ProductVersion')</c>): major.minor.build[.revision]. This is the
/// engine's own version and has nothing to do with a database compatibility level. Future major versions are representable
/// as-is; rules compare numerically and never enumerate known releases.
/// </summary>
public readonly record struct SqlServerVersion : IComparable<SqlServerVersion>
{
    public SqlServerVersion(int major, int minor, int build, int? revision = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(build);
        if (revision is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "Revision must not be negative.");
        }

        Major = major;
        Minor = minor;
        Build = build;
        Revision = revision;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Build { get; }

    /// <summary>The fourth component, when the server reported one.</summary>
    public int? Revision { get; }

    /// <summary>
    /// The marketing release name for versions this code knows about (for example "SQL Server 2022"), otherwise null.
    /// Informational only; never used by capability rules.
    /// </summary>
    public string? ReleaseName => (Major, Minor) switch
    {
        (9, _) => "SQL Server 2005",
        (10, >= 50) => "SQL Server 2008 R2",
        (10, _) => "SQL Server 2008",
        (11, _) => "SQL Server 2012",
        (12, _) => "SQL Server 2014",
        (13, _) => "SQL Server 2016",
        (14, _) => "SQL Server 2017",
        (15, _) => "SQL Server 2019",
        (16, _) => "SQL Server 2022",
        (17, _) => "SQL Server 2025",
        _ => null
    };

    /// <summary>Parses <c>major.minor.build</c> with an optional fourth <c>.revision</c>; each component is digits only.</summary>
    public static bool TryParse(string? text, out SqlServerVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('.');
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        var numbers = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return false;
            }
        }

        version = new SqlServerVersion(numbers[0], numbers[1], numbers[2], numbers.Length == 4 ? numbers[3] : null);
        return true;
    }

    public bool IsAtLeast(int major, int minor = 0) => Major > major || (Major == major && Minor >= minor);

    public int CompareTo(SqlServerVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Build.CompareTo(other.Build);
        if (result != 0)
        {
            return result;
        }

        // A missing revision sorts before any reported one, matching record equality (null differs from 0).
        return Nullable.Compare(Revision, other.Revision);
    }

    public static bool operator <(SqlServerVersion left, SqlServerVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SqlServerVersion left, SqlServerVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SqlServerVersion left, SqlServerVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SqlServerVersion left, SqlServerVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        Revision is { } revision ? $"{Major}.{Minor}.{Build}.{revision}" : $"{Major}.{Minor}.{Build}";
}

/// <summary>The database compatibility level (for example 160). A property of a database, not of the server software version.</summary>
public readonly record struct SqlServerCompatibilityLevel(int Value)
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
