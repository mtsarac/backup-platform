using System.Text.Json;
using BackupPlatform.Domain.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.S3;
using BackupPlatform.StorageProviders.Smb;

namespace BackupPlatform.Api.Backups;

public sealed class NetworkStorageConnectionProviderFactory(IHttpClientFactory clients, IWindowsNetworkSession? windowsSession = null)
{
    public IStorageProvider Create(StorageConnection connection, ISecretProvider secrets)
    {
        var config = connection.Configuration;
        var tenant = connection.TenantId.Value;
        var id = connection.Id;
        if (connection.ProviderType == StorageProviderTypes.S3)
        {
            var options = new S3StorageOptions(new Uri(config["endpoint"]), config["bucket"], config["region"], tenant, id,
                config.GetValueOrDefault("objectLockMode"), int.Parse(config.GetValueOrDefault("retentionDays", "0"), System.Globalization.CultureInfo.InvariantCulture));
            return new S3StorageProvider(clients.CreateClient("network-storage"), options, async token =>
            {
                var fields = await ReadFieldsAsync(connection, secrets, token);
                return new(fields["accessKey"], fields["secretKey"], fields.GetValueOrDefault("sessionToken"));
            });
        }
        if (connection.ProviderType == StorageProviderTypes.Smb)
            return new SmbStorageProvider(new(config["host"], config["share"], tenant, id), async token =>
            {
                var fields = await ReadFieldsAsync(connection, secrets, token);
                return new(fields["username"], fields["password"], fields.GetValueOrDefault("domain"));
            }, windowsSession);
        throw new StorageException(StorageFailureKind.InvalidConfiguration, "The network storage provider is not supported.");
    }

    public static async ValueTask<Dictionary<string, string>> ReadFieldsAsync(StorageConnection connection, ISecretProvider secrets, CancellationToken token)
    {
        if (connection.SecretReference is not { } reference || !reference.StartsWith($"sc-{connection.Id:N}-", StringComparison.Ordinal))
            throw new StorageException(StorageFailureKind.AuthenticationFailed, "The storage credentials are unavailable.");
        try
        {
            var secret = await secrets.ResolveAsync(new SecretReference(reference), token);
            var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(secret.Reveal());
            var required = connection.ProviderType == StorageProviderTypes.S3 ? new[] { "accessKey", "secretKey" } : ["username", "password"];
            if (fields is null || required.Any(field => !fields.TryGetValue(field, out var value) || string.IsNullOrEmpty(value)))
                throw new InvalidOperationException();
            return fields;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new StorageException(StorageFailureKind.AuthenticationFailed, "The storage credentials could not be loaded."); }
    }
}
