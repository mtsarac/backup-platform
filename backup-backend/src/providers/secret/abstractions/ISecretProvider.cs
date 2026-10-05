namespace BackupPlatform.SecretProviders.Abstractions;

/// <summary>A provider-local name, not secret material and not an arbitrary file path.</summary>
public sealed record SecretReference
{
    public SecretReference(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
        {
            throw new ArgumentException("The secret reference is invalid.", nameof(name));
        }
        Name = name;
    }

    public string Name { get; }
}

/// <summary>Resolved only for the operation that needs it. Never log or serialize its revealed value.</summary>
public sealed class SecretValue(string value)
{
    private readonly string _value = value ?? throw new ArgumentNullException(nameof(value));
    public string Reveal() => _value;
    public override string ToString() => "[redacted]";
}

/// <summary>On-demand resolution; consumers must not persist or cache credential material.</summary>
public interface ISecretProvider
{
    ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken cancellationToken);
}
