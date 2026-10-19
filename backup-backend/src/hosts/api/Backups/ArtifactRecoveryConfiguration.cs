using BackupPlatform.Application.Backups;
using BackupPlatform.Infrastructure.Backups;

namespace BackupPlatform.Api.Backups;

/// <summary>
/// Loads the platform recovery recipient from configuration (<c>Encryption:PlatformRecoveryPublicKeyPem</c>, env <c>Encryption__PlatformRecoveryPublicKeyPem</c>). Only a PUBLIC key
/// is accepted: the recovery private key is Shamir-shared offline and never reaches the backend, and a PEM carrying a private key fails startup instead of being used.
/// Absent means "no platform recovery recipient": artifacts are then sealed to the tenant key (and the tenant's own recovery key) only.
/// </summary>
public static class ArtifactRecoveryConfiguration
{
    public const string PemKey = "Encryption:PlatformRecoveryPublicKeyPem";
    public const string VersionKey = "Encryption:PlatformRecoveryKeyVersion";

    /// <exception cref="InvalidOperationException">The configured key is not a usable RSA public key (RSA, at least 4096 bits).</exception>
    public static ArtifactRecoveryOptions Load(IConfiguration configuration)
    {
        var pem = configuration[PemKey];
        if (string.IsNullOrWhiteSpace(pem)) return new ArtifactRecoveryOptions(null);
        var version = configuration.GetValue(VersionKey, 1);
        if (version < 1) throw new InvalidOperationException($"{VersionKey} must be at least 1.");
        try
        {
            // Environment variables cannot hold a multi-line value in every deployment format; a literal \n is accepted as a line break.
            var (spki, keyId) = RecoveryPublicKeyParser.Parse(pem.Replace("\\n", "\n"));
            return new ArtifactRecoveryOptions(new PlatformRecoveryKey(version, keyId, spki));
        }
        catch (InvalidRecoveryKeyException exception)
        {
            // The code names the problem; the key itself is never part of the message.
            throw new InvalidOperationException($"{PemKey} is not a usable recovery public key ({exception.Code}).");
        }
    }
}
