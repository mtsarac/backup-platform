using System.Net.Http.Headers;
using System.Text.Json;
using BackupPlatform.Domain.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Storage;

namespace BackupPlatform.Api.Backups;

public sealed class GoogleDriveCapacityClient(
    HttpClient httpClient,
    GoogleDriveConnectionProviderFactory factory,
    ISecretProvider secrets) : IGoogleDriveCapacityClient
{
    private const string AboutEndpoint = "https://www.googleapis.com/drive/v3/about?fields=storageQuota";

    public async Task<(long? TotalBytes, long? UsedBytes, long? FreeBytes)> QueryCapacityAsync(
        StorageConnection connection, CancellationToken cancellationToken)
    {
        var token = await factory.GetAccessTokenAsync(connection, secrets, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, AboutEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        long? total = null;
        long? used = null;
        long? free = null;

        if (doc.RootElement.TryGetProperty("storageQuota", out var quota))
        {
            if (quota.TryGetProperty("limit", out var limitProp) && limitProp.ValueKind == JsonValueKind.String
                && long.TryParse(limitProp.GetString(), out var limitVal))
            {
                total = limitVal;
            }
            if (quota.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.String
                && long.TryParse(usageProp.GetString(), out var usageVal))
            {
                used = usageVal;
            }
            if (total.HasValue && used.HasValue)
            {
                free = Math.Max(0, total.Value - used.Value);
            }
        }

        return (total, used, free);
    }
}
