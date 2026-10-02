namespace BackupPlatform.Domain.Capabilities;

/// <summary>
/// A canonical, case-sensitive capability name with grammar
/// <c>[a-z0-9-]+(\.[a-z0-9-]+)+</c>: at least two nonempty dot-separated
/// segments containing only ASCII lowercase letters, digits, and hyphens.
/// Names are validated, never trimmed or normalized.
/// </summary>
public sealed record CapabilityIdentifier
{
    public CapabilityIdentifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var hasNamespace = false;
        var segmentLength = 0;
        foreach (var character in value)
        {
            if (character == '.')
            {
                if (segmentLength == 0)
                {
                    throw new ArgumentException("Capability name segments must not be empty.", nameof(value));
                }

                hasNamespace = true;
                segmentLength = 0;
                continue;
            }

            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                throw new ArgumentException("Capability names must contain only ASCII lowercase letters, digits, hyphens, and segment separators.", nameof(value));
            }

            segmentLength++;
        }

        if (!hasNamespace || segmentLength == 0)
        {
            throw new ArgumentException("Capability names must have at least two nonempty dot-separated segments.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
