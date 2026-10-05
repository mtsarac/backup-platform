namespace BackupPlatform.StorageProviders.Abstractions;

/// <summary>Identifies a storage provider kind (for example <c>local</c> or <c>central</c>): ASCII lowercase letters, digits and hyphens.</summary>
public sealed record StorageProviderId
{
    public StorageProviderId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value.Length > 32 || !value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            throw new ArgumentException("Storage provider identifiers are 1..32 ASCII lowercase letters, digits and hyphens.", nameof(value));
        }

        Value = value;
    }

    public static StorageProviderId Local { get; } = new("local");
    public static StorageProviderId Central { get; } = new("central");

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// A provider-relative object name such as <c>tenant-a/mssql/sales/sales_full.bak</c>. It is a logical key, never a file system
/// path: segments are separated by <c>/</c> only and the grammar is deliberately narrow (ASCII letters, digits, <c>.</c>, <c>_</c>,
/// <c>-</c>, <c>=</c>, <c>+</c>, <c>@</c>). A key therefore cannot escape a storage root (no <c>..</c>, rooted keys, drive letters,
/// backslashes or alternate data streams) and cannot mean different things on different operating systems (Windows reserved
/// device names such as <c>CON</c> and trailing dots are refused).
/// </summary>
public sealed record StorageObjectKey
{
    public const int MaxLength = 512;
    public const int MaxSegmentLength = 128;

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public StorageObjectKey(string value)
    {
        Value = Validate(value);
    }

    public string Value { get; }

    public IReadOnlyList<string> Segments => Value.Split('/');

    public string FileName => Value[(Value.LastIndexOf('/') + 1)..];

    public static bool TryCreate(string? value, out StorageObjectKey? key)
    {
        try
        {
            key = value is null ? null : new StorageObjectKey(value);
        }
        catch (ArgumentException)
        {
            key = null;
        }

        return key is not null;
    }

    /// <summary>Appends one validated segment, for example a file name to a directory-like prefix.</summary>
    public StorageObjectKey Combine(string segment) => new($"{Value}/{segment}");

    public override string ToString() => Value;

    private static string Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value.Length > MaxLength)
        {
            throw new ArgumentException($"Storage keys are 1..{MaxLength} characters.", nameof(value));
        }

        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment.Length > MaxSegmentLength)
            {
                throw new ArgumentException($"Storage key segments are 1..{MaxSegmentLength} characters; empty segments and rooted keys are not allowed.", nameof(value));
            }

            if (segment is "." or ".." || segment.EndsWith('.') || !segment.All(IsAllowed))
            {
                throw new ArgumentException("Storage key segments may only contain ASCII letters, digits, '.', '_', '-', '=', '+' and '@', must not be '.' or '..' and must not end with '.'.", nameof(value));
            }

            var stem = segment.Split('.')[0];
            if (ReservedWindowsNames.Contains(stem))
            {
                throw new ArgumentException("Storage key segments must not use reserved device names.", nameof(value));
            }
        }

        return value;
    }

    private static bool IsAllowed(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-' or '=' or '+' or '@';
}

/// <summary>Where a stored object lives, expressed as provider plus key. It is stable across hosts and never contains a file system path.</summary>
public sealed record StorageLocation(StorageProviderId Provider, StorageObjectKey Key)
{
    public override string ToString() => $"{Provider}:{Key}";
}
