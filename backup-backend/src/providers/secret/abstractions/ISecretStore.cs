namespace BackupPlatform.SecretProviders.Abstractions;

/// <summary>The referenced secret does not exist (or cannot be decrypted). Never carries the name's value or any secret material.</summary>
public sealed class SecretUnavailableException : InvalidOperationException
{
    public SecretUnavailableException() : base("The referenced secret is unavailable.") { }
}

/// <summary>
/// A secret provider that can also be written to: the only door through which a credential value enters the platform. Callers keep the
/// <see cref="SecretReference"/> (a name) and nothing else; the value is resolved on demand through <see cref="ISecretProvider"/> for the one
/// operation that needs it. An implementation must never expose a stored value except through <see cref="ISecretProvider.ResolveAsync"/>.
/// </summary>
public interface ISecretStore : ISecretProvider
{
    /// <summary>Stores <paramref name="value"/> under <paramref name="reference"/>, replacing any earlier value.</summary>
    Task SetAsync(SecretReference reference, SecretValue value, CancellationToken cancellationToken);

    /// <summary>True when something was stored under the reference and is now gone. Deleting an unknown reference is not an error.</summary>
    Task<bool> DeleteAsync(SecretReference reference, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(SecretReference reference, CancellationToken cancellationToken);
}

/// <summary>
/// Where an encrypting <see cref="ISecretStore"/> keeps what it produced: opaque ciphertext by name. The repository never sees a plaintext
/// value and cannot decrypt anything, so a database dump of it discloses no credential.
/// </summary>
public interface ISecretCiphertextRepository
{
    Task<string?> GetAsync(string name, CancellationToken cancellationToken);

    Task SetAsync(string name, string ciphertext, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken);
}
