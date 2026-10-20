using System.Globalization;
using System.Text.Json;
using BackupPlatform.Domain.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Sftp;

namespace BackupPlatform.Api.Backups;

/// <summary>Builds from the supplied definition, including uncommitted test candidates. Credentials are read only when an operation connects.</summary>
public sealed class SftpConnectionProviderFactory(SftpNetworkOptions network)
{
    private readonly SftpNetworkOptions _network = network ?? throw new ArgumentNullException(nameof(network));

    public IStorageProvider Create(StorageConnection connection, ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(secrets);
        if (connection.ProviderType != StorageProviderTypes.Sftp || connection.Id == Guid.Empty || connection.TenantId.Value == Guid.Empty)
            throw InvalidConfiguration();
        try
        {
            var configuration = connection.Configuration;
            var options = new SftpStorageOptions(
                configuration["host"], int.Parse(configuration.GetValueOrDefault("port", "22"), NumberStyles.None, CultureInfo.InvariantCulture),
                configuration["username"], configuration["authenticationType"], configuration["remoteBaseDirectory"],
                configuration["hostKeyFingerprint"], connection.TenantId.Value, connection.Id);
            var reference = connection.SecretReference;
            if (reference is null || !reference.StartsWith($"sc-{connection.Id:N}-", StringComparison.Ordinal))
                throw InvalidConfiguration();
            var secretReference = new SecretReference(reference);
            return new SftpStorageProvider(options, token => ReadCredentialAsync(secrets, secretReference, options.AuthenticationType, token), _network);
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw InvalidConfiguration();
        }
    }

    internal static async ValueTask<SftpCredential> ReadCredentialAsync(
        ISecretProvider secrets, SecretReference reference, string authenticationType, CancellationToken cancellationToken)
    {
        try
        {
            var value = await secrets.ResolveAsync(reference, cancellationToken);
            var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(value.Reveal());
            if (fields is null) throw InvalidCredentials();
            fields.TryGetValue("password", out var password);
            fields.TryGetValue("privateKey", out var privateKey);
            fields.TryGetValue("passphrase", out var passphrase);
            if (authenticationType == "password"
                ? string.IsNullOrEmpty(password) || privateKey is not null || passphrase is not null
                : string.IsNullOrEmpty(privateKey) || password is not null)
                throw InvalidCredentials();
            return new SftpCredential(password, privateKey, passphrase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Secret stores and JSON parsers can include credential material in their exception messages or inner exceptions.
            throw InvalidCredentials();
        }
    }

    private static StorageException InvalidConfiguration() => new(StorageFailureKind.InvalidConfiguration, "The SFTP storage connection is not configured correctly.");
    private static StorageException InvalidCredentials() => new(StorageFailureKind.AccessDenied, "The SFTP storage credentials could not be loaded.");
}
