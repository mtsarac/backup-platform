using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.S3;

namespace BackupPlatform.Agent.Transport;

/// <summary>
/// Aborts a journaled S3 multipart upload of a finished attempt. The lease-bound task provider cannot: its lease is dead, so the credential comes from the backend's
/// terminal-attempt-only <c>abort-access</c> route. Other providers keep no remote state that outlives the attempt.
/// </summary>
public sealed class HttpRetainedUploadAborter(HttpClient http, AgentBackendEndpoint endpoint, IAgentCredentialStore identities) : IRetainedUploadAborter
{
    public async Task AbortAsync(Guid attemptId, RetainedUpload upload, CancellationToken cancellationToken)
    {
        if (upload.StorageProvider != "s3") return;
        if (!ManagedBackupLayout.TryParseStorageKey(upload.Key, out var layout) || layout is null || layout.AttemptId != attemptId)
            throw new StorageException(StorageFailureKind.InvalidConfiguration, "The journaled upload key does not belong to the attempt.");
        var identity = await identities.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new StorageException(StorageFailureKind.AccessDenied, "The agent identity is unavailable.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUri, $"api/agent/tasks/{attemptId:D}/storage/abort-access"));
        request.Headers.TryAddWithoutValidation("Authorization", identity.Current.ToAuthorizationHeaderValue());
        request.Headers.TryAddWithoutValidation(AgentProtocolHeader.Name, AgentProtocol.Current.Version.ToString());
        request.Headers.TryAddWithoutValidation(TaskStorageProtocol.DestinationHeader, upload.DestinationId.ToString("D"));
        TaskStorageAccessResponse access;
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new StorageException(StorageFailureKind.AccessDenied, "The backend refused abort access.");
            access = await response.Content.ReadFromJsonAsync<TaskStorageAccessResponse>(JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false)
                ?? throw new StorageException(StorageFailureKind.Io, "The abort access response is invalid.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            throw new StorageException(StorageFailureKind.NetworkUnavailable, "The backend is unavailable.", exception);
        }
        var config = access.Configuration ?? throw new StorageException(StorageFailureKind.InvalidConfiguration, "Missing network storage configuration.");
        var options = new S3StorageOptions(new Uri(config["endpoint"]), config["bucket"], config["region"], layout.TenantId, upload.DestinationId,
            config.GetValueOrDefault("objectLockMode"), int.Parse(config.GetValueOrDefault("retentionDays", "0"), System.Globalization.CultureInfo.InvariantCulture));
        var credential = new S3Credential(access.AccessKey ?? "", access.SecretKey ?? "", access.SessionToken);
        await new S3StorageProvider(http, options, _ => ValueTask.FromResult(credential))
            .AbortTransferAsync(new StorageObjectKey(upload.Key), upload.ProviderState, cancellationToken).ConfigureAwait(false);
    }
}
