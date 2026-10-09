using BackupPlatform.SecretProviders.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace BackupPlatform.SecretProviders.DataProtection;

/// <summary>
/// Encrypts every secret with ASP.NET Core Data Protection before it is handed to a <see cref="ISecretCiphertextRepository"/>. The protector is
/// derived per secret NAME, so a ciphertext copied onto another reference does not decrypt: moving rows between connections or tenants discloses
/// nothing and is detected as an unavailable secret. Key management (key ring location, rotation, optional key encryption at rest) is the
/// host's Data Protection configuration; this class never sees a key.
/// </summary>
public sealed class DataProtectionSecretStore(IDataProtectionProvider protection, ISecretCiphertextRepository repository) : ISecretStore
{
    private const string Purpose = "BackupPlatform.SecretProviders.DataProtection.v1";

    public async ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var ciphertext = await repository.GetAsync(reference.Name, cancellationToken).ConfigureAwait(false) ?? throw new SecretUnavailableException();
        try
        {
            return new SecretValue(Protector(reference).Unprotect(ciphertext));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // A wrong key ring, a tampered row or a ciphertext of another reference: all the same to the caller, and none worth echoing.
            throw new SecretUnavailableException();
        }
    }

    public Task SetAsync(SecretReference reference, SecretValue value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(value);
        return repository.SetAsync(reference.Name, Protector(reference).Protect(value.Reveal()), cancellationToken);
    }

    public Task<bool> DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return repository.DeleteAsync(reference.Name, cancellationToken);
    }

    public async Task<bool> ExistsAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return await repository.GetAsync(reference.Name, cancellationToken).ConfigureAwait(false) is not null;
    }

    private IDataProtector Protector(SecretReference reference) => protection.CreateProtector(Purpose, reference.Name);
}
